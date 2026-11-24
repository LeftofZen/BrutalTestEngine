using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MSLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace BrutalTestEngine.Host;

/// <summary>
/// Runs a Model Context Protocol server on its own thread, exposing the engine's variables and its command
/// grammar to an AI client.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Debugging a running engine through screenshots is lossy: a screenshot cannot be
/// diffed, cannot be asserted on, and cannot be taken at all when the window is minimised or the machine is
/// headless. This server is the same information as a machine-readable surface, so a client can ask what the
/// engine currently thinks and act on the answer.
/// </para>
/// <para>
/// <b>Why its own thread.</b> The engine's first principle is that no concern shares a thread with another.
/// An MCP client is a network peer that may be slow, may disconnect mid-request, and may hold a connection
/// open for hours; none of that may touch a frame or a tick. The listener therefore runs on a dedicated
/// thread and every request is handled on the thread pool, so a stalled client costs one pool thread and
/// nothing else.
/// </para>
/// <para>
/// <b>Why HTTP rather than stdio.</b> The engine is a long-running windowed process that owns its own stdin
/// (the console command source reads it). A stdio transport would take that away and would tie the server's
/// lifetime to a parent process. Streamable HTTP is the transport that fits a process that is already
/// running and is expected to stay running.
/// </para>
/// <para>
/// <b>Why stateless.</b> The transport is configured stateless so a request carries everything needed to
/// answer it. That removes session bookkeeping, makes a client restart a non-event, and means a tool call
/// cannot be answered from a stale session's view of the engine.
/// </para>
/// <para>
/// <b>Loopback only.</b> Like the web and rcon sources, the listener binds to 127.0.0.1. An unauthenticated
/// endpoint that can issue commands is a vulnerability on a public interface, so it is deliberately not
/// offered.
/// </para>
/// <para>
/// <b>Reads are live.</b> Every variable is read through the <see cref="CvarRegistry"/>, which holds readers
/// rather than values, so a client sees the engine as it is at the moment of the call rather than a snapshot
/// taken at startup.
/// </para>
/// </remarks>
public sealed class McpServerThread : IDisposable
{
    private const int AcceptTimeoutMilliseconds = 16;
    private const int MaxRequestBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly CvarRegistry _cvars;
    private readonly McpCommandSource _commands;
    private readonly Func<EngineStatus> _readStatus;
    private readonly HttpListener _listener;
    private readonly StreamableHttpServerTransport _transport;
    private readonly McpServer _server;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ThreadMetrics _pollMetrics;
    private readonly ThreadMetrics _requestMetrics;
    private readonly Brutal.Logging.LogCategory _log = Logging.EngineLog.Categories.Mcp;

    private long _requests;
    private long _failures;
    private volatile bool _disposed;

    /// <summary>
    /// The server's own loop, held so <see cref="Dispose"/> can wait for it rather than abandoning it.
    /// </summary>
    /// <remarks>
    /// A field rather than a local because the loop outlives the constructor: it is started there and only
    /// completes when the transport is disposed, which happens during shutdown.
    /// </remarks>
    private Task? _serverLoop;

    /// <param name="cvars">The variable registry every read is served from.</param>
    /// <param name="bus">The command bus tool calls are posted to.</param>
    /// <param name="readStatus">Produces the engine's status summary.</param>
    /// <param name="port">The loopback port to listen on.</param>
    /// <param name="timers">Where this thread records its own cost.</param>
    public McpServerThread(
        CvarRegistry cvars,
        CommandBus bus,
        Func<EngineStatus> readStatus,
        int port,
        ThreadTimerRegistry timers
    )
    {
        ArgumentNullException.ThrowIfNull(cvars);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(readStatus);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);

        _cvars = cvars;
        _readStatus = readStatus;
        _commands = new McpCommandSource(bus);
        _pollMetrics = timers.Get(ThreadTimerRegistry.Keys.McpPoll);
        _requestMetrics = timers.Get(ThreadTimerRegistry.Keys.McpRequest);

        _transport = new StreamableHttpServerTransport(null!) { Stateless = true };
        _server = McpServer.Create(_transport, BuildOptions(), null, null);

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        Port = port;

        _thread = new Thread(Run)
        {
            Name = "Mcp",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>The loopback port the listener is bound to.</summary>
    public int Port { get; }

    /// <summary>Number of HTTP requests received.</summary>
    public long RequestCount => Interlocked.Read(ref _requests);

    /// <summary>Number of requests that could not be answered.</summary>
    public long FailureCount => Interlocked.Read(ref _failures);

    /// <summary>Number of commands posted from MCP tool calls.</summary>
    public long CommandsPosted => _commands.CommandsPosted;

    /// <summary>Number of tool calls whose text produced no command.</summary>
    public long RejectedCount => _commands.RejectedCount;

    /// <summary>Number of variables currently exposed.</summary>
    public int CvarCount => _cvars.Count;

    /// <summary>The most recent commands posted from MCP, oldest first.</summary>
    public Command[] RecentCommands => _commands.RecentCommands;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        _thread.Join(TimeSpan.FromSeconds(2));

        // The transport owns the server's inbound channel; disposing it is what ends the server loop.
        try
        {
            _transport.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutting down; a fault here is not actionable.
        }

        try
        {
            _serverLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutting down; a fault here is not actionable.
        }

        _cancellation.Dispose();
    }

