using System.Net;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Networking;

namespace BrutalTestEngine.Tests.Networking;

/// <summary>
/// The transport seam is only real if the engine can be driven by something that is not the built-in UDP
/// socket. These use a fake transport, which is what proves the abstraction rather than merely describing it.
/// </summary>
/// <remarks>
/// The point is not to test the fake. It is to pin the property that lets the transport decision be deferred:
/// the network thread must accept any <see cref="INetworkTransport"/> and behave identically, so choosing
/// LiteNetLib or GameNetworkingSockets later is a one-line change with no engine edits.
/// </remarks>
[TestFixture]
public class NetworkTransportTests
{
    /// <summary>
    /// A transport that is entirely in-memory. If the engine can be driven through this, it can be driven
    /// through anything.
    /// </summary>
    private sealed class ScriptedTransport : INetworkTransport
    {
        private readonly Queue<byte[]> _pending = new();
        private readonly List<byte[]> _sent = [];

        public string Name => "scripted";

        public IPEndPoint LocalEndPoint { get; private set; } = new(IPAddress.Loopback, 1);

        public bool IsOpen { get; private set; }

        public long ReceivedCount { get; private set; }

        public long SentCount => _sent.Count;

        public long RejectedCount { get; private set; }

        public int MaxPayloadSize => 1400;

        /// <summary>Everything the engine asked to send, in order.</summary>
        public IReadOnlyList<byte[]> Sent
        {
            get
            {
                lock (_sent)
                    return [.. _sent];
            }
        }

        /// <summary>Queues a payload to be handed to the engine on its next poll.</summary>
        public void Deliver(byte[] payload)
        {
            lock (_pending)
                _pending.Enqueue(payload);
        }

        public void Open(int port)
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, port == 0 ? 1 : port);
            IsOpen = true;
        }

        public bool Send(ReadOnlySpan<byte> payload)
        {
            if (payload.Length > MaxPayloadSize)
                return false;

            lock (_sent)
                _sent.Add(payload.ToArray());

            return true;
        }

        public bool SendTo(ReadOnlySpan<byte> payload, IPEndPoint endpoint) => Send(payload);

        public bool Poll(int timeoutMilliseconds, out TransportDatagram datagram)
        {
            lock (_pending)
            {
                if (_pending.Count > 0)
                {
                    var payload = _pending.Dequeue();
                    ReceivedCount++;
                    datagram = new TransportDatagram(payload, LocalEndPoint);
                    return true;
                }
            }

            // The engine polls in a tight loop; sleeping the requested timeout keeps the test from spinning a
            // core while it waits for the engine thread to notice the queue is empty.
            Thread.Sleep(Math.Min(timeoutMilliseconds, 1));
            datagram = default;
            return false;
        }

        public void Dispose() => IsOpen = false;
    }

    [Test]
    public void The_network_thread_drives_whatever_transport_it_is_given()
    {
        var transport = new ScriptedTransport();
        using var bus = new CommandBus();
        var timers = new ThreadTimerRegistry();

        using var thread = new NetworkThread(bus, timers, transport);

        Assert.Multiple(() =>
        {
            Assert.That(thread.Transport, Is.SameAs(transport));
            Assert.That(thread.Transport.Name, Is.EqualTo("scripted"));
            Assert.That(transport.IsOpen, Is.True, "the network thread must open the transport it is given");
        });
    }

    /// <summary>
    /// A datagram handed to the transport must reach the bus as commands, decoded by the shared grammar.
    /// This is the whole contract of the network thread, independent of how the bytes arrived.
    /// </summary>
    [Test]
    public void A_datagram_becomes_commands_on_the_bus()
    {
        var transport = new ScriptedTransport();
        using var bus = new CommandBus();
        var timers = new ThreadTimerRegistry();
        using var thread = new NetworkThread(bus, timers, transport);

        transport.Deliver("tick-rate 120\nkey down Left"u8.ToArray());

        // The engine polls on its own thread, so wait for it rather than assuming a schedule.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (bus.AcceptedCount < 2 && DateTime.UtcNow < deadline)
            Thread.Sleep(5);

        Assert.Multiple(() =>
        {
            Assert.That(thread.CommandsPosted, Is.EqualTo(2));
            Assert.That(bus.AcceptedCount, Is.EqualTo(2));
        });
    }

    /// <summary>
    /// The policy the network thread applies is engine policy, so it has to hold for every transport: a
    /// malformed frame is counted and dropped, never parsed into an intent.
    /// </summary>
    [Test]
    public void Invalid_utf8_is_rejected_rather_than_parsed()
    {
        var transport = new ScriptedTransport();
        using var bus = new CommandBus();
        var timers = new ThreadTimerRegistry();
        using var thread = new NetworkThread(bus, timers, transport);

        // 0xFF is never valid in UTF-8.
        transport.Deliver([0xFF, 0xFE, 0x71, 0x75, 0x69, 0x74]);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (thread.RejectedCount == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(5);

        Assert.Multiple(() =>
        {
            Assert.That(thread.RejectedCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(bus.AcceptedCount, Is.Zero, "a malformed frame must never reach the bus");
        });
    }

    /// <summary>
    /// The default is the built-in UDP transport, so the engine runs and is testable with no third-party
    /// package and no external server.
    /// </summary>
    [Test]
    public void The_default_transport_is_udp()
    {
        using var transport = NetworkTransports.Udp();

        Assert.That(transport, Is.TypeOf<UdpNetworkTransport>());
        Assert.That(transport.Name, Is.EqualTo("udp"));
    }

    /// <summary>
    /// A transport that refuses an oversized payload must be reported as a failed send rather than silently
    /// truncated, because a truncated command is a corrupt intent.
    /// </summary>
    [Test]
    public void An_oversized_payload_is_refused_by_the_udp_transport()
    {
        using var transport = NetworkTransports.Udp();
        transport.Open(0);

        var oversized = new byte[UdpNetworkTransport.DefaultMaxPayloadSize + 1];

        Assert.Multiple(() =>
        {
            Assert.That(transport.Send(oversized), Is.False);
            Assert.That(transport.MaxPayloadSize, Is.EqualTo(UdpNetworkTransport.DefaultMaxPayloadSize));
        });
    }

    /// <summary>
    /// Port zero means "pick a free port". The engine relies on this to run several instances side by side,
    /// and the self-test relies on it to bind without colliding.
    /// </summary>
    [Test]
    public void Opening_on_port_zero_binds_a_real_ephemeral_port()
    {
        using var transport = NetworkTransports.Udp();
        transport.Open(0);

        Assert.Multiple(() =>
        {
            Assert.That(transport.IsOpen, Is.True);
            Assert.That(transport.LocalEndPoint.Port, Is.GreaterThan(0));
        });
    }
}
