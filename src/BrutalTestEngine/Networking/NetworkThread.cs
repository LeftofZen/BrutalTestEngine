using System.Net;
using System.Net.Sockets;
using System.Text;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Threading;

namespace BrutalTestEngine.Networking;

/// <summary>A datagram received by the network thread.</summary>
/// <param name="Payload">The raw bytes received.</param>
/// <param name="Remote">The sender's endpoint.</param>
public readonly record struct NetworkMessage(byte[] Payload, IPEndPoint Remote);

/// <summary>
/// Owns the network thread. Sockets are blocking and unpredictable, so all networking runs here and never
/// on the simulation or render thread.
/// </summary>
/// <remarks>
/// <para>
/// Received datagrams are treated as text in the shared command grammar and parsed into commands, which
/// are then posted to the bus. That is the whole contract: the network thread never decides what a
/// message means, it only decodes the transport and hands the intent over. A peer therefore has exactly
/// the same powers and the same code path as a key press.
/// </para>
/// <para>
/// The thread binds a UDP socket on the loopback interface. Keeping the engine self-contained means it can
/// run and be tested with no external server, while still exercising the real contract.
/// </para>
/// <para>
/// A dedicated thread rather than the thread pool keeps socket ownership and ordering explicit, and keeps
/// the engine's thread count predictable.
/// </para>
/// </remarks>
public sealed class NetworkThread : IDisposable
{
    private const int ReceiveTimeoutMilliseconds = 16;
    private const int MaxDatagramSize = 1400;
    private const int MaxCommandsPerDatagram = 64;
    private const int MaxPayloadCharacters = 4096;

    /// <summary>
    /// Strict UTF-8, so malformed bytes throw instead of being silently replaced with U+FFFD.
    /// </summary>
    /// <remarks>
    /// A lossy decoder would turn a corrupt frame into a plausible-looking command with replacement
    /// characters in it, which is far worse than dropping the frame: the engine would act on a mangled
    /// intent instead of reporting that the transport delivered garbage.
    /// </remarks>
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    private readonly CommandBus _bus;
    private readonly MpscQueue<byte[]> _outgoing = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Socket _socket;
    private readonly IPEndPoint _remote;
        private readonly ThreadMetrics _pollMetrics;
        private readonly ThreadMetrics _decodeMetrics;
    private long _received;
    private long _sent;
    private long _commandsPosted;
    private long _rejected;
    private volatile bool _disposed;

