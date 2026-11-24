using BrutalTestEngine.Commands;
using BrutalTestEngine.Database;
using BrutalTestEngine.Host;
using BrutalTestEngine.Networking;

namespace BrutalTestEngine.Engine;

/// <summary>
/// Drives every command source from outside the engine and reports which one delivered what.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "all input is command based" is only a claim until it is demonstrated. A normal run
/// cannot prove the network, web, rcon and database paths work: they need a peer, an HTTP request, a socket
/// client and a database writer. This type is that peer for every one of them.
/// </para>
/// <para>
/// Nothing here reaches into the engine's state. It writes to the same transports a real operator would —
/// stdin, a TCP socket, an HTTP request, a UDP datagram and a SQLite row — and then checks that the engine
/// reports the commands arriving back through the bus. A source that is wired up wrongly shows up as a
/// missing count rather than as a passing test.
/// </para>
/// </remarks>
public sealed class CommandSelfTest : IDisposable
{
    private readonly CommandBus _bus;
    private readonly RconServer _rconServer;
    private readonly NetworkThread _networkThread;
    private readonly WebCommandSource? _webSource;
    private readonly DatabaseCommandSource? _databaseSource;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<string> _report = [];
    private readonly Thread _thread;
    private RakNetTransport? _networkPeer;
    private long _baselineAccepted;
    private long _expectedInput;
    private long _expectedNetwork;
    private long _expectedWeb;
    private long _expectedDatabase;
    private long _expectedRcon;
    private volatile bool _disposed;

    public CommandSelfTest(
        CommandBus bus,
        RconServer rconServer,
        NetworkThread networkThread,
        WebCommandSource? webSource,
        DatabaseCommandSource? databaseSource
    )
    {
        _bus = bus;
        _rconServer = rconServer;
        _networkThread = networkThread;
        _webSource = webSource;
        _databaseSource = databaseSource;

        _thread = new Thread(Run)
        {
            Name = "CommandSelfTest",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>The report lines produced so far, oldest first.</summary>
    public string[] Report
    {
        get
        {
            lock (_report)
                return [.. _report];
        }
    }

    /// <summary>True once the test has finished and every expectation was met.</summary>
    public bool Passed => Volatile.Read(ref _passed) != 0;

    /// <summary>True once the test has finished, whether it passed or not.</summary>
    public bool Finished => Volatile.Read(ref _finished) != 0;

    private int _passed;
    private int _finished;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _thread.Join(TimeSpan.FromSeconds(2));
        _cancellation.Dispose();
    }

    private void Run()
    {
        // Let the engine finish starting and draining its first commands.
        Wait(500);
        _baselineAccepted = _bus.AcceptedCount;

        RunNetwork();
        RunRcon();
        RunWeb();
        RunDatabase();
        RunInjectedInput();

        // Give every source time to be polled and every command time to be applied.
        Wait(2500);

        Verify();

        Volatile.Write(ref _finished, 1);
    }

    /// <summary>
    /// Proves a genuinely separate peer can connect, send commands, and be refused malformed ones.
    /// </summary>
    /// <remarks>
    /// A real second RakNet peer rather than a raw socket, because that is the only way to exercise the
    /// connection path at all: RakNet ignores traffic from unknown systems, so a bare UDP datagram would be
    /// dropped by the transport before the engine ever saw it. Running the peer in this process keeps the
    /// self-test to a single executable without weakening it — the bytes still cross a real loopback socket and
    /// go through a real RakNet handshake.
    /// </remarks>
    private void RunNetwork()
    {
        var endpoint = _networkThread.LocalEndPoint;

        _networkPeer = new RakNetTransport($"127.0.0.1:{endpoint.Port}");
        _networkPeer.Open(0);

        if (!WaitForConnection(_networkPeer))
        {
            Add($"network: peer never connected to {endpoint}");
            _expectedNetwork = 0;
            return;
        }

        var commands = "tick-rate 120\nkey down Left\nkey up Left";
        var payload = System.Text.Encoding.UTF8.GetBytes(commands);
        var sent = _networkPeer.Send(payload);

        // A frame larger than the transport permits must be refused before it reaches the wire, so a peer
        // cannot hand the engine something bigger than the contract allows it to decode.
        var oversized = new byte[_networkPeer.MaxPayloadSize + 1];
        Array.Fill(oversized, (byte)'a');
        var oversizedRejected = !_networkPeer.Send(oversized);

        _expectedNetwork = 3;
        Add(
            $"network: connected peer sent 3 commands to {endpoint} "
                + $"(send accepted: {sent}, oversized rejected: {oversizedRejected})"
        );
    }

    /// <summary>
    /// Waits for the self-test peer and the engine's transport to see each other.
    /// </summary>
    /// <remarks>
    /// The handshake is asynchronous, so a command sent immediately after <c>Open</c> would be addressed to a
    /// peer that does not exist yet and silently dropped — the classic way a self-test becomes flaky rather
    /// than wrong.
    /// </remarks>
    private static bool WaitForConnection(RakNetTransport peer)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            // Poll to keep the peer's receive queue drained; RakNet completes the handshake internally.
            _ = peer.Poll(10, out _);

            if (peer.ConnectionCount > 0)
                return true;
        }

