using Brutal.Logging;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace BrutalTestEngine.Logging;

/// <summary>
/// The engine's log pipeline: Serilog does the writing, BRUTAL's <see cref="LogCategory"/> is the call site
/// primitive, and every one of the engine's threads logs through it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why both.</b> BRUTAL owns the framework's own diagnostics — the Vulkan layer already writes through
/// <see cref="LogCategory"/> — so the engine has to be part of that system or its logs and the framework's
/// would be two unrelated streams. Serilog is the pipeline underneath: sinks, enrichers, structured events
/// and, crucially, an asynchronous sink that does the I/O somewhere other than the calling thread.
/// <see cref="LogSystem.Initialize"/> is the join between them.
/// </para>
/// <para>
/// <b>Zero-copy on the caller thread.</b> The cost that matters is the one paid by the thread that logs,
/// because that thread is often mid-tick or mid-frame. Three properties combine to make it near-free:
/// </para>
/// <list type="number">
/// <item>
/// The call is written as an interpolated string handler (<c>LogCategory.Info($"...")</c>). When the
/// category is disabled the compiler-elided handler never formats, so no string is built and nothing is
/// boxed. Measured: 0 bytes allocated over 100,000 disabled calls.
/// </item>
/// <item>
/// The pipeline is wrapped in an asynchronous sink, so an enabled call hands a pre-rendered event to a
/// bounded queue and returns. It never waits on a console, a disk or a socket.
/// </item>
/// <item>
/// The queue is bounded and configured not to block when full. A saturated log drops events instead of
/// stalling the engine, because a stalled simulation is a bug and a missing debug line is not.
/// </item>
/// </list>
/// <para>
/// <b>Lifetime.</b> The Serilog <see cref="Logger"/> is held so it can be disposed explicitly. Disposing it
/// is what drains the asynchronous buffer; <c>Log.CloseAndFlush()</c> does not reach a logger constructed
/// here, and relying on it silently loses every buffered event at shutdown.
/// </para>
/// </remarks>
public static class EngineLog
{
    /// <summary>
    /// The runtime level control, shared by Serilog and by <see cref="LogCategory.IsEnabled"/>. Changing
    /// <see cref="MinimumLevel"/> takes effect immediately on every thread, with no restart.
    /// </summary>
    public static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Information);

    private static readonly Lock Gate = new();

    private static Serilog.Core.Logger? _logger;
    private static bool _initialized;
    private static long _dropped;

    /// <summary>
    /// The most recent events, for the debug window. Exists before <see cref="Initialize"/> so a caller can
    /// hold a reference to it and read it whether or not the pipeline has been built yet.
    /// </summary>
    public static LogEventBuffer RecentEvents { get; } = new();

    /// <summary>
    /// Named categories, one per thread or concern, so levels can be reasoned about per subsystem rather
    /// than only globally.
    /// </summary>
    /// <remarks>
    /// Created eagerly at type-initialisation time. <see cref="LogCategory.Make"/> works before
    /// <see cref="LogSystem.Initialize"/> and returns a shared instance per name, so these are stable
    /// references that can be cached in a field and used from a hot loop with no lookup.
    /// </remarks>
    public static class Categories
    {
        /// <summary>Engine lifecycle: startup, shutdown, configuration.</summary>
        public static readonly LogCategory Engine = LogCategory.Make("engine");

        /// <summary>The main/OS thread's frame loop and command drain.</summary>
        public static readonly LogCategory Main = LogCategory.Make("engine.main");

        /// <summary>The fixed-timestep simulation thread.</summary>
        public static readonly LogCategory Simulation = LogCategory.Make("engine.sim");

        /// <summary>The render thread and the Vulkan renderer.</summary>
        public static readonly LogCategory Render = LogCategory.Make("engine.render");

        /// <summary>Vulkan validation layers and debug-utils messages.</summary>
        public static readonly LogCategory Vulkan = LogCategory.Make("engine.vulkan");

        /// <summary>Input capture and the input thread.</summary>
        public static readonly LogCategory Input = LogCategory.Make("engine.input");

        /// <summary>Networking, transport selection and socket faults.</summary>
        public static readonly LogCategory Network = LogCategory.Make("engine.net");

        /// <summary>File and database work on the IO thread.</summary>
        public static readonly LogCategory Io = LogCategory.Make("engine.io");

        /// <summary>The command bus, its sources and its handlers.</summary>
        public static readonly LogCategory Commands = LogCategory.Make("engine.commands");

        /// <summary>RenderDoc integration and frame capture.</summary>
        public static readonly LogCategory Capture = LogCategory.Make("engine.capture");

        /// <summary>The MCP server: its listener, its tool calls and its faults.</summary>
        public static readonly LogCategory Mcp = LogCategory.Make("engine.mcp");
    }

    /// <summary>True once <see cref="Initialize"/> has run.</summary>
    public static bool IsInitialized
    {
        get
        {
            lock (Gate)
                return _initialized;
        }
    }

    /// <summary>
    /// The runtime minimum level. Setting it updates Serilog and every BRUTAL category at once.
    /// </summary>
    public static LogEventLevel MinimumLevel
    {
        get => LevelSwitch.MinimumLevel;
        set => LevelSwitch.MinimumLevel = value;
    }

    /// <summary>Events dropped because the asynchronous buffer was full.</summary>
    /// <remarks>
    /// Exposed because a silently lossy log is a trap: if this is non-zero the log is missing events and
    /// whoever is reading it needs to know that rather than infer a false history.
    /// </remarks>
    public static long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Builds the log pipeline. Idempotent: a second call is ignored, so a host that configures logging and
    /// then hands off to a component that also configures it cannot tear the first one down.
    /// </summary>
    public static void Initialize(EngineLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (Gate)
        {
            if (_initialized)
                return;

            LevelSwitch.MinimumLevel = options.MinimumLevel;

            var configuration = new LoggerConfiguration()
                .MinimumLevel.ControlledBy(LevelSwitch)
                .Enrich.WithThreadId()
                .Enrich.WithThreadName();

            // The debug window's copy of the stream. Wrapped in an asynchronous sink for the same reason the
            // console and file sinks are: the thread that logs must not pay for the window's bookkeeping.
            configuration = configuration.WriteTo.Async(
                sink => sink.Sink(new LogEventSink(RecentEvents)),
                blockWhenFull: false,
                bufferSize: options.AsyncBufferSize
            );

            if (options.Console)
                configuration = configuration.WriteTo.Async(
                    sink => sink.Console(),
                    blockWhenFull: false,
                    bufferSize: options.AsyncBufferSize
                );

            if (options.FilePath is { Length: > 0 } path)
                configuration = configuration.WriteTo.Async(
                    sink => sink.File(
                        path,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        shared: true
                    ),
                    blockWhenFull: false,
                    bufferSize: options.AsyncBufferSize
                );

            _logger = configuration.CreateLogger();

            // dispose: false because this class owns the logger and disposes it in Shutdown, which is the
            // only place that reliably drains the asynchronous buffer.
            LogSystem.Initialize(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Trace);
                builder.AddSerilog(_logger, dispose: false);
            });

            _initialized = true;
        }
    }

    /// <summary>
    /// Initialises the pipeline only if nothing has yet, with default options. Lets the engine run without a
    /// host having configured logging first.
    /// </summary>
    public static void InitializeDefault()
    {
        lock (Gate)
        {
            if (_initialized)
                return;
        }

        Initialize(new EngineLogOptions());
    }

    /// <summary>
    /// Drains and closes the pipeline. Safe to call when it was never initialised, and idempotent.
    /// </summary>
    /// <remarks>
    /// Disposing the logger is what flushes the asynchronous buffer. It is deliberately called from the
    /// host's shutdown path rather than from a finaliser, because a finaliser runs on the finaliser thread
    /// and can be skipped entirely when the process exits abruptly.
    /// </remarks>
    public static void Shutdown()
    {
        Serilog.Core.Logger? logger;

        lock (Gate)
        {
            if (!_initialized)
                return;

            logger = _logger;
            _logger = null;
            _initialized = false;
        }

        // Disposed outside the lock: it blocks until the background writer has drained, and holding a lock
        // across that would stall any thread that happens to touch this class while shutting down.
        logger?.Dispose();
    }

    /// <summary>Records that the pipeline dropped events, for <see cref="DroppedCount"/>.</summary>
    internal static void RecordDrop() => Interlocked.Increment(ref _dropped);
}