    /// <param name="bus">The command bus received messages are posted to.</param>
        /// <param name="timers">Where this thread records how long a poll cycle and a decode take.</param>
    /// <param name="port">The loopback port to bind. Zero picks a free port.</param>
        public NetworkThread(CommandBus bus, ThreadTimerRegistry timers, int port = 0)
    {
        ArgumentNullException.ThrowIfNull(bus);
            ArgumentNullException.ThrowIfNull(timers);

        _bus = bus;
            _pollMetrics = timers.Get(ThreadTimerRegistry.Keys.NetworkPoll);
            _decodeMetrics = timers.Get(ThreadTimerRegistry.Keys.NetworkDecode);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        _socket.ReceiveTimeout = ReceiveTimeoutMilliseconds;

        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        _remote = new IPEndPoint(IPAddress.Loopback, LocalEndPoint.Port);

        _thread = new Thread(Run)
        {
            Name = "Network",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>The loopback endpoint the socket is bound to.</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>Number of datagrams received.</summary>
    public long ReceivedCount => Interlocked.Read(ref _received);

    /// <summary>Number of datagrams sent.</summary>
    public long SentCount => Interlocked.Read(ref _sent);

    /// <summary>Number of commands posted to the bus from received datagrams.</summary>
    public long CommandsPosted => Interlocked.Read(ref _commandsPosted);

    /// <summary>Number of datagrams that could not be decoded and were discarded.</summary>
    public long RejectedCount => Interlocked.Read(ref _rejected);

    /// <summary>Raised on the network thread when the socket fails.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Sends a datagram to the loopback peer. Safe from any thread.</summary>
    public void Send(byte[] payload)
    {
        if (_disposed)
            return;

        _outgoing.Enqueue(payload);
    }

    /// <summary>
    /// Encodes a command with the shared grammar and sends it to the loopback peer, so it comes back in
    /// through the real socket as a network command.
    /// </summary>
    public bool Send(Command command)
    {
        if (!CommandParser.TryFormat(command, out var line))
            return false;

        Send(Encoding.UTF8.GetBytes(line));
        return true;
    }

    /// <summary>
    /// Sends a payload to an arbitrary endpoint. Used to prove the receive path accepts a genuine inbound
    /// datagram rather than only the loopback echo.
    /// </summary>
    public bool SendTo(ReadOnlySpan<byte> payload, IPEndPoint endpoint)
    {
        try
        {
            _socket.SendTo(payload, endpoint);
            Interlocked.Increment(ref _sent);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _outgoing.Complete();
        _thread.Join(TimeSpan.FromSeconds(2));
        _socket.Dispose();
        _cancellation.Dispose();
        _outgoing.Dispose();
    }

    private void Run()
    {
        var token = _cancellation.Token;

        // One byte larger than the maximum, so a datagram that has to be truncated is detected rather
        // than silently accepted as a short one.
        var buffer = new byte[MaxDatagramSize + 1];
        EndPoint remote = _remote;
        var text = new StringBuilder(MaxDatagramSize);

        while (!token.IsCancellationRequested)
        {
                    var pollStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();

            try
            {
                while (_outgoing.TryDequeue(out var payload))
                {
                    _socket.SendTo(payload, _remote);
                    Interlocked.Increment(ref _sent);
                }

                if (!_socket.Poll(ReceiveTimeoutMilliseconds * 1000, SelectMode.SelectRead))
                    continue;

                var received = _socket.ReceiveFrom(buffer, ref remote);
                if (received <= 0)
                    continue;

                Interlocked.Increment(ref _received);

                // An oversized datagram is truncated by the receive buffer, so its content cannot be trusted.
                if (received > MaxDatagramSize)
                {
                    Interlocked.Increment(ref _rejected);
                    continue;
                }

                        var decodeStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                        try
                        {
                            Decode(buffer.AsSpan(0, received), text);
                        }
                        finally
                        {
                            _decodeMetrics.RecordSince(decodeStartedAt);
                        }
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut)
            {
                // Nothing arrived in this window; keep polling.
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock)
            {
                // Non-blocking socket with no data; keep polling.
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.MessageSize)
            {
                // Windows reports an oversized datagram as an error rather than a short read. The frame was
                // truncated, so its content cannot be trusted: count it and keep the socket alive. Letting
                // this reach the generic handler would tear the network thread down over one bad frame.
                Interlocked.Increment(ref _rejected);
            }
            catch (Exception exception)
            {
                Faulted?.Invoke(exception);
                break;
            }
                    finally
                    {
                        // Includes the time spent blocked in Poll, which is the honest cost of a poll cycle: it is
                        // how long this thread was occupied before it could look at anything else.
                        _pollMetrics.RecordSince(pollStartedAt);
                    }
        }
    }

    private void Decode(ReadOnlySpan<byte> payload, StringBuilder text)
    {
        text.Clear();

        // A frame that is not valid UTF-8, or that decodes to something absurdly long, is discarded here
        // rather than parsed into garbage commands: a malformed transport frame is a transport problem, not
        // an intent, and the engine must never act on a mangled one.
        try
        {
            text.Append(StrictUtf8.GetString(payload));
        }
        catch (DecoderFallbackException)
        {
            Interlocked.Increment(ref _rejected);
            return;
        }

        if (text.Length > MaxPayloadCharacters)
        {
            Interlocked.Increment(ref _rejected);
            return;
        }

        var commands = CommandParser.ParseMany(text.ToString(), CommandSource.Network);
        if (commands.Count > MaxCommandsPerDatagram)
        {
            Interlocked.Increment(ref _rejected);
            return;
        }

        foreach (var command in commands)
        {
            _bus.Post(command);
            Interlocked.Increment(ref _commandsPosted);
        }
    }
}
