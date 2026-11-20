namespace BrutalTestEngine.Commands;

/// <summary>
/// A bounded ring buffer of the commands the bus has accepted, for the debug console.
/// </summary>
/// <remarks>
/// <para>
/// The log exists because the command stream is the engine's single decision point: being able to see
/// the last few hundred intents, in order, with their source, is what makes a concurrent system
/// debuggable without attaching a debugger.
/// </para>
/// <para>
/// It is deliberately bounded. An unbounded log would grow for the lifetime of a session and turn a
/// diagnostic aid into a leak; the oldest entry is overwritten instead.
/// </para>
/// </remarks>
public sealed class CommandLog
{
    private readonly Command[] _entries;
    private readonly Lock _gate = new();
    private int _next;
    private int _count;
    private long _dropped;

    public CommandLog(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _entries = new Command[capacity];
    }

    /// <summary>Maximum number of entries the log keeps.</summary>
    public int Capacity => _entries.Length;

    /// <summary>Number of entries currently retained.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>Number of entries overwritten because the log was full.</summary>
    public long OverwrittenCount
    {
        get
        {
            lock (_gate)
                return _dropped;
        }
    }

    /// <summary>Appends a command. Safe to call from any thread.</summary>
    public void Add(Command command)
    {
        lock (_gate)
        {
            _entries[_next] = command;
            _next = (_next + 1) % _entries.Length;

            if (_count < _entries.Length)
                _count++;
            else
                _dropped++;
        }
    }

    /// <summary>
    /// Copies the retained commands into <paramref name="destination"/>, oldest first. Returns the
    /// number of commands written.
    /// </summary>
    public int Snapshot(Command[] destination)
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

    /// <summary>Removes every retained command.</summary>
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
