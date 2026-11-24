using System.Diagnostics;
using System.Text;
using Brutal.Numerics;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Logging;
using BrutalTestEngine.Threading;
using MSLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace BrutalTestEngine.Input;

/// <summary>
/// Owns the input thread. It drains input commands from the bus and folds them into an
/// <see cref="InputSnapshot"/> the simulation reads without blocking.
/// </summary>
/// <remarks>
/// <para>
/// This thread no longer receives raw GLFW events; it receives commands. That is the point of the command
/// pattern here: the main thread's GLFW callbacks translate a callback into a key, mouse, cursor, scroll
/// or focus command and post it to the bus, and this thread treats it no differently from a key command
/// that arrived over the network or from the remote console. One code path applies all of them.
/// </para>
/// <para>
/// Folding happens on its own thread so that a burst of events (a mouse drag, a key-repeat storm) cannot
/// stretch a simulation tick, and so the simulation always sees a coherent state rather than a
/// half-applied batch. It consumes the bus rather than owning a private queue, because the bus is where
/// ordering between the input, network and console sources is established.
/// </para>
/// <para>
/// Snapshots are published through a <see cref="TripleBuffer{T}"/>: the simulation always reads the newest
/// complete state and never waits for this thread.
/// </para>
/// </remarks>
public sealed class InputThread : IDisposable, ICommandHandler
{
    /// <summary>
    /// How many commands one drain pass may apply. A source that floods the bus must not be able to stall
    /// the snapshot loop indefinitely.
    /// </summary>
    private const int MaxCommandsPerDrain = 4096;

    private const int PollTimeoutMilliseconds = 4;

    /// <summary>
    /// Minimum time between publishes. Publishing per command would let a key-repeat or mouse-move storm
    /// dominate the cost of this thread, and the simulation only samples the snapshot once per tick, so
    /// bounding the rate keeps the cost flat without any visible latency.
    /// </summary>
    private const double PublishIntervalSeconds = 0.004;

    private const int TypedTextCapacity = 256;

    private readonly CommandBus _bus;
    private readonly TripleBuffer<InputSnapshot> _snapshots = new(() => InputSnapshot.Empty);
    private readonly Lock _snapshotGate = new();
    private readonly ThreadMetrics _drainMetrics;
    private readonly ThreadMetrics _publishMetrics;

    // Cached so the per-event log calls below cost a field read and a level check when they are filtered out.
    private readonly Brutal.Logging.LogCategory _log = EngineLog.Categories.Input;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly HashSet<int> _keysDown = [];
    private readonly HashSet<int> _buttonsDown = [];
    private readonly StringBuilder _typedText = new(TypedTextCapacity);
    private double2 _cursorPosition;
    private double2 _scrollDelta;
    private bool _focused = true;
    private long _eventCount;
    private volatile bool _dirty;
    private volatile bool _disposed;

    /// <param name="bus">The command bus this thread consumes input commands from.</param>
    /// <param name="timers">
    /// Where this thread records how long draining commands and publishing a snapshot take.
    /// </param>
    public InputThread(CommandBus bus, ThreadTimerRegistry timers)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(timers);

        _bus = bus;
        _drainMetrics = timers.Get(ThreadTimerRegistry.Keys.InputDrain);
        _publishMetrics = timers.Get(ThreadTimerRegistry.Keys.InputPublish);