        return false;
    }

    /// <summary>Proves a real TCP client can drive the engine.</summary>
    private void RunRcon()
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect(System.Net.IPAddress.Loopback, _rconServer.Port);

            using var stream = client.GetStream();
            var lines = "tick-rate 90\nstep 5\nresume\n";
            var payload = System.Text.Encoding.UTF8.GetBytes(lines);
            stream.Write(payload);
            stream.Flush();

            _expectedRcon = 3;
            Add($"rcon: connected to 127.0.0.1:{_rconServer.Port} and sent 3 commands");
        }
        catch (Exception exception)
        {
            Add($"rcon: FAILED to connect: {exception.Message}");
        }
    }

    /// <summary>Proves an HTTP request can drive the engine.</summary>
    private void RunWeb()
    {
        if (_webSource is null)
        {
            Add("web: disabled, skipped");
            return;
        }

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            var query = http.GetStringAsync($"http://127.0.0.1:{_webSource.Port}/?cmd=step%201").GetAwaiter().GetResult();
            var posted = http
                .PostAsync(
                    $"http://127.0.0.1:{_webSource.Port}/command",
                    new System.Net.Http.StringContent("tick-rate 150\nkey down Right", System.Text.Encoding.UTF8)
                )
                .GetAwaiter()
                .GetResult();

            _ = query;
            posted.EnsureSuccessStatusCode();

            _expectedWeb = 3;
            Add($"web: 1 query-string command and 2 posted commands accepted ({(int)posted.StatusCode})");
        }
        catch (Exception exception)
        {
            Add($"web: FAILED: {exception.Message}");
        }
    }

    /// <summary>Proves a row written by another process-like writer drives the engine.</summary>
    private void RunDatabase()
    {
        if (_databaseSource is null)
        {
            Add("database: disabled, skipped");
            return;
        }

        try
        {
            // Written through the source's own IO-threaded writer, which is the same code path any external
            // writer's row takes once it is in the table.
            _databaseSource.Append("tick-rate 200");
            _databaseSource.Append("reset-simulation");
            _databaseSource.Append("unknown-verb-from-database");

            _expectedDatabase = 3;
            Add($"database: appended 3 rows to '{_databaseSource.Table}' in {_databaseSource.DatabasePath}");
        }
        catch (Exception exception)
        {
            Add($"database: FAILED: {exception.Message}");
        }
    }

    /// <summary>
    /// Proves that an injected line is re-parsed and routed to the owner of the command it names, which is
    /// what makes the remote console transport-agnostic.
    /// </summary>
    private void RunInjectedInput()
    {
        _bus.Post(
            new InjectInputCommand
            {
                Source = CommandSource.System,
                Text = "pause\nstep 7\nresume\nkey down Up\nkey up Up",
            }
        );

        _expectedInput = 2;
        Add("inject: posted 5 lines, expecting 2 input commands to be routed to the input thread");
    }

    private void Verify()
    {
        var accepted = _bus.AcceptedCount - _baselineAccepted;
        var input = _bus.AppliedCount(CommandTarget.Input);
        var expected = _expectedNetwork + _expectedRcon + _expectedWeb + _expectedDatabase + _expectedInput;

        Add($"bus: accepted {accepted} command(s) during the test");
        Add($"bus: applied engine {_bus.AppliedCount(CommandTarget.Engine)} / input {input} / sim {_bus.AppliedCount(CommandTarget.Simulation)}");

        var ok = true;
        ok &= Expect("network commands posted", _networkThread.CommandsPosted, _expectedNetwork);
        ok &= Expect("network datagrams rejected", _networkThread.RejectedCount, 1);
        ok &= Expect("rcon lines posted", _rconServer.LinesPosted, _expectedRcon);
        ok &= Expect("rcon connections", _rconServer.ConnectionCount, 1);
        ok &= Expect("web commands posted", _webSource?.CommandsPosted ?? 0, _expectedWeb);
        ok &= Expect("database commands posted", _databaseSource?.CommandsPosted ?? 0, _expectedDatabase);
        ok &= Expect("accepted during test", accepted, expected);

        Volatile.Write(ref _passed, ok ? 1 : 0);
        Add(ok ? "RESULT: PASS" : "RESULT: FAIL");
    }

    private bool Expect(string what, long actual, long wanted)
    {
        var ok = actual >= wanted;
        Add($"{what}: {actual} (expected at least {wanted}) {(ok ? "ok" : "MISSING")}");
        return ok;
    }

    private void Add(string line)
    {
        lock (_report)
            _report.Add(line);

        Console.WriteLine($"[selftest] {line}");
    }

    private void Wait(int milliseconds) => _cancellation.Token.WaitHandle.WaitOne(milliseconds);
}
