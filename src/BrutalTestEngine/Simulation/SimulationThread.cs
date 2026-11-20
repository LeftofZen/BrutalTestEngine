using System.Diagnostics;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Input;
using BrutalTestEngine.Threading;

namespace BrutalTestEngine.Simulation;

/// <summary>
/// Owns the simulation thread. It runs a fixed-timestep tick completely independently of the render loop
/// and publishes a <see cref="SimulationSnapshot"/> after each tick.
/// </summary>
/// <remarks>
/// <para>
/// A fixed timestep is the core reason the simulation is separated from rendering: physics and game logic
/// must advance in deterministic, equal-sized steps, while the render loop runs as fast as the display
/// allows. Coupling them would make simulation behaviour depend on frame rate.
/// </para>
/// <para>
/// This thread is also the sole consumer of the bus's simulation-targeted commands and drains them at the
/// top of every iteration, before the ticks for that iteration run. That placement is deliberate: a tick
/// is the simulation's unit of time, so draining at the tick boundary means every command takes effect on
/// an exact tick, in sequence, no matter when it arrived or which transport carried it. That is what makes
/// a recorded command stream replayable — and replayable command streams are the foundation the sharded
/// world will be built on.
/// </para>
/// <para>
/// The thread accumulates real elapsed time and consumes it in fixed steps. If the machine cannot keep up
/// it drops ticks rather than spiralling (the classic "spiral of death"), and reports the drops so the
/// debug UI can show them. Pausing, single-stepping by an exact tick count and resetting are commands too,
/// so they travel the same path as every other intent.
/// </para>
/// <para>
/// Snapshots are published through a <see cref="TripleBuffer{T}"/>, so the render thread always reads the
/// newest complete state and neither thread ever waits for the other.
/// </para>
/// </remarks>
public sealed class SimulationThread : IDisposable, ICommandHandler
{
    /// <summary>
    /// How many commands one iteration may apply. A source that floods the bus must not be able to stretch
    /// a tick without bound; whatever is left over is applied on the next iteration, still in order.
    /// </summary>
    private const int MaxCommandsPerDrain = 1024;

    private const int PollTimeoutMilliseconds = 4;
    private const float RotationSpeedRadiansPerSecond = 1.0f;
    private const float SteeredRotationSpeedRadiansPerSecond = 2.0f;
    private const float ScaleAmplitude = 0.25f;
    private const double TicksPerSecondWindowSeconds = 0.5;

    private readonly CommandBus _bus;
    private readonly TripleBuffer<SimulationSnapshot> _snapshots = new(() => SimulationSnapshot.Empty);
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Func<InputSnapshot> _readInput;
    private readonly Func<long> _readFrameCount;
    private readonly Func<long> _readInputCommandCount;
    private readonly Func<long> _readIoCount;
    private readonly Func<long> _readNetworkCount;
    private readonly Func<int> _readPendingEngineCommands;
    private readonly ThreadMetrics _tickMetrics;
    private readonly ThreadMetrics _cycleMetrics;
    private readonly ThreadMetrics _drainMetrics;

    // Written by the main thread in response to a tick-rate command, read by the simulation thread.
    private double _fixedDeltaSeconds;
    private double _maxFrameSeconds;

    private long _tick;
    private long _droppedTicks;
    private long _ticksExecuted;
    private long _ticksInWindow;
    private long _commandsApplied;
    private long _pendingSteps;
    private int _paused;
    private double _elapsedSeconds;
    private double _ticksPerSecond;
    private double _ticksPerSecondWindowStart;
    private float _rotationRadians;
    private float _scale = 1.0f;
    private volatile bool _disposed;

