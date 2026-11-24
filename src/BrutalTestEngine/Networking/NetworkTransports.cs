namespace BrutalTestEngine.Networking;

/// <summary>
/// Chooses which transport the engine runs on.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place a concrete networking library is named. The engine depends on
/// <see cref="INetworkTransport"/>; a host picks an implementation here and nothing above the transport line
/// changes. The transport is RakNet because that is what BRUTAL ships, but the seam is what makes that a
/// reversible decision rather than a commitment baked through the simulation.
/// </para>
/// <para>
/// A <see cref="Func{TResult}"/> factory rather than an enum because a transport is configured at
/// construction — server address, reliability settings, channel layout, certificate — and an enum would force
/// all of that into option properties that only one transport understands. A factory keeps those details with
/// the transport that owns them.
/// </para>
/// </remarks>
public static class NetworkTransports
{
    /// <summary>
    /// The engine's default: BRUTAL's RakNet binding with no server configured, so it listens for peers and
    /// broadcasts to whoever connects. This is what makes an unconfigured engine still exercise the real
    /// transport rather than a stub.
    /// </summary>
    public static Func<INetworkTransport> RakNet { get; } = static () => new RakNetTransport();

    /// <summary>
    /// A RakNet transport that dials <paramref name="server"/> as a client, given as <c>host</c> or
    /// <c>host:port</c>, and keeps accepting inbound connections as well.
    /// </summary>
    public static Func<INetworkTransport> RakNetClient(string server) =>
        () => new RakNetTransport(server);

    /// <summary>
    /// The engine's original built-in transport: a bare UDP socket on loopback with no reliability and no
    /// connection concept.
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted because it is the reference implementation of
    /// <see cref="INetworkTransport"/> — the smallest thing that satisfies the contract — and because the
    /// network tests use it to prove the engine depends on the seam and not on RakNet.
    /// </remarks>
    public static Func<INetworkTransport> Udp { get; } = static () => new UdpNetworkTransport();
}
