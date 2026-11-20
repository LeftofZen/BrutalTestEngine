using System.Text;
using BrutalTestEngine.Commands;

namespace BrutalTestEngine.Host;

/// <summary>
/// Reads lines from standard input and posts them as commands.
/// </summary>
/// <remarks>
/// <para>
/// The host console is a local remote console: the same grammar, the same bus, the same handlers, but the
/// transport is the process's own stdin. Line-oriented reads are blocking with no timeout, so the reading
/// happens on a background thread and the thread is only ever held while it is genuinely waiting for a
/// line. End of input (stdin redirected from a file or a pipe) ends the reader cleanly instead of spinning.
/// </para>
/// <para>
/// The reader is deliberately the simplest possible thing: read a line, parse it with the shared grammar,
/// hand it to the bus. Anything more — history, completion, multi-line editing — belongs in the UI, not in
/// the transport.
/// </para>
/// </remarks>
public sealed class ConsoleCommandSource : IDisposable
{
    private readonly CommandBus _bus;
    private readonly TextReader _input;
    private readonly Thread _thread;
    private long _linesPosted;
    private volatile bool _disposed;

    /// <param name="bus">The command bus lines are posted to.</param>
    /// <param name="input">The reader to read lines from, defaulting to standard input.</param>
    public ConsoleCommandSource(CommandBus bus, TextReader? input = null)
    {
        ArgumentNullException.ThrowIfNull(bus);

        _bus = bus;
        _input = input ?? Console.In;

        _thread = new Thread(Run)
        {
            Name = "Console",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>Number of console lines posted to the bus.</summary>
    public long LinesPosted => Interlocked.Read(ref _linesPosted);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // A reader blocked on an interactive stdin cannot be interrupted portably. The thread is a
        // background thread that exits as soon as a line arrives or the stream ends, so it is not awaited
        // past a short grace period.
        _thread.Join(TimeSpan.FromMilliseconds(50));
    }

    private void Run()
    {
        while (!_disposed)
        {
            string? line;
            try
            {
                line = _input.ReadLine();
            }
            catch (IOException)
            {
                // The stream closed underneath us.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Null means end of input.
            if (line is null)
                return;

            _bus.Post(CommandParser.Parse(line, CommandSource.Remote));
            Interlocked.Increment(ref _linesPosted);
        }
    }
}
