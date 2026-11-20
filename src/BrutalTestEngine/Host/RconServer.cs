using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BrutalTestEngine.Commands;

namespace BrutalTestEngine.Host;

/// <summary>
/// Runs a minimal line-oriented remote console over TCP on the loopback interface.
/// </summary>
/// <remarks>
/// <para>
/// This is a genuine second transport for the same command grammar: a remote operator connects, sends
/// command lines and receives replies. The engine does not care whether an intent was typed on the host
/// console or arrived over this socket, which is precisely what the command pattern is for.
/// </para>
/// <para>
/// A client cannot post commands directly. A received line becomes an <see cref="InjectInputCommand"/>
/// on the bus, and the engine itself decides what that line means when it applies the command on its own
/// thread. The network therefore never reaches into the queues, and per-source policy has one place to
/// live.
/// </para>
/// <para>
/// The listener is loopback-only by design: an unauthenticated remote console on a public interface would
/// be a vulnerability, so it is deliberately not offered.
/// </para>
/// </remarks>
public sealed class RconServer : IDisposable
{
    private const int AcceptTimeoutMilliseconds = 16;
    private const int MaxLineLength = 4096;
    private const int MaxClients = 8;
    private const int MaxPendingBroadcasts = 256;

    private readonly CommandBus _bus;
    private readonly TcpListener _listener;
    private readonly Thread _acceptThread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<int, Client> _clients = new();
    private int _nextClientId;
    private long _linesPosted;
    private long _connections;
    private volatile bool _disposed;

    public RconServer(CommandBus bus, int port = 0)
    {
        ArgumentNullException.ThrowIfNull(bus);

        _bus = bus;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _acceptThread = new Thread(AcceptLoop)
        {
            Name = "RconAccept",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _acceptThread.Start();
    }

    /// <summary>The loopback TCP port the server listens on.</summary>
    public int Port { get; }

    /// <summary>Number of lines received from clients and posted as commands.</summary>
    public long LinesPosted => Interlocked.Read(ref _linesPosted);

    /// <summary>Number of client connections accepted.</summary>
    public long ConnectionCount => Interlocked.Read(ref _connections);

    /// <summary>Number of clients currently connected.</summary>
    public int ClientCount => _clients.Count;

    /// <summary>
    /// Queues text for every connected client. Called from the main thread when the engine wants to explain
    /// what it did with a line, for example to report an unrecognised verb.
    /// </summary>
    public void Broadcast(string text)
    {
        if (_disposed)
            return;

        foreach (var client in _clients.Values)
            client.Enqueue(text);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already torn down.
        }

        foreach (var client in _clients.Values)
            client.Dispose();

        _acceptThread.Join(TimeSpan.FromSeconds(1));
        _cancellation.Dispose();
    }

    private void AcceptLoop()
    {
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            if (!_listener.Pending())
            {
                token.WaitHandle.WaitOne(AcceptTimeoutMilliseconds);
                continue;
            }

            TcpClient accepted;
            try
            {
                accepted = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (_clients.Count >= MaxClients)
            {
                accepted.Dispose();
                continue;
            }

            var id = Interlocked.Increment(ref _nextClientId);
            var client = new Client(this, id, accepted);
            _clients[id] = client;
            Interlocked.Increment(ref _connections);
            client.Start();
        }
    }

    private void OnClientLine(string line)
    {
        if (line.Length > MaxLineLength)
            return;

        _bus.Post(new InjectInputCommand { Source = CommandSource.Remote, Text = line });
        Interlocked.Increment(ref _linesPosted);
    }

    private void OnClientClosed(int id) => _clients.TryRemove(id, out _);

    /// <summary>
    /// One connected client. Each client owns its read/write loop so a client that stops reading cannot
    /// block the accept loop or any other client.
    /// </summary>
    private sealed class Client : IDisposable
    {
        private readonly RconServer _server;
        private readonly int _id;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly Thread _thread;
        private readonly ConcurrentQueue<byte[]> _outgoing = new();
        private volatile bool _disposed;

        public Client(RconServer server, int id, TcpClient client)
        {
            _server = server;
            _id = id;
            _client = client;
            _stream = client.GetStream();

            _thread = new Thread(Run)
            {
                Name = $"RconClient{id}",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
        }

        public void Start() => _thread.Start();

        public void Enqueue(string text)
        {
            if (_disposed)
                return;

            // Bounded: a client that never reads must not be able to grow this without limit.
            if (_outgoing.Count >= MaxPendingBroadcasts)
                return;

            _outgoing.Enqueue(Encoding.UTF8.GetBytes(text + "\n"));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _client.Dispose();
        }

        private void Run()
        {
            var buffer = new byte[MaxLineLength];
            var text = new StringBuilder();
            var writable = _client.Client.Poll(0, SelectMode.SelectWrite);

            while (!_disposed)
            {
                Flush();

                // Non-blocking read: this loop also has to service outgoing text, so it cannot park in a
                // blocking read waiting for the next line.
                int read;
                try
                {
                    read = _client.Available > 0 ? _stream.Read(buffer, 0, buffer.Length) : 0;
                }
                catch (IOException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (read > 0)
                {
                    text.Append(Encoding.UTF8.GetString(buffer, 0, read));

                    while (TryTakeLine(text, out var line))
                        _server.OnClientLine(line);

                    continue;
                }

                Thread.Sleep(2);
            }

            _server.OnClientClosed(_id);
            Dispose();
        }

        private void Flush()
        {
            while (_outgoing.TryPeek(out var payload))
            {
                try
                {
                    _stream.Write(payload, 0, payload.Length);
                    _stream.Flush();
                }
                catch (IOException)
                {
                    _outgoing.Clear();
                    Dispose();
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                _outgoing.TryDequeue(out _);
            }
        }

        private static bool TryTakeLine(StringBuilder text, out string line)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                    continue;

                line = text.ToString(0, i).TrimEnd('\r');

                // Drop the consumed line and its terminator.
                text.Remove(0, i + 1);
                return true;
            }

            line = string.Empty;
            return false;
        }
    }
}
