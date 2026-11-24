using System.Net;
using System.Net.Sockets;

namespace BrutalTestEngine.Networking;

/// <summary>
/// The engine's built-in transport: a plain UDP socket on the loopback interface.
/// </summary>
/// <remarks>
/// <para>
/// Kept as the default because it needs no third-party dependency and exercises the full contract, so the
/// engine can run and be tested with no external server. It is also the reference implementation of
/// <see cref="INetworkTransport"/>: everything a richer transport must satisfy is demonstrated here.
/// </para>
/// <para>
/// Deliberately unambitious. UDP is unordered, unreliable and unfragmented, and this type makes no attempt to
/// hide that — adding reliability here would duplicate what a real transport library already does properly,
/// and would make swapping it out a behaviour change rather than a dependency change.
/// </para>
/// </remarks>
public sealed class UdpNetworkTransport : INetworkTransport
{
    /// <summary>
    /// The largest datagram this transport will carry.
    /// </summary>
    /// <remarks>
    /// A conservative MTU-safe payload: the smallest common path MTU is 1280, and 1400 leaves room for the
    /// IPv6 and UDP headers without relying on IP fragmentation, which many middleboxes drop.
    /// </remarks>
    public const int DefaultMaxPayloadSize = 1400;

    private readonly Socket _socket;
    private IPEndPoint _peer;
    private bool _disposed;

    private long _received;
    private long _sent;
    private long _rejected;

    public UdpNetworkTransport(int maxPayloadSize = DefaultMaxPayloadSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadSize, 1);
        MaxPayloadSize = maxPayloadSize;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _peer = new IPEndPoint(IPAddress.Loopback, 0);
    }

    public string Name => "udp";

    public IPEndPoint LocalEndPoint { get; private set; } = new(IPAddress.Loopback, 0);

    public bool IsOpen { get; private set; }

    public long ReceivedCount => Interlocked.Read(ref _received);

    public long SentCount => Interlocked.Read(ref _sent);

    public long RejectedCount => Interlocked.Read(ref _rejected);

    public int MaxPayloadSize { get; }

    public void Open(int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsOpen)
            return;

        _socket.Bind(new IPEndPoint(IPAddress.Loopback, port));

        // A short receive timeout keeps Poll bounded without spinning; the owning thread decides how often to
        // poll and must stay responsive to cancellation.
        _socket.ReceiveTimeout = 16;

        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;

        // With no configured peer, the transport talks to itself. That is what makes the default engine
        // self-contained: a command sent over the socket comes back in through the real receive path, so the
        // loopback echo exercises exactly the code a remote peer would.
        _peer = new IPEndPoint(IPAddress.Loopback, LocalEndPoint.Port);
        IsOpen = true;
    }

    public bool Send(ReadOnlySpan<byte> payload) => SendTo(payload, _peer);

    public bool SendTo(ReadOnlySpan<byte> payload, IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (_disposed || payload.Length > MaxPayloadSize)
            return false;

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
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public bool Poll(int timeoutMilliseconds, out TransportDatagram datagram)
    {
        datagram = default;

        if (_disposed || !IsOpen)
            return false;

        // One byte larger than the maximum, so a datagram that had to be truncated is detected rather than
        // silently accepted as a short one.
        var buffer = new byte[MaxPayloadSize + 1];
        EndPoint remote = new IPEndPoint(IPAddress.Loopback, 0);

        try
        {
            if (!_socket.Poll(timeoutMilliseconds * 1000, SelectMode.SelectRead))
                return false;

            var received = _socket.ReceiveFrom(buffer, ref remote);
            if (received <= 0)
                return false;

            Interlocked.Increment(ref _received);

            if (received > MaxPayloadSize)
            {
                Interlocked.Increment(ref _rejected);
                return false;
            }

            datagram = new TransportDatagram(new ReadOnlyMemory<byte>(buffer, 0, received), (IPEndPoint)remote);
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.TimedOut)
        {
            return false;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.WouldBlock)
        {
            return false;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.MessageSize)
        {
            // Windows reports an oversized datagram as an error rather than a short read. The frame was
            // truncated, so its content cannot be trusted: count it and keep the socket alive.
            Interlocked.Increment(ref _rejected);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        IsOpen = false;
        _socket.Dispose();
    }
}
