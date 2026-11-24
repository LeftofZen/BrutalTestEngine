using System.Diagnostics;
using BrutalTestEngine.Threading;

namespace BrutalTestEngine.Commands;

/// <summary>
/// The engine's concurrent command queue: a single, thread-safe place where every source posts intents
/// and from which the owning threads drain them.
/// </summary>
/// <remarks>
/// <para>
/// The bus is the enforcement point for the engine's first principle that all input and logic is command
/// based. Keyboard and mouse events, network datagrams, remote console lines, web events and database
/// row changes all arrive here as <see cref="Command"/> instances; nothing mutates engine or simulation
/// state directly.
/// </para>
/// <para>
/// Ingress is many-producer / single-consumer per target: any number of source threads may call
/// <see cref="Post(Command)"/>, and exactly one owning thread calls <see cref="TryDequeue"/>. Each
/// target has its own queue — the main thread owns <see cref="CommandTarget.Engine"/>, the input thread
/// owns <see cref="CommandTarget.Input"/> and the simulation thread owns
/// <see cref="CommandTarget.Simulation"/> — so no thread can consume another's commands.
/// </para>
/// <para>
/// Admitting a command is deliberately cheap and never blocks, because it runs inside GLFW callbacks
/// and socket reads. Sequencing, timing and logging happen here rather than in the callers so that every
/// source gets identical ordering and diagnostics for free.
/// </para>
/// </remarks>
public sealed class CommandBus : IDisposable
{
    private static readonly CommandTarget[] Targets =
    [
        CommandTarget.Engine,
        CommandTarget.Input,
        CommandTarget.Simulation,
        CommandTarget.Physics,
        CommandTarget.Audio,
    ];

    private readonly Dictionary<CommandTarget, MpscQueue<Command>> _queues = new()
    {
        [CommandTarget.Engine] = new MpscQueue<Command>(),
        [CommandTarget.Input] = new MpscQueue<Command>(),
        [CommandTarget.Simulation] = new MpscQueue<Command>(),
        [CommandTarget.Physics] = new MpscQueue<Command>(),
        [CommandTarget.Audio] = new MpscQueue<Command>(),
    };

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _sequenceGate = new();
    private readonly long[] _applied = new long[Targets.Length];
    private long _sequence;
    private long _accepted;
    private bool _disposed;

    /// <summary>The most recently accepted commands, for diagnostics.</summary>
    public CommandLog Log { get; } = new();

    /// <summary>Total number of commands accepted from all sources.</summary>
    public long AcceptedCount => Interlocked.Read(ref _accepted);

    /// <summary>Number of accepted commands not yet applied.</summary>
    public int PendingCount
    {
        get
        {
            var pending = 0;
            foreach (var queue in _queues.Values)
                pending += queue.Count;

            return pending;
        }
    }

    /// <summary>Number of commands applied on the thread that owns a target.</summary>
    public long AppliedCount(CommandTarget target) => Interlocked.Read(ref _applied[(int)target]);

    /// <summary>
    /// Queues a command for its owning thread. Safe from any thread and never blocks, so it may be
    /// called directly from a GLFW callback or a socket read.
    /// </summary>
    public long Post(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        long sequence;
        lock (_sequenceGate)
        {
            sequence = ++_sequence;
        }

        command.Sequence = sequence;
        command.TimestampSeconds = _clock.Elapsed.TotalSeconds;

        // Log on the producing thread. It is cheap, and deferring it would force the consumer to see
        // commands it does not own.
        Log.Add(command);
        Interlocked.Increment(ref _accepted);

        _queues[command.Target].Enqueue(command);
        return sequence;
    }

    /// <summary>
    /// Takes the next command for one target without blocking, counting it as applied by the calling
    /// thread. Returns false when that target's queue is empty.
    /// </summary>
    public bool TryDequeue(CommandTarget target, out Command command)
    {
        if (_queues[target].TryDequeue(out command))
        {
            Interlocked.Increment(ref _applied[(int)target]);
            return true;
        }

        command = null!;
        return false;
    }

    /// <summary>
    /// Drains up to <paramref name="maxCommands"/> commands for one target, dispatching each to
    /// <paramref name="handler"/>. Returns the number applied.
    /// </summary>
    /// <remarks>
    /// The bound matters: a source that floods the bus must not be able to stall a frame or a tick
    /// indefinitely. Whatever is left over is applied on the next call, still in order.
    /// </remarks>
    public int Drain(CommandTarget target, ICommandHandler handler, int maxCommands)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var applied = 0;
        while (applied < maxCommands && TryDequeue(target, out var command))
        {
            CommandDispatcher.Dispatch(handler, command);
            applied++;
        }

        return applied;
    }

    /// <summary>Signals that no further commands will be posted and wakes any waiting consumer.</summary>
    public void Complete()
    {
        foreach (var queue in _queues.Values)
            queue.Complete();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var queue in _queues.Values)
            queue.Dispose();
    }
}