        _thread = new Thread(Run)
        {
            Name = "Input",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    /// <summary>Number of input commands applied so far.</summary>
    public long EventCount => Interlocked.Read(ref _eventCount);

    /// <summary>
    /// Reads the newest input snapshot. The returned instance is owned by this thread and stays valid
    /// until the next call, so copy out what is needed.
    /// </summary>
    /// <remarks>
    /// The triple buffer behind this allows exactly one reader thread. Both the simulation thread and the
    /// main thread sample input, so the handoff between them and the buffer is serialised here rather than
    /// letting two threads claim the same published slot. The gate is only ever taken by a caller that
    /// wants the newest value, so neither the input thread nor a reader is blocked on the other's work.
    /// </remarks>
    public bool TryReadSnapshot(out InputSnapshot snapshot, out long version)
    {
        lock (_snapshotGate)
            return _snapshots.TryRead(out snapshot, out version);
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

    private void Run()
    {
        var token = _cancellation.Token;
        var clock = Stopwatch.StartNew();
        var nextPublishSeconds = 0.0;

        while (!token.IsCancellationRequested)
        {
            var drainStartedAt = Stopwatch.GetTimestamp();
            _bus.Drain(CommandTarget.Input, this, MaxCommandsPerDrain);
            _drainMetrics.RecordSince(drainStartedAt);

            var now = clock.Elapsed.TotalSeconds;
            if (_dirty && now >= nextPublishSeconds)
            {
                var publishStartedAt = Stopwatch.GetTimestamp();
                PublishSnapshot();
                _publishMetrics.RecordSince(publishStartedAt);
                nextPublishSeconds = now + PublishIntervalSeconds;
            }

            // A short wait rather than blocking on the bus, so the thread also notices cancellation.
            token.WaitHandle.WaitOne(PollTimeoutMilliseconds);
        }

        PublishSnapshot();
    }

    void ICommandHandler.Handle(KeyCommand command)
    {
        if (command.Pressed)
            _keysDown.Add(command.Key);
        else
            _keysDown.Remove(command.Key);

        // Input events are the highest-volume thing the engine logs, so they sit at the lowest level: they
        // are only worth seeing when something is wrong with input, and at any higher level they would bury
        // everything else. The level check is what keeps the interpolated string from being formatted.
        if (_log.IsEnabled(MSLogLevel.Trace))
            _log.Trace($"key {command.Key} {(command.Pressed ? "down" : "up")}");

        Count();
    }

    void ICommandHandler.Handle(MouseButtonCommand command)
    {
        if (command.Pressed)
            _buttonsDown.Add(command.Button);
        else
            _buttonsDown.Remove(command.Button);

        if (_log.IsEnabled(MSLogLevel.Trace))
            _log.Trace($"mouse {command.Button} {(command.Pressed ? "down" : "up")}");

        Count();
    }

    void ICommandHandler.Handle(CursorMoveCommand command)
    {
        _cursorPosition = new double2(command.X, command.Y);

        if (_log.IsEnabled(MSLogLevel.Trace))
            _log.Trace($"cursor {command.X:0.0},{command.Y:0.0}");

        Count();
    }

    void ICommandHandler.Handle(ScrollCommand command)
    {
        _scrollDelta += new double2(command.X, command.Y);

        if (_log.IsEnabled(MSLogLevel.Trace))
            _log.Trace($"scroll {command.X:0.00},{command.Y:0.00}");

        Count();
    }

    void ICommandHandler.Handle(TextInputCommand command)
    {
        _typedText.Append(command.Text);

        if (_log.IsEnabled(MSLogLevel.Trace))
            _log.Trace($"text '{command.Text}'");

        Count();
    }

    void ICommandHandler.Handle(FocusCommand command)
    {
        _focused = command.Focused;
        if (!_focused)
        {
            // A window that loses focus never delivers the matching release events.
            _keysDown.Clear();
            _buttonsDown.Clear();
        }

        if (_log.IsEnabled(MSLogLevel.Trace))
            _log.Trace($"focus {(_focused ? "gained" : "lost")}");

        Count();
    }

    // Input commands are the only ones this thread owns. Every other command type is admitted and logged
    // by the bus and left to the thread it was routed to, so the handlers below only count the fact.

    void ICommandHandler.Handle(QuitCommand command) => Ignore();

    void ICommandHandler.Handle(HelpCommand command) => Ignore();

    void ICommandHandler.Handle(SetWindowModeCommand command) => Ignore();

    void ICommandHandler.Handle(ToggleWindowModeCommand command) => Ignore();

    void ICommandHandler.Handle(SetTickRateCommand command) => Ignore();

    void ICommandHandler.Handle(InjectInputCommand command) => Ignore();

    void ICommandHandler.Handle(StepSimulationCommand command) => Ignore();

    void ICommandHandler.Handle(SetPausedCommand command) => Ignore();

    void ICommandHandler.Handle(ResetSimulationCommand command) => Ignore();

    void ICommandHandler.Handle(CaptureFrameCommand command) => Ignore();

    void ICommandHandler.Handle(SetLogLevelCommand command) => Ignore();

    void ICommandHandler.Handle(UnhandledCommand command) => Ignore();

    void ICommandHandler.Handle(AddBodyCommand command) => Ignore();

    void ICommandHandler.Handle(SetGravityCommand command) => Ignore();

    void ICommandHandler.Handle(SetVolumeCommand command) => Ignore();

    private static void Ignore()
    {
        // Nothing to do: the command is not addressed to this thread.
    }

    private void Count()
    {
        Interlocked.Increment(ref _eventCount);
        _dirty = true;
    }

    private void PublishSnapshot()
    {
        var eventCount = EventCount;

        // Nothing has arrived yet, so the simulation keeps reading the empty snapshot it started with.
        if (eventCount == 0)
            return;

        _dirty = false;

        _snapshots.Publish(snapshot =>
        {
            // The snapshot instance is reused, so every field has to be overwritten each publish.
            snapshot.KeysDown = new HashSet<int>(_keysDown);
            snapshot.ButtonsDown = new HashSet<int>(_buttonsDown);
            snapshot.CursorPosition = _cursorPosition;
            snapshot.ScrollDelta = _scrollDelta;
            snapshot.TypedText = _typedText.ToString();
            snapshot.Focused = _focused;
            snapshot.EventCount = eventCount;
        });

        _scrollDelta = default;
        _typedText.Clear();
    }
}
