using System.Diagnostics;
using Brutal.Logging;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Logging;
using BrutalTestEngine.Physics;
using Microsoft.Extensions.Logging;

namespace BrutalTestEngine.Simulation;

/// <summary>
/// Owns the physics thread, which advances an <see cref="IPhysicsWorld"/> and applies physics commands.
/// </summary>
/// <remarks>
/// <para>
/// Physics gets its own thread rather than being called from inside the simulation tick for two reasons. The
/// first is cost: a large world's step can take longer than the tick that asked for it, and folding it into the
/// simulation would make every other system's budget depend on the busiest moment in the physics world. The
/// second is sharding, which is where this engine is going: a seamless world that splits itself by region will
/// run several regions' physics independently, and independent work needs independent threads to run on.
/// </para>
/// <para>
/// The thread is a clock, not a slave to the tick rate. It advances the world by the elapsed wall time, capped
/// per iteration, so a stalled frame cannot ask the solver to catch up by simulating a second in one step — the
/// classic way a physics system explodes after a hitch.
/// </para>
/// </remarks>
public sealed class PhysicsThread : IDisposable, ICommandHandler
{
    /// <summary>How long one iteration waits when it has no commands and nothing to integrate.</summary>
    private const int PollMilliseconds = 4;

    /// <summary>
    /// Largest step a single iteration may take, in seconds.
    /// </summary>
    /// <remarks>
    /// Caps catch-up after a hitch. Without it, a 500 ms frame would arrive as one 500 ms step and whatever was
    /// resting on the ground would be found below it.
    /// </remarks>
    private const float MaxStepSeconds = 1.0f / 30.0f;

    /// <summary>Substeps per step. Two is Jolt's own suggested default for stable stacking.</summary>
    private const int CollisionSteps = 2;

    private static readonly LogCategory Log = EngineLog.Categories.Engine;

    private readonly CommandBus _bus;
    private readonly IPhysicsWorld _world;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ThreadMetrics _stepMetrics;
    private readonly ThreadMetrics _drainMetrics;

    private long _commandsApplied;
    private long _rejectedCommands;
    private volatile bool _disposed;

    public PhysicsThread(CommandBus bus, IPhysicsWorld world, ThreadTimerRegistry timers)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(timers);

        _bus = bus;
        _world = world;
        _stepMetrics = timers.Get(ThreadTimerRegistry.Keys.PhysicsStep);
        _drainMetrics = timers.Get(ThreadTimerRegistry.Keys.PhysicsDrain);

        if (Log.IsEnabled(LogLevel.Information))
            Log.Info($"physics thread started with backend '{world.Name}' (available: {world.IsAvailable})");

        _thread = new Thread(Run)
        {
            Name = "Physics",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>The world this thread owns. Exposed for diagnostics and the debug panel.</summary>
    public IPhysicsWorld World => _world;

    /// <summary>Commands applied on this thread.</summary>
    public long CommandsApplied => Interlocked.Read(ref _commandsApplied);

    /// <summary>Commands this thread refused, because they were invalid or physics is unavailable.</summary>
    public long RejectedCommands => Interlocked.Read(ref _rejectedCommands);

    /// <summary>Raised on the physics thread when the world fails.</summary>
    public event Action<Exception>? Faulted;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _world.Dispose();
        _cancellation.Dispose();
    }

    private void Run()
    {
        var token = _cancellation.Token;
        var clock = Stopwatch.StartNew();
        var previousSeconds = clock.Elapsed.TotalSeconds;

        while (!token.IsCancellationRequested)
        {
            var drainStartedAt = Stopwatch.GetTimestamp();
            _bus.Drain(CommandTarget.Physics, this, MaxCommandsPerDrain);
            _drainMetrics.RecordSince(drainStartedAt);

            var now = clock.Elapsed.TotalSeconds;
            var elapsed = (float)(now - previousSeconds);
            previousSeconds = now;

            if (elapsed > 0.0f && elapsed < MaxStepSeconds)
            {
                var stepStartedAt = Stopwatch.GetTimestamp();
                try
                {
                    _world.Step(elapsed, CollisionSteps);
                }
                catch (Exception exception)
                {
                    if (Log.IsEnabled(LogLevel.Error))
                        Log.Error(exception, $"physics step failed: {exception.Message}");

                    Faulted?.Invoke(exception);
                    break;
                }
                finally
                {
                    _stepMetrics.RecordSince(stepStartedAt);
                }
            }

            token.WaitHandle.WaitOne(PollMilliseconds);
        }
    }

    private const int MaxCommandsPerDrain = 64;

    /// <summary>Handles <see cref="AddBodyCommand"/>.</summary>
    void ICommandHandler.Handle(AddBodyCommand command)
    {
        if (!_world.IsAvailable || command.Radius <= 0.0f)
        {
            Interlocked.Increment(ref _rejectedCommands);
            return;
        }

        var handle = _world.AddSphere(command.Radius, (command.X, command.Y, command.Z), command.IsStatic);
        if (handle == 0)
        {
            Interlocked.Increment(ref _rejectedCommands);
            return;
        }

        Interlocked.Increment(ref _commandsApplied);
    }

    /// <summary>Handles <see cref="SetGravityCommand"/>.</summary>
    void ICommandHandler.Handle(SetGravityCommand command)
    {
        if (!_world.IsAvailable)
        {
            Interlocked.Increment(ref _rejectedCommands);
            return;
        }

        if (_world is JoltPhysicsWorld jolt)
            jolt.SetGravity(command.X, command.Y, command.Z);

        Interlocked.Increment(ref _commandsApplied);
    }

    // Physics commands are the only ones this thread owns. Everything else is admitted and logged by the bus and
    // left to the thread it was routed to, so these handlers only acknowledge the fact.

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

    void ICommandHandler.Handle(StepSimulationCommand command) => Ignore();

    void ICommandHandler.Handle(SetPausedCommand command) => Ignore();

    void ICommandHandler.Handle(ResetSimulationCommand command) => Ignore();

    void ICommandHandler.Handle(CaptureFrameCommand command) => Ignore();

    void ICommandHandler.Handle(SetLogLevelCommand command) => Ignore();

    void ICommandHandler.Handle(SetVolumeCommand command) => Ignore();

    void ICommandHandler.Handle(UnhandledCommand command) => Ignore();

    private static void Ignore()
    {
        // Nothing to do: the command is not addressed to this thread.
    }
}