    private McpServerOptions BuildOptions() =>
        new()
        {
            ServerInfo = new Implementation
            {
                Name = "brutaltestengine",
                Title = "BrutalTestEngine",
                Version = typeof(McpServerThread).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Description =
                    "Live inspection and control of a running BrutalTestEngine instance. "
                    + "Read engine variables with the cvar tools, and issue engine commands with run_command.",
            },
            ServerInstructions =
                "This server exposes a running game engine. "
                + "Use list_cvars to see every readable engine variable, get_cvar to read one, "
                + "and run_command to issue a command through the engine's own command bus. "
                + "run_command accepts the same grammar as the engine's console; call the help command to see it.",
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability(),
                Resources = new ResourcesCapability(),
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(ListTools()),
                CallToolHandler = (request, _) => ValueTask.FromResult(CallTool(request.Params)),
                ListResourcesHandler = (_, _) => ValueTask.FromResult(ListResources()),
                ListResourceTemplatesHandler = (_, _) => ValueTask.FromResult(ListResourceTemplates()),
                ReadResourceHandler = (request, _) => ValueTask.FromResult(ReadResource(request.Params.Uri)),
            },
        };

    private static ListToolsResult ListTools() =>
        new()
        {
            Tools =
            [
                new Tool
                {
                    Name = "list_cvars",
                    Title = "List engine variables",
                    Description =
                        "Every readable variable the engine currently exposes, with its value, type and meaning. "
                        + "This is the engine's live state as data, rather than as a screenshot.",
                    InputSchema = Schema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                    Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true },
                },
                new Tool
                {
                    Name = "get_cvar",
                    Title = "Read one engine variable",
                    Description = "Reads a single engine variable by name, for example 'engine.frames' or 'net.poll.mean'.",
                    InputSchema = Schema(
                        """
                        {"type":"object","properties":{"name":{"type":"string","description":"The variable name."}},
                        "required":["name"],"additionalProperties":false}
                        """
                    ),
                    Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true },
                },
                new Tool
                {
                    Name = "run_command",
                    Title = "Issue an engine command",
                    Description =
                        "Posts a command to the engine's command bus, using the same grammar as the engine console. "
                        + "Returns the bus sequence number the engine assigned, which is the proof it was accepted. "
                        + "Use 'help' as the text to list every verb.",
                    InputSchema = Schema(
                        """
                        {"type":"object","properties":{"text":{"type":"string","description":"One or more command lines."}},
                        "required":["text"],"additionalProperties":false}
                        """
                    ),
                    Annotations = new ToolAnnotations
                    {
                        ReadOnlyHint = false,
                        DestructiveHint = true,
                        IdempotentHint = false,
                        OpenWorldHint = false,
                    },
                },
                new Tool
                {
                    Name = "engine_status",
                    Title = "Engine status summary",
                    Description =
                        "A one-call summary of the engine: frames, ticks, command counts, thread timings and "
                        + "which optional subsystems are available.",
                    InputSchema = Schema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                    Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true },
                },
                new Tool
                {
                    Name = "recent_commands",
                    Title = "Recent commands",
                    Description = "The most recent commands the engine accepted, oldest first, with their source and target.",
                    InputSchema = Schema("""{"type":"object","properties":{},"additionalProperties":false}"""),
                    Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true },
                },
            ],
        };

    private static ListResourcesResult ListResources() =>
        new()
        {
            Resources =
            [
                new Resource
                {
                    Uri = "bte://cvars",
                    Name = "cvars",
                    Title = "Engine variables",
                    Description = "Every readable engine variable, as a JSON object of name to value.",
                    MimeType = "application/json",
                },
                new Resource
                {
                    Uri = "bte://status",
                    Name = "status",
                    Title = "Engine status",
                    Description = "A summary of the engine's counters and subsystem availability.",
                    MimeType = "application/json",
                },
                new Resource
                {
                    Uri = "bte://commands",
                    Name = "commands",
                    Title = "Recent commands",
                    Description = "The most recent commands the engine accepted.",
                    MimeType = "application/json",
                },
                new Resource
                {
                    Uri = "bte://help",
                    Name = "help",
                    Title = "Command grammar",
                    Description = "The engine's command grammar, as text.",
                    MimeType = "text/plain",
                },
            ],
        };

    private static ListResourceTemplatesResult ListResourceTemplates() =>
        new()
        {
            ResourceTemplates =
            [
                new ResourceTemplate
                {
                    UriTemplate = "bte://cvars/{name}",
                    Name = "cvar",
                    Title = "One engine variable",
                    Description = "A single engine variable by name, as JSON.",
                    MimeType = "application/json",
                },
            ],
        };

    private CallToolResult CallTool(CallToolRequestParams parameters)
    {
        var name = parameters.Name ?? string.Empty;

        return name switch
        {
            "list_cvars" => Text(JsonSerializer.Serialize(CvarMap(), JsonOptions)),
            "get_cvar" => GetCvar(parameters),
            "run_command" => RunCommand(parameters),
            "engine_status" => Text(JsonSerializer.Serialize(StatusDocument(), JsonOptions)),
            "recent_commands" => Text(JsonSerializer.Serialize(CommandDocument(), JsonOptions)),
            _ => Error($"unknown tool '{name}'"),
        };
    }

    private CallToolResult GetCvar(CallToolRequestParams parameters)
    {
        if (!TryArgument(parameters, "name", out var name))
            return Error("the 'name' argument is required");

        if (!_cvars.TryGet(name, out var value))
            return Error($"no variable named '{name}'");

        return Text(
            JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["name"] = value.Name,
                    ["value"] = value.Value,
                    ["kind"] = value.Kind,
                    ["description"] = value.Description,
                },
                JsonOptions
            )
        );
    }

    private CallToolResult RunCommand(CallToolRequestParams parameters)
    {
        if (!TryArgument(parameters, "text", out var text))
            return Error("the 'text' argument is required");

        var sequence = _commands.Post(text, out var count);

        if (count == 0)
            return Error($"'{text}' produced no command; send 'help' to list the verbs");

        return Text(
            JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["accepted"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["sequence"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["note"] =
                        "accepted by the bus; the owning thread applies it on its next drain. "
                        + "Read the effect back with list_cvars.",
                },
                JsonOptions
            )
        );
    }

    private ReadResourceResult ReadResource(string uri)
    {
        var (mimeType, text) = uri switch
        {
            "bte://cvars" => ("application/json", JsonSerializer.Serialize(CvarMap(), JsonOptions)),
            "bte://status" => ("application/json", JsonSerializer.Serialize(StatusDocument(), JsonOptions)),
            "bte://commands" => ("application/json", JsonSerializer.Serialize(CommandDocument(), JsonOptions)),
            "bte://help" => ("text/plain", CommandParser.HelpText),
            _ => ReadTemplatedResource(uri),
        };

        return new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = uri, MimeType = mimeType, Text = text }],
        };
    }

    private (string MimeType, string Text) ReadTemplatedResource(string uri)
    {
        const string prefix = "bte://cvars/";

        if (uri.StartsWith(prefix, StringComparison.Ordinal))
        {
            var name = Uri.UnescapeDataString(uri[prefix.Length..]);

            if (_cvars.TryGet(name, out var value))
            {
                return (
                    "application/json",
                    JsonSerializer.Serialize(
                        new Dictionary<string, string>
                        {
                            ["name"] = value.Name,
                            ["value"] = value.Value,
                            ["kind"] = value.Kind,
                            ["description"] = value.Description,
                        },
                        JsonOptions
                    )
                );
            }

            return ("application/json", $"{{\"error\":\"no variable named '{name}'\"}}");
        }

        return ("application/json", $"{{\"error\":\"unknown resource '{uri}'\"}}");
    }

    private Dictionary<string, string> CvarMap()
    {
        var values = _cvars.Snapshot();
        var map = new Dictionary<string, string>(values.Length, StringComparer.Ordinal);

        foreach (var value in values)
            map[value.Name] = value.Value;

        return map;
    }

    private Dictionary<string, object?> StatusDocument()
    {
        var status = _readStatus();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["frames"] = status.Frames,
            ["ticks"] = status.Ticks,
            ["ticksPerSecond"] = status.TicksPerSecond,
            ["commandsAccepted"] = status.CommandsAccepted,
            ["commandsPending"] = status.CommandsPending,
            ["windowMode"] = status.WindowMode,
            ["networkTransport"] = status.NetworkTransport,
            ["networkEndpoint"] = status.NetworkEndpoint,
            ["physics"] = status.PhysicsBackend,
            ["physicsAvailable"] = status.PhysicsAvailable,
            ["physicsFault"] = status.PhysicsFault,
            ["audio"] = status.AudioBackend,
            ["audioAvailable"] = status.AudioAvailable,
            ["networkFault"] = status.NetworkFault,
            ["renderDocAvailable"] = status.RenderDocAvailable,
            ["logLevel"] = status.LogLevel,
            ["cvarCount"] = _cvars.Count,
            ["mcpRequests"] = RequestCount,
            ["mcpCommandsPosted"] = CommandsPosted,
            ["threadTimings"] = status.ThreadTimings,
        };
    }

    private Dictionary<string, object?>[] CommandDocument()
    {
        var commands = _commands.RecentCommands;
        var document = new Dictionary<string, object?>[commands.Length];

        for (var i = 0; i < commands.Length; i++)
        {
            var command = commands[i];
            document[i] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["sequence"] = command.Sequence,
                ["source"] = command.Source.ToString(),
                ["target"] = command.Target.ToString(),
                ["description"] = command.Describe(),
            };
        }

        return document;
    }

    private static bool TryArgument(CallToolRequestParams parameters, string name, out string value)
    {
        value = string.Empty;

        if (parameters.Arguments is not { } arguments || !arguments.TryGetValue(name, out var element))
            return false;

        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => element.ToString(),
        };

        return value.Length > 0;
    }

    private static CallToolResult Text(string text) =>
        new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private void Run()
    {
        var token = _cancellation.Token;

        // The server's own loop reads the transport's inbound channel and writes replies back through it.
        // It is started here, on this thread, so the whole MCP surface has one owner.
        _serverLoop = _server.RunAsync(token);

        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                // BeginGetContext plus a wait, rather than the blocking GetContext, so the loop still observes
                // cancellation promptly when the engine shuts down.
                var pending = _listener.BeginGetContext(null, null);
                while (!pending.AsyncWaitHandle.WaitOne(AcceptTimeoutMilliseconds))
                {
                    if (token.IsCancellationRequested)
                        return;
                }

                // Timed around the accept itself, not around the wait for a request to arrive. Folding the
                // idle wait in would make this metric read back as "how long since the last request", which
                // is a property of the client rather than of the engine.
                var acceptStartedAt = Stopwatch.GetTimestamp();
                context = _listener.EndGetContext(pending);
                _pollMetrics.RecordSince(acceptStartedAt);
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            // Handled on the thread pool so a client that reads its response slowly cannot hold up the accept
            // loop, and so a stalled client costs one pool thread rather than the MCP thread.
            _ = Task.Run(() => Handle(context), CancellationToken.None);
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var startedAt = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _requests);

        try
        {
            var request = context.Request;

            switch (request.HttpMethod)
            {
                case "POST":
                    HandlePost(context);
                    break;

                case "GET":
                    // The standalone server-to-client stream. Nothing here pushes unsolicited messages, but a
                    // client is entitled to open it, so it is served rather than refused.
                    context.Response.ContentType = "text/event-stream";
                    context.Response.StatusCode = 200;
                    _transport.HandleGetRequestAsync(context.Response.OutputStream, _cancellation.Token)
                        .GetAwaiter()
                        .GetResult();
                    break;

                case "DELETE":
                    // Session termination. The transport is stateless, so there is nothing to tear down.
                    context.Response.StatusCode = 204;
                    context.Response.Close();
                    break;

                default:
                    context.Response.StatusCode = 405;
                    context.Response.Close();
                    break;
            }
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _failures);

            if (_log.IsEnabled(MSLogLevel.Warning))
                _log.Warning($"MCP request failed: {exception.GetType().Name}: {exception.Message}");

            TryFail(context);
        }
        finally
        {
            _requestMetrics.RecordSince(startedAt);
        }
    }

    private void HandlePost(HttpListenerContext context)
    {
        var body = ReadBody(context.Request);

        if (body.Length == 0)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        JsonRpcMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<JsonRpcMessage>(body, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        if (message is null)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        // The reply is an SSE stream, which is what the streamable HTTP transport produces and what a client
        // expects to read. Sending it as application/json makes the client wait for a reply that never
        // arrives, because it parses the body as an event stream.
        context.Response.ContentType = "text/event-stream";
        context.Response.StatusCode = 200;

        _transport.HandlePostRequestAsync(message, context.Response.OutputStream, _cancellation.Token)
            .GetAwaiter()
            .GetResult();

        context.Response.OutputStream.Flush();
        context.Response.OutputStream.Close();
    }

    private static string ReadBody(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
            return string.Empty;

        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var buffer = new char[MaxRequestBytes];
        var read = reader.Read(buffer, 0, buffer.Length);
        return read <= 0 ? string.Empty : new string(buffer, 0, read);
    }

    private static void TryFail(HttpListenerContext context)
    {
        try
        {
            context.Response.StatusCode = 500;
            context.Response.Close();
        }
        catch (ObjectDisposedException)
        {
            // The client went away.
        }
        catch (HttpListenerException)
        {
            // The connection is already gone.
        }
    }
}
