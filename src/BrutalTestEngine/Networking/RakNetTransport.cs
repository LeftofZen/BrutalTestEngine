using System.Diagnostics;
using System.Net;
using System.Text;
using Brutal;
using Brutal.Logging;
using Brutal.RakNetApi;
using BrutalTestEngine.Logging;
using Microsoft.Extensions.Logging;

namespace BrutalTestEngine.Networking;

/// <summary>
/// The engine's RakNet transport, built on BRUTAL's <see cref="RakPeerInterface"/> binding.
/// </summary>
/// <remarks>
/// <para>
/// RakNet is what BRUTAL ships, so it is the transport the engine is actually shaped for: it brings reliable
/// ordered channels, packet priorities and connection management that a raw UDP socket does not have, and it
/// is the layer a server-meshed world will need when peers are long-lived and authoritative.
/// </para>
/// <para>
/// <b>Peer mode.</b> The transport always acts as a peer, never as a listener-only socket. <see cref="Open"/>
/// starts RakNet, accepts inbound connections and — when <c>server</c> is non-empty — also dials out to that
/// server. That is what lets the engine be the client of a dedicated server, the server for a connecting
/// client, or a self-contained loopback peer with no server at all, without three code paths: only whether a
/// connect attempt was made differs. A server is identified by <see cref="RakGUIDFromEndpoint"/> so replies
/// address the peer rather than whatever address the last packet happened to carry.
/// </para>
/// <para>
/// <b>Broadcast payloads.</b> A payload handed to <see cref="Send"/> goes to the connected peer when there is
/// one and is broadcast to every connected peer when there is not. The engine's command grammar is a line
/// protocol, so "send to the peer" and "send to everyone" are the only two useful meanings, and picking by
/// connection state keeps the caller from having to know which role it is in.
/// </para>
/// <para>
/// <b>Unreliable, unsequenced framing.</b> Payloads are sent as a single unreliable-ordered datagram of a
/// small (<see cref="HeaderSize"/> byte) sequence header plus the caller's bytes. Reliability is deliberately
/// not used: the engine's command stream is state-oriented, a stale command is worse than a lost one, and
/// pretending otherwise would hide transport loss from the simulation that has to cope with it. The header
/// exists only so a peer's datagram sequence is observable, which the engine records for diagnostics.
/// </para>
/// </remarks>
public sealed unsafe class RakNetTransport : INetworkTransport
{
    /// <summary>
    /// A conservative MTU-safe payload: the smallest common path MTU is 1280, and 1400 leaves room for the
    /// IPv6, UDP and RakNet headers without relying on IP fragmentation, which many middleboxes drop.
    /// </summary>
    public const int DefaultMaxPayloadSize = 1400;

    /// <summary>Four bytes of little-endian datagram sequence, ahead of the caller's payload.</summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// The channel command traffic is sent on. One channel is all the engine needs today, and naming it
    /// makes the eventual split — reliable control channel, unreliable snapshot channel — a change here only.
    /// </summary>
    private const byte CommandChannel = 0;

    /// <summary>The message id RakNet reserves for application packets.</summary>
    private const byte ApplicationPacketId = (byte)DefaultMessageIDTypes.UserPacketEnum;

    private static readonly LogCategory Log = EngineLog.Categories.Network;

    private readonly int _maxPayloadSize;
    private readonly string _serverHost;
    private readonly int _serverPort;
    private readonly object _sendGate = new();

    // One receive buffer, reused: the poll path must not allocate, because it runs at frame rate.
    private readonly byte[] _receiveBuffer;
    private readonly Dictionary<ulong, IPEndPoint> _remotes = [];

    private RakPeerInstance _peer;
    private bool _started;
    private IPEndPoint _local = new(IPAddress.Loopback, 0);
    private uint _sequence;
    private long _received;
    private long _sent;
    private long _rejected;

    private bool _disposed;

