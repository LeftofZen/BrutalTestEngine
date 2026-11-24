using Serilog.Events;

namespace BrutalTestEngine.Logging;

/// <summary>
/// One rendered log event, as the debug window shows it.
/// </summary>
/// <remarks>
/// A value type with only immutable members, so a snapshot of the buffer is a plain array copy and the
/// window can render it without holding a lock or racing the thread that produced it.
/// </remarks>
public readonly struct LogEntry(
    LogEventLevel level,
    double elapsedSeconds,
    string category,
    string thread,
    string message
)
{
    /// <summary>The event's severity.</summary>
    public LogEventLevel Level { get; } = level;

    /// <summary>Seconds since the log pipeline started, for ordering and for a relative timestamp.</summary>
    public double ElapsedSeconds { get; } = elapsedSeconds;

    /// <summary>The logging category, e.g. <c>engine.render</c>.</summary>
    public string Category { get; } = category;

    /// <summary>The name of the thread that logged the event.</summary>
    public string Thread { get; } = thread;

    /// <summary>The rendered message.</summary>
    public string Message { get; } = message;
}

/// <summary>
/// A bounded ring buffer of the most recent log events, for the debug window.
/// </summary>
/// <remarks>
/// <para>
/// The console sink writes to a terminal the engine cannot read back, so the debug window needs its own copy
/// of the stream. This is that copy: a fixed-size ring that keeps the last few hundred events and overwrites
/// the oldest, so a long session cannot turn a diagnostic aid into a leak.
/// </para>
/// <para>
/// It is fed from a Serilog sink that is itself wrapped in an asynchronous sink, so the thread that logs
/// never touches this buffer: it hands a pre-rendered event to the pipeline's queue and returns. That keeps
/// the "zero-copy on the caller thread" property intact even though the window needs the events.
/// </para>
/// </remarks>
public sealed class LogEventBuffer
{
    /// <summary>How many events the buffer keeps by default.</summary>
    public const int DefaultCapacity = 512;

    private readonly LogEntry[] _entries;
    private readonly Lock _gate = new();
    private int _next;
    private int _count;
    private long _overwritten;

    public LogEventBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _entries = new LogEntry[capacity];
    }

    /// <summary>Maximum number of events retained.</summary>
    public int Capacity => _entries.Length;

    /// <summary>Number of events currently retained.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>Number of events overwritten because the buffer was full.</summary>
    public long OverwrittenCount
    {
        get
        {
            lock (_gate)
                return _overwritten;
        }
    }

    /// <summary>Appends an event. Safe to call from any thread.</summary>
    public void Add(in LogEntry entry)
    {
        lock (_gate)
        {
            _entries[_next] = entry;
            _next = (_next + 1) % _entries.Length;

            if (_count < _entries.Length)
                _count++;
            else
                _overwritten++;
        }
    }

    /// <summary>
    /// Copies the retained events into <paramref name="destination"/>, oldest first. Returns the number
    /// written.
    /// </summary>
    public int Snapshot(LogEntry[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        lock (_gate)
        {
            var written = Math.Min(_count, destination.Length);
            var start = (_next - written + _entries.Length * 2) % _entries.Length;

            for (var i = 0; i < written; i++)
                destination[i] = _entries[(start + i) % _entries.Length];

            return written;
        }
    }

    /// <summary>Removes every retained event.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_entries);
            _next = 0;
            _count = 0;
        }
    }
}
