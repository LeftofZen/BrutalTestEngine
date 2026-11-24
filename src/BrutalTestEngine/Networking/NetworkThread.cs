using System.Net;
using System.Text;
using Brutal.Logging;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Logging;
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
/// The thread is deliberately ignorant of <i>how</i> the bytes arrive. It drives an
/// <see cref="INetworkTransport"/>, which is the only type in the engine that names a networking library:
/// swapping UDP for a reliable message transport is a constructor argument here and nothing else. The policy
/// that stays in this class — size limits, UTF-8 validation, command batching — is engine policy and must
/// apply identically whatever carries the bytes.
/// </para>
/// <para>
/// A dedicated thread rather than the thread pool keeps transport ownership and ordering explicit, and keeps
/// the engine's thread count predictable.
/// </para>
/// </remarks>
public sealed class NetworkThread : IDisposable
{
    private const int ReceiveTimeoutMilliseconds = 2;

    /// <summary>
    /// How long to idle when the transport had nothing.
    /// </summary>
    /// <remarks>
    /// The one place the thread deliberately does nothing. It is a single wait per idle cycle rather than a wait
    /// per poll attempt, which matters because Windows rounds any sleep up to its 15.6 ms timer tick: paying
    /// that once per interval keeps command latency at a few milliseconds instead of a couple of dozen.
    /// </remarks>
    private const int IdleMilliseconds = 2;
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
    private readonly INetworkTransport _transport;
    private readonly MpscQueue<byte[]> _outgoing = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ThreadMetrics _pollMetrics;
    private readonly ThreadMetrics _decodeMetrics;
    private readonly LogCategory _log = EngineLog.Categories.Network;
    private long _commandsPosted;
    private long _decodeRejected;
    private volatile bool _disposed;

    /// <param name="bus">The command bus received messages are posted to.</param>
    /// <param name="timers">Where this thread records how long a poll cycle and a decode take.</param>
    /// <param name="transport">
    /// The transport to drive. This thread takes ownership and disposes it. Defaults to loopback UDP so the
    /// engine stays self-contained.
    /// </param>
    /// <param name="port">The port to bind. Zero picks a free port.</param>
    public NetworkThread(
        CommandBus bus,
        ThreadTimerRegistry timers,
        INetworkTransport? transport = null,
        int port = 0
    )
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(timers);

        _bus = bus;
        _transport = transport ?? new RakNetTransport();
        _pollMetrics = timers.Get(ThreadTimerRegistry.Keys.NetworkPoll);
        _decodeMetrics = timers.Get(ThreadTimerRegistry.Keys.NetworkDecode);

        _transport.Open(port);

        if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information))
            _log.Info($"transport '{_transport.Name}' listening on {_transport.LocalEndPoint}");

        _thread = new Thread(Run)
        {
            Name = "Network",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>The transport driving this thread. Exposed for diagnostics and for the self-test.</summary>
    public INetworkTransport Transport => _transport;

    /// <summary>The endpoint the transport is bound to.</summary>
    public IPEndPoint LocalEndPoint => _transport.LocalEndPoint;

    /// <summary>Number of datagrams received.</summary>
    public long ReceivedCount => _transport.ReceivedCount;

    /// <summary>Number of datagrams sent.</summary>
    public long SentCount => _transport.SentCount;

    /// <summary>Number of commands posted to the bus from received datagrams.</summary>
    public long CommandsPosted => Interlocked.Read(ref _commandsPosted);

    /// <summary>
    /// Number of datagrams that could not be decoded and were discarded.
    /// </summary>
    /// <remarks>
    /// Sums the transport's own rejections (oversized frames) with this thread's decode rejections (bad UTF-8,
    /// too long, too many commands). Both mean "the engine refused this frame", and a caller asking the
    /// question does not care which stage refused it.
    /// </remarks>
    public long RejectedCount => _transport.RejectedCount + Interlocked.Read(ref _decodeRejected);

    /// <summary>Raised on the network thread when the transport fails.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Sends a datagram to the transport's peer. Safe from any thread.</summary>
    public void Send(byte[] payload)
    {
        if (_disposed)
            return;

        _outgoing.Enqueue(payload);
    }

    /// <summary>
    /// Encodes a command with the shared grammar and sends it to the transport's peer, so it comes back in
    /// through the real transport as a network command.
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
    /// datagram rather than only a transport echo.
    /// </summary>
    public bool SendTo(ReadOnlySpan<byte> payload, IPEndPoint endpoint) =>
        _transport.SendTo(payload, endpoint);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _outgoing.Complete();
        _thread.Join(TimeSpan.FromSeconds(2));
        _transport.Dispose();
        _cancellation.Dispose();
        _outgoing.Dispose();
    }

    private void Run()
    {
        var token = _cancellation.Token;
        var text = new StringBuilder(MaxPayloadCharacters);

        while (!token.IsCancellationRequested)
        {
            try
            {
                while (_outgoing.TryDequeue(out var payload))
                    _transport.Send(payload);

                var pollStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var received = _transport.Poll(ReceiveTimeoutMilliseconds, out var datagram);
                _pollMetrics.RecordSince(pollStartedAt);

                if (!received)
                {
                    // The transport's poll is a bounded spin, so the idle time is spent here rather than inside
                    // it: one sleep for the whole interval instead of one per attempt. Sleeping on Windows costs
                    // a full timer tick whatever duration is asked for, so a per-attempt sleep would multiply that
                    // cost by the number of attempts in the interval.
                    token.WaitHandle.WaitOne(IdleMilliseconds);
                    continue;
                }

                var decodeStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    Decode(datagram.Payload.Span, text);
                }
                finally
                {
                    _decodeMetrics.RecordSince(decodeStartedAt);
                }
            }
            catch (Exception exception)
            {
                if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error))
                    _log.Error(exception, $"network transport fault: {exception.Message}");

                Faulted?.Invoke(exception);
                break;
            }
        }
    }

    private void Decode(ReadOnlySpan<byte> payload, StringBuilder text)
    {
        text.Clear();

        // A frame that is not valid UTF-8, or that decodes to something absurdly long, is discarded here
        // rather than parsed into garbage commands: a malformed transport frame is a transport problem, not
        // an intent, and the engine must never act on a mangled one.
        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            Reject("payload was not valid UTF-8");
            return;
        }

        if (decoded.Length > MaxPayloadCharacters)
        {
            Reject($"payload exceeded {MaxPayloadCharacters} characters");
            return;
        }

        text.Append(decoded);

        var commands = CommandParser.ParseMany(text.ToString(), CommandSource.Network);
        if (commands.Count > MaxCommandsPerDatagram)
        {
            Reject($"payload carried more than {MaxCommandsPerDatagram} commands");
            return;
        }

        foreach (var command in commands)
        {
            _bus.Post(command);
            Interlocked.Increment(ref _commandsPosted);
        }
    }

    private void Reject(string reason)
    {
        Interlocked.Increment(ref _decodeRejected);

        if (_log.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            _log.Debug($"rejected datagram: {reason}");
    }
}
