using System.Net;
using System.Text;
using BrutalTestEngine.Commands;

namespace BrutalTestEngine.Host;

/// <summary>A request the web listener decoded and posted as a command.</summary>
/// <param name="Method">The HTTP method the client used.</param>
/// <param name="Path">The request path.</param>
/// <param name="Body">The command text, trimmed.</param>
public readonly record struct WebEvent(string Method, string Path, string Body);

/// <summary>
/// Runs a minimal HTTP listener on the loopback interface and turns each request into a command.
/// </summary>
/// <remarks>
/// <para>
/// The web surface is another transport for the same grammar, and the one a live-ops dashboard would use.
/// A request body, or a <c>?cmd=</c> query parameter so a browser address bar works, is a command line: it
/// is parsed with the shared parser and posted to the bus. Nothing here knows what any command does.
/// </para>
/// <para>
/// The listeners are served on their own threads so a slow client cannot stall the accept loop, and the
/// recent-request history is bounded so a busy listener cannot grow without limit.
/// </para>
/// <para>
/// The listener is loopback-only for the same reason as the remote console: an unauthenticated command
/// endpoint on a public interface would be a vulnerability, so it is deliberately not offered.
/// </para>
/// </remarks>
public sealed class WebCommandSource : IDisposable
{
    private const int MaxRequestBytes = 8192;
    private const int MaxRecentEvents = 256;

    private readonly CommandBus _bus;
    private readonly HttpListener _listener;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Queue<WebEvent> _recent = new();
    private readonly Lock _recentGate = new();
    private long _requests;
    private long _commandsPosted;
    private volatile bool _disposed;

    /// <param name="bus">The command bus requests are posted to.</param>
    /// <param name="port">The loopback port to listen on.</param>
    public WebCommandSource(CommandBus bus, int port)
    {
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);

        _bus = bus;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        Port = port;

        _thread = new Thread(Run)
        {
            Name = "Web",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>The loopback port the listener is bound to.</summary>
    public int Port { get; }

    /// <summary>Number of HTTP requests received.</summary>
    public long RequestCount => Interlocked.Read(ref _requests);

    /// <summary>Number of commands posted to the bus from web requests.</summary>
    public long CommandsPosted => Interlocked.Read(ref _commandsPosted);

    /// <summary>The most recent requests, oldest first, for the debug UI.</summary>
    public WebEvent[] RecentEvents
    {
        get
        {
            lock (_recentGate)
                return [.. _recent];
        }
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
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        _thread.Join(TimeSpan.FromSeconds(1));
        _cancellation.Dispose();
    }

    private void Run()
    {
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                // BeginGetContext plus a wait, rather than the blocking GetContext, so the loop still
                // observes cancellation promptly when the engine shuts down.
                var pending = _listener.BeginGetContext(null, null);
                while (!pending.AsyncWaitHandle.WaitOne(AcceptTimeoutMilliseconds))
                {
                    if (token.IsCancellationRequested)
                        return;
                }

                context = _listener.EndGetContext(pending);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            // Hand the request to its own thread so a client that reads its response slowly cannot hold up
            // the accept loop.
            var worker = new Thread(() => Handle(context))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            worker.Start();
        }
    }

    private const int AcceptTimeoutMilliseconds = 16;

    private void Handle(HttpListenerContext context)
    {
        Interlocked.Increment(ref _requests);

        var request = context.Request;
        var method = request.HttpMethod;
        var path = request.Url?.AbsolutePath ?? "/";

        var body = ReadBody(request);

        // The command may arrive in the body or the query string, so a request can be made from a browser
        // address bar as well as programmatically.
        if (body.Length == 0)
            body = request.QueryString["cmd"] ?? string.Empty;

        body = body.Trim();

        RecordEvent(new WebEvent(method, path, body));

        if (body.Length > 0 && method is "GET" or "POST")
        {
            foreach (var command in CommandParser.ParseMany(body, CommandSource.Web))
            {
                _bus.Post(command);
                Interlocked.Increment(ref _commandsPosted);
            }
        }

        try
        {
            var response = context.Response;
            var isIndex = path is "/" or "/index.html";
            var payload = Encoding.UTF8.GetBytes(isIndex ? IndexPage : $"accepted {body}\n");

            response.ContentType = isIndex ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";
            response.ContentLength64 = payload.Length;
            response.OutputStream.Write(payload);
            response.OutputStream.Close();
        }
        catch (IOException)
        {
            // The client went away before the reply was written.
        }
        catch (ObjectDisposedException)
        {
            // Listener shut down.
        }
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

    private void RecordEvent(WebEvent webEvent)
    {
        lock (_recentGate)
        {
            _recent.Enqueue(webEvent);

            // Bounded, so a busy listener cannot grow without limit.
            while (_recent.Count > MaxRecentEvents)
                _recent.Dequeue();
        }
    }

    private const string IndexPage =
        "<!doctype html><meta charset=\"utf-8\"><title>BrutalTestEngine</title>"
        + "<h1>BrutalTestEngine command endpoint</h1>"
        + "<p>Requests are parsed with the same grammar as every other source.</p>"
        + "<form method=\"get\"><input name=\"cmd\" value=\"help\" size=\"60\"><button>Send</button></form>"
        + "<ul>"
        + "<li><a href=\"/?cmd=tick-rate+120\">tick-rate 120</a>"
        + "<li><a href=\"/?cmd=pause\">pause</a>"
        + "<li><a href=\"/?cmd=resume\">resume</a>"
        + "<li><a href=\"/?cmd=key+down+Left\">key down Left</a>"
        + "<li><a href=\"/?cmd=key+up+Left\">key up Left</a>"
        + "<li><a href=\"/?cmd=reset-simulation\">reset-simulation</a>"
        + "<li><a href=\"/?cmd=window-mode+toggle\">window-mode toggle</a>"
        + "</ul>";
}