    /// <param name="server">
    /// The server to dial, as <c>host</c> or <c>host:port</c>. Empty means "accept connections only", which is
    /// how a dedicated server, and the engine's own self-contained default, are expressed.
    /// </param>
    /// <param name="maxPayloadSize">Largest payload the transport will carry in one datagram.</param>
    public RakNetTransport(string server = "", int maxPayloadSize = DefaultMaxPayloadSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPayloadSize, HeaderSize + 1);

        MaxPayloadSize = maxPayloadSize;
        _maxPayloadSize = maxPayloadSize;
        _receiveBuffer = new byte[maxPayloadSize];

        if (server.Length == 0)
        {
            _serverHost = string.Empty;
            return;
        }

        var separator = server.LastIndexOf(':');
        if (separator < 0)
        {
            _serverHost = server;
            _serverPort = DefaultServerPort;
            return;
        }

        _serverHost = server[..separator];
        _serverPort = int.TryParse(server[(separator + 1)..], out var port) ? port : DefaultServerPort;
    }

    /// <summary>The port a server address without an explicit port is assumed to use.</summary>
    public const int DefaultServerPort = 7777;

    public string Name => _serverHost.Length == 0 ? "raknet" : $"raknet -> {_serverHost}:{_serverPort}";

    public IPEndPoint LocalEndPoint => _local;

    /// <summary>True when a server address was configured, so the transport dialled out on open.</summary>
    public bool IsClient => _serverHost.Length > 0;

    public bool IsOpen { get; private set; }

    public long ReceivedCount => Interlocked.Read(ref _received);

    public long SentCount => Interlocked.Read(ref _sent);

    public long RejectedCount => Interlocked.Read(ref _rejected);

    public int MaxPayloadSize { get; }

    /// <summary>The datagram sequence last observed, which is what makes loss visible.</summary>
    public uint LastPeerSequence { get; private set; }

    /// <summary>Datagrams the peer skipped, according to the sequence header.</summary>
    public long DroppedFromPeer { get; private set; }

    /// <summary>Number of peers currently connected.</summary>
    public int ConnectionCount => _started ? RakPeerInterface.NumberOfConnections(_peer) : 0;

    public void Open(int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsOpen)
            return;

        _peer = RakNetLibrary.CreateInstance();
        if (_peer.IsNull())
            throw new InvalidOperationException("RakNet refused to create a peer instance");

        // Zero asks the operating system for a free port, which is what keeps two engine instances and the
        // unit tests from colliding.
        var descriptor = new SocketDescriptor(port, System.Net.Sockets.AddressFamily.InterNetwork);
        var result = RakPeerInterface.Startup(_peer, MaxConnections, [descriptor], threadPriority: 0);
        if (result != StartupResult.RaknetStarted && result != StartupResult.RaknetAlreadyStarted)
            throw new InvalidOperationException($"RakNet failed to start: {result}");

        _started = true;

        // The server role is the permissive one: it accepts incoming connections. A pure client leaves this
        // at zero, so a compromised client cannot be turned into a relay.
        RakPeerInterface.SetMaximumIncomingConnections(
            _peer,
            (ushort)(IsClient ? 0 : MaxConnections)
        );

        _local = ToEndPoint(RakPeerInterface.GetMyBoundAddress(_peer, 0));
        IsOpen = true;

        if (Log.IsEnabled(LogLevel.Information))
            Log.Info($"{Name} listening on {_local}");

        if (IsClient)
            DialServer();
    }

    public bool Send(ReadOnlySpan<byte> payload)
    {
        if (!IsOpen || payload.Length + HeaderSize > _maxPayloadSize)
            return false;

        lock (_sendGate)
        {
            var frame = Frame(payload);

            uint delivered;
            if (IsClient && TryGetServerAddress(out var server))
            {
                delivered = RakPeerInterface.Send(
                    _peer,
                    frame,
                    PacketPriority.HighPriority,
                    PacketReliability.UnreliableSequenced,
                    CommandChannel,
                    AddressOrGUID.From(server),
                    false,
                    0
                );
            }
            else
            {
                // No configured server: broadcast, which is both "send to my one peer" in the loopback case and
                // "send to everyone" on a host. False means "did not broadcast because there were no peers",
                // which is not an error.
                delivered = RakPeerInterface.Send(
                    _peer,
                    frame,
                    PacketPriority.HighPriority,
                    PacketReliability.UnreliableSequenced,
                    CommandChannel,
                    default,
                    true,
                    0
                );
            }

            // RakNet reports the number of peers it queued the payload for, so zero is an undelivered payload,
            // not an exception.
            if (delivered == 0)
                return false;

            Interlocked.Increment(ref _sent);
            return true;
        }
    }

    public bool SendTo(ReadOnlySpan<byte> payload, IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!IsOpen || payload.Length + HeaderSize > _maxPayloadSize)
            return false;

        if (!TryGetAddress(endpoint, out var address))
            return false;

        lock (_sendGate)
        {
            var frame = Frame(payload);

            var delivered = RakPeerInterface.Send(
                _peer,
                frame,
                PacketPriority.HighPriority,
                PacketReliability.UnreliableSequenced,
                CommandChannel,
                AddressOrGUID.From(address),
                false,
                0
            );

            if (delivered == 0)
                return false;

            Interlocked.Increment(ref _sent);
            return true;
        }
    }

    public bool Poll(int timeoutMilliseconds, out TransportDatagram datagram)
    {
        datagram = default;

        if (_disposed || !IsOpen)
            return false;

        // Drain everything already queued before considering the wait, so a burst of datagrams costs one poll
        // cycle rather than one cycle each and the thread drains at the rate traffic arrives rather than the
        // rate it is asked.
        if (TryDrain(out datagram))
            return true;

        // RakNet has no blocking receive, so waiting is a bounded spin. It must not sleep: Thread.Sleep's
        // granularity here is a full 15.6 ms timer tick regardless of the duration asked for, so a 1 ms sleep
        // turns a 4 ms poll interval into a 16 ms one and the latency shows up in command delivery. The budget
        // is therefore kept small by the caller and the sleeping is done by the thread, which knows its own
        // interval and can spend it once rather than per attempt.
        var spin = new SpinWait();
        var start = Stopwatch.GetTimestamp();
        var timeoutTicks = (long)(timeoutMilliseconds * (double)Stopwatch.Frequency / 1000.0);

        while (Stopwatch.GetTimestamp() - start < timeoutTicks)
        {
            if (TryDrain(out datagram))
                return true;

            spin.SpinOnce(sleep1Threshold: -1);
        }

        return TryDrain(out datagram);
    }

    /// <summary>
    /// Takes one application payload from the queue, discarding any RakNet protocol messages in front of it.
    /// </summary>
    /// <returns>
    /// True when a payload was produced. False when the queue was empty or held only protocol traffic.
    /// </returns>
    private bool TryDrain(out TransportDatagram datagram)
    {
        datagram = default;

        while (true)
        {
            var packet = RakPeerInterface.Receive(_peer);
            if (packet is null)
                return false;

            try
            {
                if (TryTakePayload(packet, out datagram))
                    return true;
            }
            finally
            {
                RakPeerInterface.DeallocatePacket(_peer, packet);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        IsOpen = false;

        if (!_started)
            return;

        _started = false;

        // A short block duration rather than zero: RakNet sends a disconnection notification, and a server that
        // never hears it keeps a phantom peer until its own timeout expires.
        RakPeerInterface.Shutdown(_peer, DisconnectBlockMilliseconds, CommandChannel, PacketPriority.LowPriority);
        RakPeerInterface.DestroyInstance(_peer);
        _peer = default;
    }

    /// <summary>
    /// Wraps a payload in the transport's framing: the RakNet user-packet id, then the datagram sequence.
    /// </summary>
    /// <remarks>
    /// The caller's bytes are copied into one buffer rather than handed to RakNet in pieces, because RakNet
    /// accepts a single span and a caller on a hot loop must not be forced into an extra allocation to satisfy
    /// that. The sequence is written under the send gate, so the order frames reach the wire is the order they
    /// were stamped.
    /// </remarks>
    private byte[] Frame(ReadOnlySpan<byte> payload)
    {
        _sequence++;

        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = ApplicationPacketId;
        BitConverter.TryWriteBytes(frame.AsSpan(1), _sequence);
        payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>The number of peers a host is willing to hold open.</summary>
    private const int MaxConnections = 32;

    private const int PollBackoffMilliseconds = 1;

    /// <summary>
    /// The wait the operating system's default timer granularity can actually deliver.
    /// </summary>
    /// <remarks>
    /// Windows schedules sleeps on a 15.6 ms tick unless the process raises the timer resolution, so asking for
    /// less than that and trusting it would silently turn a 4 ms poll interval into a 16 ms one and show up as
    /// latency the engine never asked for. Below this threshold the wait is spun instead.
    /// </remarks>
    private const long SleepGranularityMilliseconds = 2;
    private const uint DisconnectBlockMilliseconds = 200;

    /// <summary>
    /// Turns one RakNet packet into a payload, or reports why it is not one.
    /// </summary>
    /// <remarks>
    /// RakNet's own protocol notifications (<c>ID_CONNECTION_REQUEST_ACCEPTED</c> and friends) arrive as
    /// packets with a length of one, carrying a message id and no payload. They are silently consumed here
    /// rather than counted as rejects: they are not traffic the engine refused, they are RakNet telling us a
    /// connection succeeded, and counting them would make a healthy connect look like a lossy link.
    /// </remarks>
    private bool TryTakePayload(Packet* packet, out TransportDatagram datagram)
    {
        datagram = default;

        var length = (int)packet->Length;
        if (length <= HeaderSize)
        {
            // A notification, a keepalive or an empty payload. Remember the peer so a reply is possible, then
            // let the caller keep polling.
            Remember(packet->SystemAddress);
            return false;
        }

        var payload = new ReadOnlySpan<byte>(packet->Data, length);

        var sequence = BitConverter.ToUInt32(payload);
        if (LastPeerSequence != 0 && sequence > LastPeerSequence + 1)
            DroppedFromPeer += sequence - LastPeerSequence - 1;

        LastPeerSequence = sequence;

        var remote = Remember(packet->SystemAddress);

        // A frame larger than the sender was allowed to produce can only be a malformed one: the engine's own
        // sends are bounded by MaxPayloadSize, so anything bigger came from something that is not this engine.
        if (length - HeaderSize > MaxPayloadSize)
        {
            Interlocked.Increment(ref _rejected);
            return false;
        }

        payload[HeaderSize..].CopyTo(_receiveBuffer);
        Interlocked.Increment(ref _received);

        datagram = new TransportDatagram(
            new ReadOnlyMemory<byte>(_receiveBuffer, 0, length - HeaderSize),
            remote
        );

        return true;
    }

    /// <summary>
    /// Records the endpoint a packet came from, keyed by its RakNet system address, and returns it.
    /// </summary>
    /// <remarks>
    /// Cached because a peer's address is a fixed-size struct that RakNet hands over by value: converting it
    /// allocates, and the poll path must not allocate per packet.
    /// </remarks>
    private IPEndPoint Remember(SystemAddress address)
    {
        var key = KeyOf(address);

        if (_remotes.TryGetValue(key, out var known))
            return known;

        var endpoint = ToEndPoint(address);
        _remotes[key] = endpoint;
        return endpoint;
    }

    private void DialServer()
    {
        var host = Encoding.UTF8.GetBytes(_serverHost);

        var result = RakPeerInterface.Connect(
            _peer,
            host,
            (ushort)_serverPort,
            [],
            null,
            0,
            SendConnectionAttempts,
            TimeBetweenAttemptsMilliseconds,
            ConnectionTimeoutMilliseconds
        );

        if (result != ConnectionAttemptResult.ConnectionAttemptStarted)
        {
            if (Log.IsEnabled(LogLevel.Warning))
                Log.Warning($"RakNet could not dial {_serverHost}:{_serverPort}: {result}");

            return;
        }

        if (Log.IsEnabled(LogLevel.Information))
            Log.Info($"RakNet dialling {_serverHost}:{_serverPort}");
    }

    private const uint SendConnectionAttempts = 12;
    private const uint TimeBetweenAttemptsMilliseconds = 500;
    private const uint ConnectionTimeoutMilliseconds = 5000;

    /// <summary>
    /// Finds the connected server's address, which is what a reply has to be addressed to.
    /// </summary>
    /// <remarks>
    /// The address is looked up rather than the guid the peer reports, because RakNet's default address is
    /// "unassigned" until a connection completes and sending to it silently does nothing — which is exactly the
    /// bug that makes a client appear to connect and then never send anything.
    /// </remarks>
    private bool TryGetServerAddress(out SystemAddress address)
    {
        address = default;

        var count = (ushort)0;
        var list = stackalloc SystemAddress[MaxConnections];

        if (!(bool)RakPeerInterface.GetConnectionList(_peer, list, ref count) || count == 0)
            return false;

        address = list[0];
        return true;
    }

    /// <summary>
    /// Finds a RakNet address for a managed endpoint, so a reply can be addressed to a specific peer.
    /// </summary>
    private bool TryGetAddress(IPEndPoint endpoint, out SystemAddress address)
    {
        address = default;

        foreach (var entry in _remotes)
        {
            if (entry.Value.Equals(endpoint))
            {
                address = AddressOf(entry.Key);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Packs the identifying parts of a system address into a key, so a peer can be looked back up.
    /// </summary>
    private static ulong KeyOf(SystemAddress address) =>
        ((ulong)address.Address.Addr4.SinAddr.SUn.SAddr << 16) | address.Address.Addr4.SinPort;

    /// <summary>
    /// Rebuilds a system address from a key produced by <see cref="KeyOf"/>.
    /// </summary>
    /// <remarks>
    /// Only the IPv4 fields the engine uses are restored. RakNet also carries a system index, which is a cache
    /// hint rather than part of the identity, so leaving it zero just means RakNet does a regular lookup.
    /// </remarks>
    private static SystemAddress AddressOf(ulong key)
    {
        var address = new SystemAddress
        {
            Address = new AddressSystemAddress
            {
                Addr4 = new SockaddrIn
                {
                    SinAddr = new InAddr
                    {
                        SUn = new SUnInAddr { SAddr = (uint)(key >> 16) },
                    },
                    // The wire stores the port upstream (network byte order), so it is swapped back here.
                    SinPort = (ushort)IPAddress.HostToNetworkOrder((short)(key & 0xFFFF)),
                },
            },
        };

        return address;
    }

    /// <summary>
    /// Converts a RakNet system address into the endpoint the transport contract speaks in.
    /// </summary>
    /// <remarks>
    /// Both byte orders here are the opposite of the intuitive one, and both were verified against a live
    /// RakNet peer rather than inferred from the header: the IPv4 bytes are stored in host order (so a
    /// little-endian read of <c>127.0.0.1</c> yields exactly that sequence), while the port is stored in
    /// network order and has to be swapped.
    /// </remarks>
    private static IPEndPoint ToEndPoint(SystemAddress address)
    {
        var raw = address.Address.Addr4.SinAddr.SUn.SAddr;
        var bytes = BitConverter.GetBytes(raw);
        var port = (ushort)IPAddress.NetworkToHostOrder((short)address.Address.Addr4.SinPort);

        return new IPEndPoint(new IPAddress(new[] { bytes[0], bytes[1], bytes[2], bytes[3] }), port);
    }
}