    /// <param name="bus">The command bus this thread consumes simulation commands from.</param>
    /// <param name="ticksPerSecond">Target simulation rate, for example 60.</param>
    /// <param name="readInput">Reads the newest input snapshot from the input thread.</param>
    /// <param name="readFrameCount">Reads the render thread's presented frame count.</param>
    /// <param name="readInputCommandCount">Reads the input thread's applied command count.</param>
    /// <param name="readIoCount">Reads the IO thread's completed job count.</param>
    /// <param name="readNetworkCount">Reads the network thread's received message count.</param>
    /// <param name="readPendingEngineCommands">
    /// Reads how many engine commands are still waiting for the main thread to apply them.
    /// </param>
        /// <param name="timers">
        /// Where this thread records how long its tick, cycle and command drain take. Shared with every other
        /// thread so the debug panel can compare them side by side.
        /// </param>
    public SimulationThread(
        CommandBus bus,
        double ticksPerSecond,
        Func<InputSnapshot> readInput,
        Func<long> readFrameCount,
        Func<long> readInputCommandCount,
        Func<long> readIoCount,
        Func<long> readNetworkCount,
            Func<int> readPendingEngineCommands,
            ThreadTimerRegistry timers
    )
    {
        ArgumentNullException.ThrowIfNull(bus);
            ArgumentNullException.ThrowIfNull(timers);

        _bus = bus;
        SetTicksPerSecond(ticksPerSecond);

        _readInput = readInput;
        _readFrameCount = readFrameCount;
        _readInputCommandCount = readInputCommandCount;
        _readIoCount = readIoCount;
        _readNetworkCount = readNetworkCount;
        _readPendingEngineCommands = readPendingEngineCommands;

            // Resolved once so the hot loop records into a plain reference with no lookup.
            _tickMetrics = timers.Get(ThreadTimerRegistry.Keys.SimulationTick);
            _cycleMetrics = timers.Get(ThreadTimerRegistry.Keys.SimulationCycle);
            _drainMetrics = timers.Get(ThreadTimerRegistry.Keys.SimulationDrain);

        _thread = new Thread(Run)
        {
            Name = "Simulation",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>The fixed timestep in seconds.</summary>
    public double FixedDeltaSeconds => Volatile.Read(ref _fixedDeltaSeconds);

    /// <summary>Number of ticks executed so far.</summary>
    public long TickCount => Interlocked.Read(ref _ticksExecuted);

    /// <summary>Number of ticks dropped because the simulation could not keep up.</summary>
    public long DroppedTicks => Interlocked.Read(ref _droppedTicks);

    /// <summary>Number of simulation commands this thread has applied.</summary>
    public long CommandsApplied => Interlocked.Read(ref _commandsApplied);

    /// <summary>True while the passage of simulated time is halted by a pause command.</summary>
    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    /// <summary>
    /// Reads the newest simulation snapshot. The returned instance is owned by the simulation thread and
    /// stays valid until the next call, so copy out what is needed.
    /// </summary>
    public bool TryReadSnapshot(out SimulationSnapshot snapshot, out long version) =>
        _snapshots.TryRead(out snapshot, out version);

    /// <summary>
    /// Changes the fixed timestep. Safe to call from any thread; called by the main thread when it applies
    /// a tick-rate command.
    /// </summary>
    public void SetTicksPerSecond(double ticksPerSecond)
    {
        if (ticksPerSecond <= 0 || !double.IsFinite(ticksPerSecond))
            return;

        var delta = 1.0 / ticksPerSecond;

        // Cap the real time a single iteration may consume. Without this, a long stall (a debugger break, a
        // window drag) would queue up thousands of ticks and freeze the app.
        Volatile.Write(ref _maxFrameSeconds, delta * 8.0);
        Volatile.Write(ref _fixedDeltaSeconds, delta);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _cancellation.Dispose();
    }

    // Only simulation-targeted commands reach these handlers. Every other command type is admitted and
    // logged by the bus and routed to the thread that owns it.

    void ICommandHandler.Handle(StepSimulationCommand command) => Interlocked.Add(ref _pendingSteps, command.Ticks);

    void ICommandHandler.Handle(SetPausedCommand command) => Volatile.Write(ref _paused, command.Paused ? 1 : 0);

    void ICommandHandler.Handle(ResetSimulationCommand command)
    {
        _tick = 0;
        _elapsedSeconds = 0;
        _rotationRadians = 0;
        _scale = 1.0f;

        Interlocked.Exchange(ref _droppedTicks, 0);
        Interlocked.Exchange(ref _ticksInWindow, 0);
        Interlocked.Exchange(ref _pendingSteps, 0);
    }

    void ICommandHandler.Handle(QuitCommand command) => Ignore();

    void ICommandHandler.Handle(HelpCommand command) => Ignore();

    void ICommandHandler.Handle(SetWindowModeCommand command) => Ignore();

    void ICommandHandler.Handle(ToggleWindowModeCommand command) => Ignore();

    void ICommandHandler.Handle(SetTickRateCommand command) => Ignore();

    void ICommandHandler.Handle(InjectInputCommand command) => Ignore();

    void ICommandHandler.Handle(KeyCommand command) => Ignore();

    void ICommandHandler.Handle(MouseButtonCommand command) => Ignore();

    void ICommandHandler.Handle(CursorMoveCommand command) => Ignore();

    void ICommandHandler.Handle(ScrollCommand command) => Ignore();

    void ICommandHandler.Handle(TextInputCommand command) => Ignore();

    void ICommandHandler.Handle(FocusCommand command) => Ignore();

    void ICommandHandler.Handle(UnhandledCommand command) => Ignore();

    private static void Ignore()
    {
        // Nothing to do: the command is not addressed to this thread.
    }

    private void Run()
    {
        var token = _cancellation.Token;
        var clock = Stopwatch.StartNew();
        var previousSeconds = clock.Elapsed.TotalSeconds;
        var accumulator = 0.0;
        _ticksPerSecondWindowStart = previousSeconds;

        while (!token.IsCancellationRequested)
        {
                var cycleStartedAt = Stopwatch.GetTimestamp();

                var drainStartedAt = Stopwatch.GetTimestamp();
            _commandsApplied += _bus.Drain(CommandTarget.Simulation, this, MaxCommandsPerDrain);
                _drainMetrics.RecordSince(drainStartedAt);

            var ticked = false;

            if (IsPaused)
            {
                // While paused the accumulator is dropped, so resuming does not replay the paused interval
                // as a burst of catch-up ticks. Only a step command produces a tick.
                accumulator = 0;
                previousSeconds = clock.Elapsed.TotalSeconds;

                if (Interlocked.Read(ref _pendingSteps) > 0)
                {
                    Interlocked.Decrement(ref _pendingSteps);
                        TimedTick();
                    ticked = true;
                }
            }
            else
            {
                accumulator += ConsumeElapsedSeconds(clock, ref previousSeconds);

                var fixedDelta = FixedDeltaSeconds;
                while (accumulator >= fixedDelta && !IsPaused)
                {
                        TimedTick();
                    accumulator -= fixedDelta;
                    fixedDelta = FixedDeltaSeconds;
                    ticked = true;
                }

                // Pausing mid-batch must not carry the unspent accumulator into the paused state.
                if (IsPaused)
                    accumulator = 0;
            }

            if (ticked)
                PublishSnapshot();

            UpdateTicksPerSecond(clock.Elapsed.TotalSeconds);

                // Measured before the sleep below, so this is the loop's own work: the tick, the drain, the publish
                // and the bookkeeping. The sleep is deliberately excluded, because it is idle time by design and
                // folding it in would make the number just read back as the tick period.
                _cycleMetrics.RecordSince(cycleStartedAt);

            var remaining = FixedDeltaSeconds - accumulator;
            if (remaining > 0)
            {
                var sleepMilliseconds = (int)Math.Min(remaining * 1000.0, PollTimeoutMilliseconds);
                if (sleepMilliseconds > 0)
                    token.WaitHandle.WaitOne(sleepMilliseconds);
            }
        }

        PublishSnapshot();
    }

        /// <summary>
        /// Runs one tick and records how long it took.
        /// </summary>
        /// <remarks>
        /// A separate method rather than a stopwatch inside <see cref="Tick"/> so the measurement cannot drift
        /// if a caller ever invokes the tick directly, and so the cost of a tick stays a property of the loop
        /// that runs it.
        /// </remarks>
        private void TimedTick()
        {
            var startedAt = Stopwatch.GetTimestamp();
            Tick();
            _tickMetrics.RecordSince(startedAt);
        }

    private double ConsumeElapsedSeconds(Stopwatch clock, ref double previousSeconds)
    {
        var now = clock.Elapsed.TotalSeconds;
        var frameSeconds = now - previousSeconds;
        previousSeconds = now;

        var maxFrameSeconds = Volatile.Read(ref _maxFrameSeconds);
        if (frameSeconds <= maxFrameSeconds)
            return frameSeconds;

        // Report the time we refused to simulate so the debug UI can show the stall.
        var dropped = (long)((frameSeconds - maxFrameSeconds) / FixedDeltaSeconds);
        Interlocked.Add(ref _droppedTicks, dropped);
        return maxFrameSeconds;
    }

    private void Tick()
    {
        _tick++;
        _elapsedSeconds += FixedDeltaSeconds;

        var input = _readInput();
        var rotationSpeed = RotationSpeedRadiansPerSecond;

        // A held key steers the demo triangle, which proves input reaches the simulation without the
        // simulation ever touching GLFW. The key state itself arrived as a command.
        if (input.IsKeyDown(InputKeys.Left))
            rotationSpeed = -SteeredRotationSpeedRadiansPerSecond;
        else if (input.IsKeyDown(InputKeys.Right))
            rotationSpeed = SteeredRotationSpeedRadiansPerSecond;

        _rotationRadians += rotationSpeed * (float)FixedDeltaSeconds;
        if (_rotationRadians > MathF.Tau)
            _rotationRadians -= MathF.Tau;
        else if (_rotationRadians < -MathF.Tau)
            _rotationRadians += MathF.Tau;

        _scale = 1.0f + (ScaleAmplitude * MathF.Sin((float)_elapsedSeconds));

        Interlocked.Increment(ref _ticksExecuted);
        Interlocked.Increment(ref _ticksInWindow);
    }

    private void UpdateTicksPerSecond(double now)
    {
        var windowSeconds = now - _ticksPerSecondWindowStart;
        if (windowSeconds < TicksPerSecondWindowSeconds)
            return;

        _ticksPerSecond = Interlocked.Read(ref _ticksInWindow) / windowSeconds;
        _ticksPerSecondWindowStart = now;
        Interlocked.Exchange(ref _ticksInWindow, 0);
    }

    private void PublishSnapshot()
    {
        var input = _readInput();
        var paused = IsPaused;

        _snapshots.Publish(snapshot =>
        {
            // The snapshot instance is reused, so every field has to be overwritten each publish.
            snapshot.Tick = Interlocked.Read(ref _tick);
            snapshot.ElapsedSeconds = _elapsedSeconds;
            snapshot.RotationRadians = _rotationRadians;
            snapshot.Scale = _scale;
            snapshot.TicksPerSecond = _ticksPerSecond;
            snapshot.DroppedTicks = Interlocked.Read(ref _droppedTicks);
            snapshot.TicksExecuted = Interlocked.Read(ref _ticksExecuted);
            snapshot.CommandsApplied = Interlocked.Read(ref _commandsApplied);
            snapshot.PendingSteps = Interlocked.Read(ref _pendingSteps);
            snapshot.Paused = paused;
            snapshot.FramesRendered = _readFrameCount();
            snapshot.InputCommandsApplied = _readInputCommandCount();
            snapshot.IoJobsCompleted = _readIoCount();
            snapshot.NetworkMessagesProcessed = _readNetworkCount();
            snapshot.PendingEngineCommands = _readPendingEngineCommands();
            snapshot.Input = input;
        });
    }
}
