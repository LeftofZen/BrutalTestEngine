using System.Net;

namespace BrutalTestEngine.Networking;

/// <summary>
/// A datagram received from a peer, in the transport's own terms.
/// </summary>
/// <param name="Payload">
/// The bytes exactly as they arrived. The transport must not decode or reinterpret them: interpreting a
/// payload is the engine's job, and a transport that guessed would make two transports behave differently on
/// the same input.
/// </param>
/// <param name="Remote">The peer the datagram came from.</param>
public readonly record struct TransportDatagram(ReadOnlyMemory<byte> Payload, IPEndPoint Remote);

/// <summary>
/// The seam between the engine and whatever carries its bytes.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the engine never names a networking library. The simulation, the command bus and the
/// network thread only ever talk to this interface, which means the transport can be chosen later — or
/// changed later — without touching anything that decides what a message means.
/// </para>
/// <para>
/// A transport is deliberately narrow. It opens, it closes, it sends bytes to an endpoint and it hands
/// received bytes back. It knows nothing about sessions, reliability, channels, ordering or commands.
/// Everything above that line is policy, and policy belongs to the engine: the same command stream has to be
/// observable and replayable whether it arrived over UDP today or a reliable ordered channel tomorrow.
/// </para>
/// <para>
/// <b>Threading.</b> A transport is owned by exactly one thread — the one that calls <see cref="Poll"/>.
/// Implementations may therefore keep their socket state unsynchronised, which is what makes the receive
/// path allocation-free. Only <see cref="Send"/>, <see cref="SendTo"/> and the counters are safe to call
/// from any thread.
/// </para>
/// <para>
/// <b>Lifetime.</b> <see cref="Open"/> is called once before polling starts and <see cref="Dispose"/> once
/// after polling stops. An implementation must tolerate being opened on a port of zero, in which case the
/// operating system picks one and reports it through <see cref="LocalEndPoint"/>.
/// </para>
/// </remarks>
public interface INetworkTransport : IDisposable
{
    /// <summary>A name for the transport, used in logs and in the debug panel.</summary>
    string Name { get; }

    /// <summary>
    /// The endpoint this transport is bound to. Only meaningful after <see cref="Open"/>.
    /// </summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>True once <see cref="Open"/> has succeeded and the transport has not been disposed.</summary>
    bool IsOpen { get; }

    /// <summary>Datagrams received and handed to the caller.</summary>
    long ReceivedCount { get; }

    /// <summary>Datagrams sent.</summary>
    long SentCount { get; }

    /// <summary>
    /// Maximum payload size the transport will carry in one datagram, in bytes.
    /// </summary>
    /// <remarks>
    /// Exposed rather than assumed because it is the transport's property, not the engine's: a reliable
    /// message-oriented library may fragment internally and carry far more than a raw UDP socket can. Code
    /// that splits payloads must ask the transport, not hard-code a number.
    /// </remarks>
    int MaxPayloadSize { get; }

    /// <summary>
    /// Binds the transport. Called once, on the thread that will own it.
    /// </summary>
    /// <param name="port">
    /// The port to bind, or zero to let the operating system choose. A transport that speaks to a remote
    /// server may ignore this.
    /// </param>
    void Open(int port);

    /// <summary>
    /// Sends a payload to the transport's configured peer. Safe from any thread; enqueues for the owning
    /// thread when it cannot write immediately.
    /// </summary>
    /// <remarks>Returns false when the payload is too large or the transport is not open.</remarks>
    bool Send(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Sends a payload to a specific endpoint. Safe from any thread.
    /// </summary>
    /// <remarks>
    /// Needed because a peer's address is not always known up front — the self-test drives the receive path
    /// with a genuine datagram from an unrelated socket, and a client-server transport would use this to
    /// reply to whoever spoke last.
    /// </remarks>
    bool SendTo(ReadOnlySpan<byte> payload, IPEndPoint endpoint);

    /// <summary>
    /// Waits for at most <paramref name="timeoutMilliseconds"/> for inbound traffic and returns it. Called
    /// only from the owning thread, which is also the thread that flushes anything <see cref="Send"/>
    /// queued.
    /// </summary>
    /// <returns>
    /// True when a datagram was produced. False on timeout, on a clean close, or when the payload had to be
    /// discarded because it was malformed — in which case the transport increments its rejected count rather
    /// than reporting a fault.
    /// </returns>
    bool Poll(int timeoutMilliseconds, out TransportDatagram datagram);

    /// <summary>Datagrams discarded because they were malformed or oversized.</summary>
    long RejectedCount { get; }
}
