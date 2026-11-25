using System.Diagnostics;
using System.Runtime.CompilerServices;
using Brutal.GlfwApi;
using Brutal.Numerics;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Database;
using BrutalTestEngine.Host;
using BrutalTestEngine.Input;
using BrutalTestEngine.Io;
using BrutalTestEngine.Jobs;
using BrutalTestEngine.Logging;
using BrutalTestEngine.Networking;
using BrutalTestEngine.Simulation;
using BrutalTestEngine.World;
using MSLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace BrutalTestEngine.Engine;

/// <summary>
/// Owns the engine's threads and runs the main (OS) thread loop.
/// </summary>
/// <remarks>
/// <para>
/// The engine is multithreaded by construction. Each concern runs on its own thread with an explicit
/// ownership boundary, and the threads communicate through lock-free handoffs rather than shared mutable
/// state:
/// </para>
/// <list type="bullet">
/// <item><b>Main / OS thread</b> — GLFW event dispatch, window mode changes, the ImGui context and frame
/// build, and the engine-targeted command drain. GLFW and ImGui are both main-thread-only.</item>
/// <item><b>Simulation thread</b> — fixed-timestep tick, fully decoupled from the frame rate.</item>
/// <item><b>Render thread</b> — owns Vulkan and the ImGui Vulkan backend; records, submits and presents.</item>
/// <item><b>Input thread</b> — folds input commands into the input snapshot the simulation reads.</item>
/// <item><b>IO thread</b> — file and database work, so nothing else ever blocks on a disk.</item>
/// <item><b>Network thread</b> — socket work, so nothing else ever blocks on the network.</item>
/// <item><b>Host sources</b> — the host console, the remote console listener and the web listener, each on
/// its own thread.</item>
/// </list>
/// <para>
/// Once the loop is running, every input and every decision travels as a command on the
/// <see cref="CommandBus"/>. This class is the engine-target consumer: the main thread drains commands that
/// change engine state (window mode, tick rate, quitting) and applies them between frames, and it is the only
/// place those changes happen. Nothing else mutates engine state directly.
/// </para>
/// <para>
/// The main thread never blocks on the simulation, and the simulation never blocks on rendering: the only
/// synchronisation between them is the triple buffer, which is wait-free on both sides.
/// </para>
/// </remarks>
public sealed class Engine : IDisposable, ICommandHandler
{
    // While minimised there is nothing to draw, so the loop idles instead of spinning a core.
    private const int MinimisedPollDelayMs = 16;

    // The main thread waits for the render thread to consume each frame's draw data, because ImGui is
    // single-context. The timeout only guards against a wedged render thread.
    private const int FrameConsumeTimeoutMs = 2000;

    /// <summary>
    /// A drain slower than this is reported to stderr. One frame's worth of drain is normally microseconds,
    /// so anything approaching a frame budget means a handler is doing real work and the frame is late.
    /// </summary>
    private const double SlowDrainWarningMilliseconds = 8.0;

    /// <summary>
    /// How many engine commands one frame may apply. A source that floods the bus must not be able to stretch
    /// a frame without bound; whatever is left over is applied on the next frame, still in order.
    /// </summary>
    private const int MaxCommandsPerFrame = 256;

    /// <summary>
    /// How long the console will wait for a command to be applied before reporting that it is still queued.
    /// The engine applies commands once per frame, so this is a budget for how long the console thread may
    /// block the caller's read loop — not a deadline the engine is expected to miss.
    /// </summary>
    private const int CommandConfirmationTimeoutMilliseconds = 250;

    private readonly EngineOptions _options;
    private readonly Window _window;
    private readonly VulkanRenderer _renderer;
    private readonly DebugUi _debugUi;
    private readonly CommandBus _commandBus;
    private readonly RenderThread _renderThread;
    private readonly SimulationThread _simulationThread;
    private readonly InputThread _inputThread;
    private readonly IoThread _ioThread;
    private readonly NetworkThread _networkThread;
    private readonly PhysicsThread _physicsThread;
    private readonly AudioThread _audioThread;
    private readonly ConsoleCommandSource _consoleSource;
    private readonly RconServer _rconServer;
    private readonly WebCommandSource? _webSource;
    private readonly DatabaseCommandSource? _databaseSource;
    private readonly McpServerThread? _mcpServer;
    private readonly WorldJobSystem? _world;
    private readonly WorldStreamer? _streamer;
    private readonly SdfClipmap? _clipmap;
    private readonly TerrainField? _terrain;
    private readonly CvarRegistry _cvars;
    private readonly CommandSelfTest? _selfTest;
    private readonly StrongBox<long> _inputCommandsObserved;
    private readonly Command[] _logBuffer = new Command[CommandLogCapacity];
    private readonly LogEntry[] _logEventBuffer = new LogEntry[LogEventCapacity];
    private readonly ThreadTimerRegistry _timers;
    private readonly ThreadMetrics _mainFrameMetrics;
    private readonly ThreadMetrics _mainDrainMetrics;
    private readonly ThreadMetrics _mainWaitMetrics;

    // Cached so the frame loop's log calls cost a null check when the level filters them out.
    private readonly Brutal.Logging.LogCategory _log = EngineLog.Categories.Main;

    private double _ticksPerSecond;
    private long _engineCommandsApplied;
    private double _lastEngineDrainMilliseconds;
    private double _maxEngineDrainMilliseconds;
    private bool _windowModeChanged;
    private string? _lastNotice;
    private bool _disposed;

    // The most recent failure reported by a worker thread's Faulted event. Written on the faulting thread
    // and read on the main thread, so each is a single reference swapped atomically rather than a string
    // mutated in place. A worker that faults stops its loop, so the last value is the one that matters.
    // Boxed because the subscriptions are wired in the static factory, before this instance exists.
    private readonly StrongBox<string?> _physicsFault;
    private readonly StrongBox<string?> _networkFault;

    private const int CommandLogCapacity = 256;

    /// <summary>
    /// How many log events the debug window's ring buffer keeps. Larger than the command log because a
    /// startup burst is the most useful thing the window shows, and it is the case most likely to overflow.
    /// </summary>
    private const int LogEventCapacity = LogEventBuffer.DefaultCapacity;

    private Engine(
        EngineOptions options,
        Window window,
        VulkanRenderer renderer,
        DebugUi debugUi,
        CommandBus commandBus,
        RenderThread renderThread,
        SimulationThread simulationThread,
        InputThread inputThread,
        IoThread ioThread,
        NetworkThread networkThread,
        PhysicsThread physicsThread,
        AudioThread audioThread,
        ConsoleCommandSource consoleSource,
        RconServer rconServer,
        WebCommandSource? webSource,
        DatabaseCommandSource? databaseSource,
        McpServerThread? mcpServer,
        WorldJobSystem? world,
        WorldStreamer? streamer,
        SdfClipmap? clipmap,
        TerrainField? terrain,
        CvarRegistry cvars,
        CommandSelfTest? selfTest,
        StrongBox<long> inputCommandsObserved,
        StrongBox<string?> physicsFault,
        StrongBox<string?> networkFault,
        ThreadTimerRegistry timers
    )
    {
        _options = options;
        _window = window;
        _renderer = renderer;
        _debugUi = debugUi;
        _commandBus = commandBus;
        _renderThread = renderThread;
        _simulationThread = simulationThread;
        _inputThread = inputThread;
        _ioThread = ioThread;
        _networkThread = networkThread;
        _physicsThread = physicsThread;
        _audioThread = audioThread;
        _consoleSource = consoleSource;
        _rconServer = rconServer;
        _webSource = webSource;
        _databaseSource = databaseSource;
        _mcpServer = mcpServer;
        _world = world;
        _streamer = streamer;
        _clipmap = clipmap;
        _terrain = terrain;
        _cvars = cvars;
        _selfTest = selfTest;
        _inputCommandsObserved = inputCommandsObserved;
        _physicsFault = physicsFault;
        _networkFault = networkFault;
        _ticksPerSecond = options.TicksPerSecond;

        _timers = timers;
        _mainFrameMetrics = timers.Get(ThreadTimerRegistry.Keys.MainFrame);
        _mainDrainMetrics = timers.Get(ThreadTimerRegistry.Keys.MainDrain);
        _mainWaitMetrics = timers.Get(ThreadTimerRegistry.Keys.MainWaitForRender);
    }

    public static Engine Create(EngineOptions options)
    {
        var window = Window.Create(options);

        // The bus is created before the GLFW callbacks are wired, because the callbacks post to it.
        var commandBus = new CommandBus();

        // One registry shared by every thread, so the debug panel can show their costs side by side.
        var timers = new ThreadTimerRegistry();
        VulkanRenderer? renderer = null;
        DebugUi? debugUi = null;
        RenderThread? renderThread = null;
        SimulationThread? simulationThread = null;
        InputThread? inputThread = null;
        IoThread? ioThread = null;
        NetworkThread? networkThread = null;
        PhysicsThread? physicsThread = null;
        AudioThread? audioThread = null;
        ConsoleCommandSource? consoleSource = null;
        RconServer? rconServer = null;
        WebCommandSource? webSource = null;
        DatabaseCommandSource? databaseSource = null;
        McpServerThread? mcpServer = null;
        WorldJobSystem? world = null;
        WorldStreamer? streamer = null;
        SdfClipmap? clipmap = null;
        TerrainField? terrain = null;
        CommandSelfTest? selfTest = null;
        var bindings = KeyBindings.CreateDefault();

        // The variable registry is created before anything that registers into it, and is handed to the MCP
        // server so a client reads the same values the debug panel shows rather than a parallel copy.
        var cvars = new CvarRegistry();

        // The key callback runs on the main thread before the Engine exists, so the count it maintains lives
        // in a box the Engine later reads for the console panel.
        var inputCommandsObserved = new StrongBox<long>(0);

        // The fault subscriptions are wired before the Engine exists, so the messages they record live in
        // boxes the Engine later reads. A worker that faults stops its loop, so one slot per thread is enough.
        var physicsFault = new StrongBox<string?>(null);
        var networkFault = new StrongBox<string?>(null);

        try
        {
            renderer = VulkanRenderer.Create(
                window.Handle,
                new int2(options.Width, options.Height),
                options.EnableValidation,
                options.EnableTracing,
                options.EnableRenderDoc,
                options.CaptureDirectory
            );

            // The ImGui context and its GLFW backend must be created on the main thread, before the render
            // thread starts, because the render thread's backend reads that context.
            debugUi = new DebugUi(window, renderer, options.Title);

            inputThread = new InputThread(commandBus, timers);
            ioThread = new IoThread(timers);

            // The engine only ever sees INetworkTransport; which library carries the bytes is the host's
            // choice and is decided here, at the single composition root.
            networkThread = new NetworkThread(
                commandBus,
                timers,
                (options.TransportFactory ?? NetworkTransports.RakNet)(),
                options.NetworkPort
            );

            // Physics and audio are separate services on separate threads: each has its own clock, and neither
            // may make a frame or a tick wait on it. The backends are chosen here, at the one composition root.
            physicsThread = new PhysicsThread(
                commandBus,
                (options.PhysicsFactory ?? PhysicsWorlds.None)(),
                timers
            );
            audioThread = new AudioThread(commandBus, (options.AudioFactory ?? AudioSystems.Fmod)(), timers);

            // A worker that faults stops its own loop, so without a subscription the failure is silent: the
            // thread simply stops stepping and the engine keeps running with a dead subsystem. These handlers
            // run on the faulting thread, so they only log and record a message; the main thread reads the
            // message and surfaces it in the debug panel and the status summary.
            physicsThread.Faulted += exception =>
            {
                physicsFault.Value = exception.Message;
                if (EngineLog.Categories.Engine.IsEnabled(MSLogLevel.Error))
                    EngineLog.Categories.Engine.Error(exception, $"physics thread faulted: {exception.Message}");
            };
            networkThread.Faulted += exception =>
            {
                networkFault.Value = exception.Message;
                if (EngineLog.Categories.Engine.IsEnabled(MSLogLevel.Error))
                    EngineLog.Categories.Engine.Error(exception, $"network thread faulted: {exception.Message}");
            };

            // GLFW callbacks run on the main thread and must return immediately, so each one only translates
            // the callback into a command and posts it. From here on they are indistinguishable from the
            // same intent arriving over a socket.
            window.Handle.OnKey += (_, key, _, action, _) =>
            {
                var command = InputCommands.Key(key, action);
                commandBus.Post(command);
                inputCommandsObserved.Value++;

                // A binding turns the key into a second, independent intent. It is posted rather than invoked
                // so that a rebound key cannot reach engine state that a remote sender could not reach.
                if (bindings.Resolve(command) is { } intent)
                    commandBus.Post(intent);
            };
            window.Handle.OnMouseButton += (_, button, action, _) =>
                commandBus.Post(InputCommands.MouseButton(button, action));
            window.Handle.OnCursorPos += (_, position) => commandBus.Post(InputCommands.CursorMove(position));
            window.Handle.OnScroll += (_, offset) => commandBus.Post(InputCommands.Scroll(offset));
            window.Handle.OnChar += (_, codePoint) => commandBus.Post(InputCommands.Text(codePoint));
            window.Handle.OnFocus += (_, focusEvent) => commandBus.Post(InputCommands.Focus(focusEvent));

            // The world is created before the render thread starts, because the renderer is handed the job
            // system's pools once and reads them from then on. The clipmap is centred on the origin rather
            // than on the camera, because the camera does not exist until the first frame; the first
            // streamer pass recentres it, which is a no-op for a camera already inside the innermost level.
            if (options.EnableWorld)
            {
                var worldSettings = options.World ?? WorldSettings.Default;
                var worldOptions = options.WorldJobs ?? WorldJobSystemOptions.Default;

                terrain = new TerrainField(worldSettings.Seed);

                // The pool's id budget is the job system's resident-brick budget, because that is what sizes
                // the job system's per-brick arrays. Letting the pool hand out more ids than that would index
                // past them.
                clipmap = new SdfClipmap(worldSettings, default, worldOptions.MaxResidentBricks);
                world = new WorldJobSystem(worldSettings, worldOptions, timers);
                streamer = new WorldStreamer(world, timers);

                world.Start();
                renderer.AttachWorld(world);
            }

            renderThread = new RenderThread(renderer, timers);

            simulationThread = new SimulationThread(
                commandBus,
                options.TicksPerSecond,
                () =>
                {
                    inputThread.TryReadSnapshot(out var snapshot, out _);
                    return snapshot;
                },
                () => renderThread.FramesRendered,
                () => inputThread.EventCount,
                () => ioThread.CompletedCount,
                () => networkThread.ReceivedCount,
                () => commandBus.PendingCount,
                timers
            );

            consoleSource = new ConsoleCommandSource(commandBus);
            rconServer = new RconServer(commandBus);

            if (options.WebPort is { } webPort)
                webSource = new WebCommandSource(commandBus, webPort);

            if (options.DatabasePath is { Length: > 0 } databasePath)
            {
                databaseSource = new DatabaseCommandSource(commandBus, ioThread, timers, databasePath);
                databaseSource.Initialize();
            }

            if (options.RunCommandSelfTest)
                selfTest = new CommandSelfTest(commandBus, rconServer, networkThread, webSource, databaseSource);

            // The MCP server is a source like any other: it parses text and posts commands. It is started last
            // so every subsystem it can report on already exists, and it is opt-in so a normal run never opens
            // a listener.
            if (options.McpPort is { } mcpPort)
            {
                RegisterCvars(
                    cvars,
                    renderer,
                    window,
                    commandBus,
                    renderThread,
                    simulationThread,
                    inputThread,
                    ioThread,
                    networkThread,
                    physicsThread,
                    audioThread,
                    rconServer,
                    webSource,
                    databaseSource,
                    world,
                    streamer,
                    clipmap,
                    timers
                );

                mcpServer = new McpServerThread(
                    cvars,
                    commandBus,
                    () => BuildStatus(
                        window,
                        commandBus,
                        renderThread,
                        simulationThread,
                        networkThread,
                        physicsThread,
                        audioThread,
                        renderer,
                        timers,
                        physicsFault,
                        networkFault
                    ),
                    mcpPort,
                                        timers,
                                        capture: renderer is null
                                            ? null
                                            : (timeout, cancellationToken) => renderer.CaptureFrameAsync(timeout, cancellationToken)
                );
            }
        }
        catch
        {
            selfTest?.Dispose();
            mcpServer?.Dispose();
            world?.Dispose();
            databaseSource?.Dispose();
            webSource?.Dispose();
            rconServer?.Dispose();
            consoleSource?.Dispose();
            simulationThread?.Dispose();
            renderThread?.Dispose();
            networkThread?.Dispose();
            ioThread?.Dispose();
            inputThread?.Dispose();
            physicsThread?.Dispose();
            audioThread?.Dispose();
            debugUi?.Dispose();
            renderer?.Dispose();
            window.Dispose();
            commandBus.Dispose();
            throw;
        }

        // The catch above rethrows, so creation succeeded and these exist. ThrowIfNull tells the compiler that
        // rather than asserting it with a null-forgiving operator, and it costs one branch at startup.
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(rconServer);

        Console.WriteLine($"Vulkan device: {renderer.DeviceName}");
        Console.WriteLine($"Swapchain: {renderer.Extent.Width}x{renderer.Extent.Height}, {renderer.ImageCount} images");
        Console.WriteLine($"Window mode: {window.Mode}");
        Console.WriteLine($"Simulation: {options.TicksPerSecond:0.#} Hz fixed timestep");
        Console.WriteLine("Threads: main, simulation, render, input, IO, network, console, rcon");
        Console.Write("Commands: console on stdin, rcon on 127.0.0.1:");
        Console.WriteLine(rconServer.Port);

        if (webSource is not null)
            Console.WriteLine($"Commands: web on http://127.0.0.1:{webSource.Port}/?cmd=...");
        if (databaseSource is not null)
            Console.WriteLine($"Commands: database table '{databaseSource.Table}' in {databaseSource.DatabasePath}");
        if (mcpServer is not null)
        {
            Console.WriteLine(
                $"MCP: http://127.0.0.1:{mcpServer.Port}/ exposing {mcpServer.CvarCount} variable(s) "
                    + "and the command grammar"
            );
        }

        // The banner goes to stdout unconditionally: it is the first thing a developer reads and it must not be
        // filtered by a log level. Everything after startup goes through the async log pipeline, so a thread in
        // a hot loop is never blocked writing diagnostics.
        var engineLog = EngineLog.Categories.Engine;
        if (engineLog.IsEnabled(MSLogLevel.Information))
        {
            engineLog.Info(
                $"engine started: device '{renderer.DeviceName}', swapchain "
                    + $"{renderer.Extent.Width}x{renderer.Extent.Height} ({renderer.ImageCount} images), "
                    + $"window mode {window.Mode}"
            );
            engineLog.Info(
                $"threads: main, sim, render, input, io, net ({networkThread.Transport.Name}), console, rcon"
            );
        }

        Console.WriteLine(CommandParser.HelpText);

        return new Engine(
            options,
            window,
            renderer,
            debugUi,
            commandBus,
            renderThread,
            simulationThread,
            inputThread,
            ioThread,
            networkThread,
            physicsThread,
            audioThread,
            consoleSource,
            rconServer,
            webSource,
            databaseSource,
            mcpServer,
            world,
            streamer,
            clipmap,
            terrain,
            cvars,
            selfTest,
            inputCommandsObserved,
            physicsFault,
            networkFault,
            timers
        );
    }

    /// <summary>Number of frames the render thread presented.</summary>
    public long FrameCount => _renderThread.FramesRendered;

    /// <summary>Number of simulation ticks executed.</summary>
    public long TickCount => _simulationThread.TickCount;

    /// <summary>Total number of commands accepted from every source.</summary>
    public long CommandsAccepted => _commandBus.AcceptedCount;

    /// <summary>Number of engine commands the main thread applied.</summary>
    public long EngineCommandsApplied => Interlocked.Read(ref _engineCommandsApplied);

    /// <summary>Result of the command self-test, or <c>null</c> when it was not requested.</summary>
    public CommandSelfTest? SelfTest => _selfTest;

    /// <summary>
    /// The most recent physics thread failure, or <c>null</c> when it has not faulted.
    /// </summary>
    /// <remarks>
    /// A faulted worker stops its own loop, so this is the only way a host learns that physics or networking
    /// has stopped doing its job. The debug panel and the MCP status summary read the same value.
    /// </remarks>
    public string? PhysicsFault => _physicsFault.Value;

    /// <summary>The most recent network thread failure, or <c>null</c> when it has not faulted.</summary>
    public string? NetworkFault => _networkFault.Value;

    /// <summary>
    /// Per-thread timing for the session, so a host can log or chart it without reaching into the threads.
    /// </summary>
    public ThreadTimerRegistry Timers => _timers;

    /// <summary>
    /// The RenderDoc API the renderer loaded, for a host that wants to query or drive captures.
    /// </summary>
    public RenderDocCapture Capture => _renderer.Capture;

    /// <summary>
    /// The engine's readable variables, for a host that wants to inspect or expose them.
    /// </summary>
    public CvarRegistry Cvars => _cvars;

    /// <summary>
    /// The MCP server, or <c>null</c> when it was not enabled.
    /// </summary>
    public McpServerThread? Mcp => _mcpServer;

    /// <summary>
    /// Runs the main thread loop until the window closes or the configured frame limit is reached, and
    /// returns the number of frames that were presented.
    /// </summary>
    public long Run()
    {
        while (!_window.ShouldClose && (_options.FrameLimit is not { } limit || FrameCount < limit))
        {
            var frameStartedAt = Stopwatch.GetTimestamp();

            _window.PollEvents();

            // Apply engine commands before anything is drawn, so a mode change or a tick-rate change takes
            // effect on this frame rather than the next one.
            var drainStartedAt = Stopwatch.GetTimestamp();
            var applied = _commandBus.Drain(CommandTarget.Engine, this, MaxCommandsPerFrame);
            var drainMilliseconds = Stopwatch.GetElapsedTime(drainStartedAt).TotalMilliseconds;
            _mainDrainMetrics.Record(drainMilliseconds);

            // Read the mode-change flag after the drain, because that is when the toggle is applied. It only
            // needs to cover this frame's drain, so it is cleared here.
            var swapchainRebuilt = _windowModeChanged;
            _windowModeChanged = false;

            _lastEngineDrainMilliseconds = drainMilliseconds;

            if (drainMilliseconds > _maxEngineDrainMilliseconds)
                _maxEngineDrainMilliseconds = drainMilliseconds;

            Interlocked.Add(ref _engineCommandsApplied, applied);

            // The engine owns the biggest commands: a window-mode change rebuilds the swapchain, so a slow
            // drain here is expected on those frames. Warn only when nothing structural happened, which is
            // the case where a handler is genuinely stalling the frame.
            if (drainMilliseconds >= SlowDrainWarningMilliseconds && !swapchainRebuilt)
            {
                if (_log.IsEnabled(MSLogLevel.Warning))
                    _log.Warning(
                        $"engine command drain took {drainMilliseconds:0.0} ms for {applied} command(s)"
                    );
            }

            _rconServer.Broadcast(_lastNotice ?? string.Empty);
            _lastNotice = null;

            var framebufferSize = _window.FramebufferSize;

            // Never block waiting for a real frame size; a minimised window would deadlock the app.
            // Requesting a zero size only flags the swapchain as out of date, so the existing one is kept and
            // rebuilt with a real extent once the window is restored.
            if (_window.IsMinimised)
            {
                _renderThread.RequestResize(framebufferSize);
                Thread.Sleep(MinimisedPollDelayMs);
                _mainFrameMetrics.RecordSince(frameStartedAt);
                continue;
            }

            _renderThread.RequestResize(framebufferSize);

            UpdateWorld();

            var snapshot = ReadSimulationSnapshot();
            var input = ReadInputSnapshot();

            if (
                _debugUi.BeginFrame(
                    snapshot,
                    input,
                    _simulationThread.FixedDeltaSeconds,
                    BuildConsoleState(),
                    SubmitConsoleLine
                )
                is { } requestedMode
            )
                _commandBus.Post(
                    new SetWindowModeCommand { Source = CommandSource.System, Mode = requestedMode }
                );

            // Hand the frame to the render thread, then wait for it to finish with the draw data before
            // building the next one: ImGui is single-context, so the two threads must not overlap.
            _renderThread.SubmitFrame(_debugUi.DrawData, snapshot, framebufferSize);

            // Timed separately because this is the main thread blocked on another thread's work, which is
            // the single largest cost in the frame and the one worth distinguishing from its own work.
            var waitStartedAt = Stopwatch.GetTimestamp();
            _renderThread.WaitForFrameConsumed(FrameConsumeTimeoutMs);
            _mainWaitMetrics.RecordSince(waitStartedAt);

            _mainFrameMetrics.RecordSince(frameStartedAt);
        }

        return FrameCount;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Stop the sources first so nothing new is posted, then the consumers, then the resources they used.
        // The simulation is stopped before the renderer so it cannot publish into a torn-down engine.
        _selfTest?.Dispose();
        _consoleSource.Dispose();
        _rconServer.Dispose();
        _webSource?.Dispose();
        _databaseSource?.Dispose();
        _mcpServer?.Dispose();

        _commandBus.Complete();

        _simulationThread.Dispose();
        _inputThread.Dispose();
        _networkThread.Dispose();
        _ioThread.Dispose();

        // Physics and audio own native resources — a Jolt world and an FMOD device — so they are disposed
        // here rather than left to the finalizer. They are stopped after the bus is completed so no new
        // command can arrive while they are shutting down.
        _physicsThread.Dispose();
        _audioThread.Dispose();

        _renderThread.Dispose();

        // The world's workers are stopped after the render thread, because the renderer reads the job
        // system's pools while it draws and a worker still writing them would be a race.
        _world?.Dispose();

        // Reverse creation order: the ImGui context references the render pass and the window.
        _debugUi.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        _commandBus.Dispose();
    }

    // Engine-targeted commands are the only ones this thread owns. Everything else is admitted and logged by
    // the bus and applied by the thread it was routed to.

    void ICommandHandler.Handle(QuitCommand command)
    {
        _window.ShouldClose = true;
        _lastNotice = "quit accepted";
    }

    void ICommandHandler.Handle(HelpCommand command) => _lastNotice = CommandParser.HelpText;

    void ICommandHandler.Handle(SetWindowModeCommand command)
    {
        _window.SetMode(command.Mode);
        _windowModeChanged = true;
    }

    void ICommandHandler.Handle(ToggleWindowModeCommand command)
    {
        _window.SetMode(
            _window.Mode == WindowMode.Windowed ? WindowMode.BorderlessFullscreen : WindowMode.Windowed
        );
        _windowModeChanged = true;
    }

    void ICommandHandler.Handle(SetTickRateCommand command)
    {
        _ticksPerSecond = command.TicksPerSecond;
        _simulationThread.SetTicksPerSecond(command.TicksPerSecond);
        _lastNotice = $"tick-rate now {command.TicksPerSecond:0.##} Hz";
    }

    /// <summary>
    /// Re-runs the text of an <see cref="InjectInputCommand"/> through the shared parser, which is how a
    /// remote console line reaches whichever thread owns it.
    /// </summary>
    /// <remarks>
    /// The re-parsed commands keep their original source-of-truth routing but take the injecting source, so
    /// "who asked" survives the hop. This is the one place a command is deliberately turned back into text and
    /// parsed again, and it is what lets the console stay ignorant of the engine's command set.
    /// </remarks>
    void ICommandHandler.Handle(InjectInputCommand command)
    {
        var commands = CommandParser.ParseMany(command.Text, command.Source);

        foreach (var injected in commands)
        {
            // A line that asks for help must not recurse; anything else is re-posted to its own target.
            switch (injected)
            {
                case UnhandledCommand:
                case HelpCommand:
                case InjectInputCommand:
                    _lastNotice = injected.Describe();
                    break;
                default:
                    _commandBus.Post(injected);
                    break;
            }
        }
    }

    // Input commands belong to the input thread.

    void ICommandHandler.Handle(KeyCommand command) => Ignore();

    void ICommandHandler.Handle(MouseButtonCommand command) => Ignore();

    void ICommandHandler.Handle(CursorMoveCommand command) => Ignore();

    void ICommandHandler.Handle(ScrollCommand command) => Ignore();

    void ICommandHandler.Handle(TextInputCommand command) => Ignore();

    void ICommandHandler.Handle(FocusCommand command) => Ignore();

    // Simulation commands belong to the simulation thread, physics commands to the physics thread and audio
    // commands to the audio thread.

    void ICommandHandler.Handle(StepSimulationCommand command) => Ignore();

    void ICommandHandler.Handle(SetPausedCommand command) => Ignore();

    void ICommandHandler.Handle(ResetSimulationCommand command) => Ignore();

    void ICommandHandler.Handle(AddBodyCommand command) => Ignore();

    void ICommandHandler.Handle(SetGravityCommand command) => Ignore();

    void ICommandHandler.Handle(SetVolumeCommand command) => Ignore();

    void ICommandHandler.Handle(UnhandledCommand command) => _lastNotice = command.Describe();

    /// <summary>
    /// Asks the render thread to capture the next frame. Fails with a notice rather than a fault when
    /// RenderDoc is not attached, because "no capture tool" is a normal state, not an error.
    /// </summary>
    void ICommandHandler.Handle(CaptureFrameCommand command)
    {
        var triggered = _renderThread.RequestCapture();

        _lastNotice = triggered
        ? "capture triggered for the next frame"
        : "capture unavailable: RenderDoc is not attached";

        // Only the failure is logged here. A successful trigger is already logged by the capture wrapper, which
        // knows the RenderDoc version and the destination; logging it again produced two identical lines and
        // made a single `capture` look like it happened twice.
        if (!triggered && _log.IsEnabled(MSLogLevel.Debug))
            _log.Debug("capture requested but unavailable: RenderDoc is not attached");
    }

    void ICommandHandler.Handle(SetLogLevelCommand command)
    {
        if (!TryParseLogLevel(command.Level, out var level))
        {
            _lastNotice = $"unknown log level '{command.Level}'";

            if (_log.IsEnabled(MSLogLevel.Warning))
                _log.Warning(_lastNotice);

            return;
        }

        EngineLog.MinimumLevel = level;
        _lastNotice = $"log level now {level}";

        // Logged after the switch, so raising the level takes effect on this message itself.
        if (_log.IsEnabled(MSLogLevel.Information))
            _log.Info(_lastNotice);
    }

    private static bool TryParseLogLevel(string value, out Serilog.Events.LogEventLevel level)
    {
        switch (value.ToLowerInvariant())
        {
            case "verbose" or "trace":
                level = Serilog.Events.LogEventLevel.Verbose;
                return true;
            case "debug":
                level = Serilog.Events.LogEventLevel.Debug;
                return true;
            case "information" or "info":
                level = Serilog.Events.LogEventLevel.Information;
                return true;
            case "warning" or "warn":
                level = Serilog.Events.LogEventLevel.Warning;
                return true;
            case "error":
                level = Serilog.Events.LogEventLevel.Error;
                return true;
            case "fatal" or "critical":
                level = Serilog.Events.LogEventLevel.Fatal;
                return true;
            default:
                level = default;
                return false;
        }
    }

    private static void Ignore()
    {
        // Nothing to do: the command is not addressed to this thread.
    }

    /// <summary>
    /// Registers every readable engine variable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registration is a delegate per variable rather than a value, so a reader always sees the engine as it
    /// is at the moment it is asked. That is what makes the MCP surface live rather than a startup snapshot,
    /// and it is why the same accessors the debug panel uses are the ones registered here: there is one
    /// definition of each number, not two that can disagree.
    /// </para>
    /// <para>
    /// The per-thread timings are registered as a group, one variable per metric, because "which thread is
    /// costing me this frame" is the question a client is most likely to ask and it needs the numbers side by
    /// side rather than behind a single opaque blob.
    /// </para>
    /// </remarks>
    private static void RegisterCvars(
        CvarRegistry cvars,
        VulkanRenderer renderer,
        Window window,
        CommandBus bus,
        RenderThread renderThread,
        SimulationThread simulationThread,
        InputThread inputThread,
        IoThread ioThread,
        NetworkThread networkThread,
        PhysicsThread physicsThread,
        AudioThread audioThread,
        RconServer rconServer,
        WebCommandSource? webSource,
        DatabaseCommandSource? databaseSource,
        WorldJobSystem? world,
        WorldStreamer? streamer,
        SdfClipmap? clipmap,
        ThreadTimerRegistry timers
    )
    {
        cvars.RegisterValue("engine.frames", () => renderThread.FramesRendered, "long", "Frames the render thread has presented.");
        cvars.RegisterValue("engine.ticks", () => simulationThread.TickCount, "long", "Simulation ticks executed.");
        cvars.RegisterValue("engine.tickRate", () => simulationThread.FixedDeltaSeconds > 0 ? 1.0 / simulationThread.FixedDeltaSeconds : 0.0, "double", "The simulation's configured fixed tick rate in Hz.");
        cvars.RegisterValue("engine.droppedTicks", () => simulationThread.DroppedTicks, "long", "Ticks dropped because the simulation fell behind.");
        cvars.RegisterValue("engine.entities", () => simulationThread.EntityCount, "int", "Live entities in the simulation's data-oriented entity store.");
        cvars.RegisterValue("engine.paused", () => simulationThread.IsPaused, "bool", "True while the passage of simulated time is halted.");
        cvars.Register("engine.windowMode", () => window.Mode.ToString(), "string", "The current window mode.");
        cvars.Register("engine.framebuffer", () => $"{window.FramebufferSize.X}x{window.FramebufferSize.Y}", "string", "The window's framebuffer size in pixels.");
        cvars.Register("engine.swapchain", () => $"{renderer.Extent.Width}x{renderer.Extent.Height}", "string", "The swapchain extent in pixels.");
        cvars.RegisterValue("engine.swapchainImages", () => renderer.ImageCount, "int", "Number of images in the swapchain.");
        cvars.Register("engine.device", () => renderer.DeviceName, "string", "The Vulkan device the renderer is using.");

        cvars.RegisterValue("cmd.accepted", () => bus.AcceptedCount, "long", "Commands accepted from every source.");
        cvars.RegisterValue("cmd.pending", () => bus.PendingCount, "int", "Commands accepted but not yet applied.");
        cvars.RegisterValue("cmd.applied.engine", () => bus.AppliedCount(CommandTarget.Engine), "long", "Engine commands the main thread has applied.");
        cvars.RegisterValue("cmd.applied.input", () => bus.AppliedCount(CommandTarget.Input), "long", "Input commands the input thread has applied.");
        cvars.RegisterValue("cmd.applied.simulation", () => bus.AppliedCount(CommandTarget.Simulation), "long", "Simulation commands the simulation thread has applied.");
        cvars.RegisterValue("cmd.applied.physics", () => bus.AppliedCount(CommandTarget.Physics), "long", "Physics commands the physics thread has applied.");
        cvars.RegisterValue("cmd.applied.audio", () => bus.AppliedCount(CommandTarget.Audio), "long", "Audio commands the audio thread has applied.");
        cvars.RegisterValue("cmd.logOverwritten", () => bus.Log.OverwrittenCount, "long", "Commands discarded from the log because it was full.");

        cvars.RegisterValue("input.commands", () => inputThread.EventCount, "long", "Input commands the input thread has applied.");
        cvars.RegisterValue("io.jobs", () => ioThread.CompletedCount, "long", "IO jobs the IO thread has completed.");
        cvars.Register("net.transport", () => networkThread.Transport.Name, "string", "The name of the network transport in use.");
        cvars.Register("net.endpoint", () => networkThread.LocalEndPoint.ToString(), "string", "The endpoint the network transport is bound to.");
        cvars.RegisterValue("net.received", () => networkThread.ReceivedCount, "long", "Messages the network thread has received.");
        cvars.RegisterValue("net.commands", () => networkThread.CommandsPosted, "long", "Commands decoded from network datagrams.");
        cvars.RegisterValue("net.rejected", () => networkThread.RejectedCount, "long", "Datagrams that could not be decoded.");

        cvars.Register("phys.backend", () => physicsThread.World.Name, "string", "The name of the physics backend.");
        cvars.RegisterValue("phys.available", () => physicsThread.World.IsAvailable, "bool", "True when the physics backend initialized and can be stepped.");
        cvars.RegisterValue("phys.bodies", () => physicsThread.World.BodyCount, "int", "Bodies currently in the physics world.");
        cvars.RegisterValue("phys.steps", () => physicsThread.World.StepCount, "long", "Physics steps performed this session.");
        cvars.Register("audio.backend", () => audioThread.Audio.Name, "string", "The name of the audio backend.");
        cvars.RegisterValue("audio.available", () => audioThread.Audio.IsAvailable, "bool", "True when an audio device was opened.");
        cvars.RegisterValue("audio.channels", () => audioThread.Audio.PlayingChannels, "int", "Channels the mixer is currently playing.");
        cvars.RegisterValue("audio.updates", () => audioThread.Audio.UpdateCount, "long", "Mixer updates performed this session.");

        // The world's variables are registered from the job system's own counters rather than from a copy, so
        // a client reading them over MCP sees the same numbers the debug panel does, live.
        if (world is not null && streamer is not null && clipmap is not null)
        {
            var jobs = world;
            var streaming = streamer;
            var map = clipmap;

            cvars.RegisterValue("world.workers", () => jobs.WorkerCount, "int", "World-generation worker threads.");
            cvars.RegisterValue("world.epoch", () => jobs.Epoch, "long", "Generation counter; bumped whenever the clipmap recentres.");
            cvars.RegisterValue("world.submitted", () => jobs.SubmittedCount, "long", "Brick builds queued to the workers.");
            cvars.RegisterValue("world.completed", () => jobs.CompletedCount, "long", "Brick builds the workers finished.");
            cvars.RegisterValue("world.inFlight", () => jobs.InFlightCount, "long", "Brick builds queued but not yet finished.");
            cvars.RegisterValue("world.waiting", () => jobs.CompletionsWaiting, "int", "Finished bricks waiting to be committed.");
            cvars.RegisterValue("world.empty", () => jobs.EmptyCount, "long", "Bricks that produced no geometry because they held no surface.");
            cvars.RegisterValue("world.stale", () => jobs.StaleResultCount, "long", "Results discarded because a recentring invalidated them.");
            cvars.RegisterValue("world.noBuffer", () => jobs.NoBufferCount, "long", "Results dropped because no reusable buffer was free.");
            cvars.RegisterValue("world.overflow", () => jobs.OverflowCount, "long", "Commits that could not get a contiguous run of meshlet slots.");
            cvars.RegisterValue("world.resident", () => jobs.ResidentBrickCount, "long", "Bricks currently holding geometry.");
            cvars.RegisterValue("world.meshlets", () => jobs.MeshletsBuilt, "long", "Meshlets built this session.");
            cvars.RegisterValue("world.arenaCapacity", () => jobs.MeshletCapacity, "int", "Meshlet slots the arena can hand out.");
            cvars.RegisterValue("world.arenaLive", () => jobs.Arena.LiveCount, "int", "Meshlet slots currently allocated.");
            cvars.RegisterValue("world.arenaLargestFree", () => jobs.Arena.LargestFreeRun, "int", "Largest contiguous run of free meshlet slots.");
            cvars.RegisterValue("world.arenaFailures", () => jobs.Arena.FailureCount, "long", "Allocations the arena could not satisfy.");
            cvars.RegisterValue("world.poolAllocated", () => map.Pool.AllocatedCount, "int", "Brick pool slots in use.");
            cvars.RegisterValue("world.poolCapacity", () => map.Pool.Capacity, "int", "Brick pool slots available.");
            cvars.RegisterValue("world.levels", () => map.LevelCount, "int", "Clipmap levels.");
            cvars.RegisterValue("world.viewDistance", () => map.Settings.ViewDistance, "float", "Radius the coarsest clipmap level covers, in metres.");
            cvars.RegisterValue("world.passes", () => streaming.PassCount, "long", "Streamer passes run.");
            cvars.RegisterValue("world.requested", () => streaming.TotalRequested, "long", "Bricks requested across every pass.");
            cvars.RegisterValue("world.evicted", () => streaming.TotalEvicted, "long", "Resident bricks evicted across every pass.");
            cvars.RegisterValue("world.candidates", () => streaming.Statistics.Candidates, "int", "Slots the last pass considered.");
            cvars.RegisterValue("world.surface", () => streaming.Statistics.SurfaceBricks, "int", "Slots the last pass believes contain surface.");
            cvars.RegisterValue("world.skipped", () => streaming.Statistics.Skipped, "int", "Slots the last pass skipped as all air or all rock.");
            cvars.RegisterValue("world.budgetExhausted", () => streaming.Statistics.BudgetExhausted, "bool", "True when the last pass stopped early because the pool or queue was full.");
            cvars.RegisterValue("world.camera", () => $"{streaming.Camera.X:0.#},{streaming.Camera.Y:0.#},{streaming.Camera.Z:0.#}", "string", "The position the last streamer pass ran for.");
        }

        cvars.RegisterValue("rcon.port", () => rconServer.Port, "int", "Loopback port of the remote console listener.");
        cvars.RegisterValue("rcon.clients", () => rconServer.ClientCount, "int", "Clients currently connected to the remote console.");
        cvars.RegisterValue("rcon.lines", () => rconServer.LinesPosted, "long", "Lines received over the remote console.");
        cvars.RegisterValue("web.port", () => webSource?.Port ?? 0, "int", "Web listener port, or 0 when it is off.");
        cvars.RegisterValue("web.requests", () => webSource?.RequestCount ?? 0, "long", "HTTP requests the web listener has received.");
        cvars.RegisterValue("web.commands", () => webSource?.CommandsPosted ?? 0, "long", "Commands posted from web requests.");
        cvars.RegisterValue("db.commands", () => databaseSource?.CommandsPosted ?? 0, "long", "Commands posted from watched database rows.");
        cvars.RegisterValue("db.polls", () => databaseSource?.PollCount ?? 0, "long", "Completed database poll cycles.");
        cvars.RegisterValue("db.failures", () => databaseSource?.FailureCount ?? 0, "long", "Database polls that threw.");

        cvars.Register("log.level", () => EngineLog.MinimumLevel.ToString(), "string", "The log pipeline's current minimum level.");
        cvars.RegisterValue("log.dropped", () => EngineLog.DroppedCount, "long", "Log events dropped because the asynchronous buffer was full.");
        cvars.RegisterValue("renderdoc.available", () => renderer.Capture.IsAvailable, "bool", "True when RenderDoc's in-application API is attached.");
        cvars.Register("renderdoc.version", () => renderer.Capture.ApiVersionNumber?.ToString() ?? "none", "string", "The RenderDoc API version, or 'none'.");
        cvars.RegisterValue("renderdoc.captures", () => renderer.Capture.CaptureCount, "uint", "Captures RenderDoc has written this session.");

        // One variable per metric, so a client can read a single thread's cost without parsing a blob.
        // The MCP metrics are resolved first because the MCP thread is created after this registration runs,
        // so they would otherwise be missing from the snapshot the loop below walks.
        _ = timers.Get(ThreadTimerRegistry.Keys.McpPoll);
        _ = timers.Get(ThreadTimerRegistry.Keys.McpRequest);

        foreach (var (key, metrics) in timers.Snapshot())
        {
            var name = key;
            cvars.RegisterValue(
                $"timing.{name}.mean",
                () => metrics.MeanMilliseconds,
                "double",
                $"Mean duration of '{name}' over its rolling window, in milliseconds."
            );
            cvars.RegisterValue(
                $"timing.{name}.p99",
                () => metrics.PercentileMilliseconds(0.99),
                "double",
                $"Estimated p99 duration of '{name}', in milliseconds."
            );
            cvars.RegisterValue(
                $"timing.{name}.max",
                () => metrics.MaxMilliseconds,
                "double",
                $"Slowest '{name}' seen this session, in milliseconds."
            );
            cvars.RegisterValue(
                $"timing.{name}.samples",
                () => metrics.Samples,
                "long",
                $"Number of '{name}' samples recorded."
            );
        }

        cvars.RegisterValue(
            "log.retained",
            () => EngineLog.RecentEvents.Count,
            "int",
            "Log events currently held for the debug window."
        );

        cvars.RegisterValue(
            "log.overwritten",
            () => EngineLog.RecentEvents.OverwrittenCount,
            "long",
            "Log events overwritten in the debug window's ring buffer because it was full."
        );

        // Registered last so it sees every variable above, including the per-thread timings. It excludes
        // itself by name: its reader walks the registry, so without the exclusion reading 'help' would
        // re-enter its own reader and recurse until the stack ran out.
        cvars.Register(
            "help",
            () => cvars.DescribeAll("help"),
            "string",
            "Every other engine variable and its current value, one per line."
        );
    }

    /// <summary>
    /// Builds the one-call status summary the MCP server serves.
    /// </summary>
    /// <remarks>
    /// Called on the MCP thread, so it reads only through accessors that are safe from any thread and copies
    /// everything into a value. It never touches the window, ImGui or the renderer's mutable state.
    /// </remarks>
    private static EngineStatus BuildStatus(
        Window window,
        CommandBus bus,
        RenderThread renderThread,
        SimulationThread simulationThread,
        NetworkThread networkThread,
        PhysicsThread physicsThread,
        AudioThread audioThread,
        VulkanRenderer renderer,
        ThreadTimerRegistry timers,
        StrongBox<string?> physicsFault,
        StrongBox<string?> networkFault
    )
    {
        var timings = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, metrics) in timers.Snapshot())
        {
            timings[key] =
                $"mean {metrics.MeanMilliseconds:0.000} ms, p99 {metrics.PercentileMilliseconds(0.99):0.000} ms, "
                + $"max {metrics.MaxMilliseconds:0.000} ms, {metrics.Samples} sample(s)";
        }

        return new EngineStatus(
            renderThread.FramesRendered,
            simulationThread.TickCount,
            simulationThread.FixedDeltaSeconds > 0 ? 1.0 / simulationThread.FixedDeltaSeconds : 0.0,
            bus.AcceptedCount,
            bus.PendingCount,
            window.Mode.ToString(),
            networkThread.Transport.Name,
            networkThread.LocalEndPoint.ToString(),
            physicsThread.World.Name,
            physicsThread.World.IsAvailable,
            audioThread.Audio.Name,
            audioThread.Audio.IsAvailable,
            renderer.Capture.IsAvailable,
            EngineLog.MinimumLevel.ToString(),
            timings,
            physicsFault.Value,
            networkFault.Value
        );
    }

    private SimulationSnapshot ReadSimulationSnapshot()
    {
        _simulationThread.TryReadSnapshot(out var snapshot, out _);
        return snapshot;
    }

    private InputSnapshot ReadInputSnapshot()
    {
        _inputThread.TryReadSnapshot(out var snapshot, out _);
        return snapshot;
    }

    /// <summary>
    /// Advances world streaming for this frame and hands the result to the renderer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order matters. The streamer decides what the world needs and queues it, then the finished bricks
    /// are committed into the destination pools, and only then is the draw list published. Publishing before
    /// committing would hand the renderer a list that names geometry the pools do not hold yet.
    /// </para>
    /// <para>
    /// The commit budget is what keeps a burst of completions from becoming a hitch. When the workers warm
    /// up they finish in waves, and an unbounded commit would copy every wave's geometry in one frame. The
    /// cap spreads that over several frames, and because the streamer re-requests nothing that is already
    /// resident, the backlog drains without the world being rebuilt.
    /// </para>
    /// </remarks>
    private void UpdateWorld()
    {
        if (_world is not { } world || _streamer is not { } streamer || _clipmap is not { } clipmap)
            return;

        if (_terrain is not { } terrain)
            return;

        var camera = CameraPosition;

        // The clipmap is centred on the *terrain surface* under the camera's horizontal position, not on the
        // camera itself. The levels are nested cubes, so the finest level only owns what falls inside its
        // small cube; centring that cube at flying altitude puts the whole terrain band inside a coarse
        // level's cube instead, and the world then resolves at tens of metres per brick with gaps between
        // them. A heightfield's finest detail belongs where the ground is, which is directly below the
        // viewpoint.
        var streamCenter = new float3(camera.X, terrain.HeightAt(camera.X, camera.Z), camera.Z);

        streamer.Update(streamCenter, clipmap, terrain);
        streamer.CommitReady(MaxWorldCommitsPerFrame);

        _renderer.SetCamera(camera, CameraTarget, CameraFieldOfView);
        _renderer.PublishDraws(world.BuildDrawList(), world.CompletedCount);
    }

    /// <summary>
    /// How many finished bricks one frame may copy into the destination pools.
    /// </summary>
    /// <remarks>
    /// A commit is a memcpy of a few kilobytes, so this is a budget of roughly a megabyte of copying per
    /// frame — small enough not to show up in a frame time, large enough that a cold start fills in within a
    /// second or two.
    /// </remarks>
    private const int MaxWorldCommitsPerFrame = 64;

    /// <summary>
    /// Where the camera is, in world space.
    /// </summary>
    /// <remarks>
    /// A fixed vantage point for now, a little above the terrain's mean height and far enough back to see the
    /// clipmap's inner levels and the fog close the horizon. It is a property rather than a constant so the
    /// debug panel and the cvars can move it without a rebuild.
    /// </remarks>
    private float3 CameraPosition { get; set; } = new(0.0f, 160.0f, 300.0f);

    /// <summary>What the camera looks at.</summary>
    private float3 CameraTarget { get; set; } = new(0.0f, 20.0f, 0.0f);

    /// <summary>The camera's vertical field of view, in radians. Sixty degrees.</summary>
    private float CameraFieldOfView { get; set; } = 1.0472f;

    /// <summary>
    /// Posts a line typed into the console. The console itself never reaches into the engine: a submitted
    /// line becomes a command on the bus, which is the same path every other source takes.
    /// </summary>
    private void SubmitConsoleLine(string line) =>
        _commandBus.Post(CommandParser.Parse(line, CommandSource.Remote));

    private ConsoleState BuildConsoleState()
    {
        var count = _commandBus.Log.Snapshot(_logBuffer);
        var logEventCount = EngineLog.RecentEvents.Snapshot(_logEventBuffer);

        return new ConsoleState
        {
            Commands = _logBuffer,
            CommandCount = count,
            LogEvents = _logEventBuffer,
            LogEventCount = logEventCount,
            LogEventOverwrittenCount = EngineLog.RecentEvents.OverwrittenCount,
            AcceptedCount = _commandBus.AcceptedCount,
            PendingCount = _commandBus.PendingCount,
            EngineAppliedCount = EngineCommandsApplied,
            InputAppliedCount = _commandBus.AppliedCount(CommandTarget.Input),
            SimulationAppliedCount = _commandBus.AppliedCount(CommandTarget.Simulation),
            OverwrittenCount = _commandBus.Log.OverwrittenCount,
            TicksPerSecond = _ticksPerSecond,
            RconPort = _rconServer.Port,
            RconClients = _rconServer.ClientCount,
            RconLines = _rconServer.LinesPosted,
            ConsoleLines = _consoleSource.LinesPosted,
            NetworkCommands = _networkThread.CommandsPosted,
            NetworkRejected = _networkThread.RejectedCount,
            WebPort = _webSource?.Port,
            WebRequests = _webSource?.RequestCount ?? 0,
            WebCommands = _webSource?.CommandsPosted ?? 0,
            DatabaseCommands = _databaseSource?.CommandsPosted ?? 0,
            DatabasePolls = _databaseSource?.PollCount ?? 0,
            DatabaseFailures = _databaseSource?.FailureCount ?? 0,
            DatabaseError = _databaseSource?.LastError,
            McpPort = _mcpServer?.Port,
            McpCvarCount = _mcpServer?.CvarCount ?? 0,
            McpRequests = _mcpServer?.RequestCount ?? 0,
            McpCommands = _mcpServer?.CommandsPosted ?? 0,
            InputCommandsObserved = _inputCommandsObserved.Value,
            LastEngineDrainMilliseconds = _lastEngineDrainMilliseconds,
            MaxEngineDrainMilliseconds = _maxEngineDrainMilliseconds,
            PhysicsFault = _physicsFault.Value,
            PhysicsBackend = _physicsThread.World.Name,
            PhysicsAvailable = _physicsThread.World.IsAvailable,
            PhysicsBodies = _physicsThread.World.BodyCount,
            PhysicsSteps = _physicsThread.World.StepCount,
            AudioBackend = _audioThread.Audio.Name,
            AudioAvailable = _audioThread.Audio.IsAvailable,
            AudioChannels = _audioThread.Audio.PlayingChannels,
            AudioUpdates = _audioThread.Audio.UpdateCount,
            ThreadTimings = _timers.Snapshot(),
            NetworkTransport = _networkThread.Transport.Name,
            NetworkEndpoint = _networkThread.LocalEndPoint.ToString(),
            NetworkFault = _networkFault.Value,
            TracingEnabled = _options.EnableTracing,
            RenderDocAvailable = _renderer.Capture.IsAvailable,
            RenderDocVersion = _renderer.Capture.ApiVersionNumber?.ToString(),
            CaptureCount = _renderer.Capture.CaptureCount,
            LogLevel = EngineLog.MinimumLevel.ToString(),
            LogDroppedCount = EngineLog.DroppedCount,
            WorldEnabled = _world is not null,
            WorldWorkers = _world?.WorkerCount ?? 0,
            WorldResidentBricks = _world?.ResidentBrickCount ?? 0,
            WorldMeshlets = _world?.MeshletsBuilt ?? 0,
            WorldArenaLive = _world?.Arena.LiveCount ?? 0,
            WorldArenaCapacity = _world?.Arena.Capacity ?? 0,
            WorldArenaLargestFree = _world?.Arena.LargestFreeRun ?? 0,
            WorldInFlight = _world?.InFlightCount ?? 0,
            WorldWaiting = _world?.CompletionsWaiting ?? 0,
            WorldEmpty = _world?.EmptyCount ?? 0,
            WorldStale = _world?.StaleResultCount ?? 0,
            WorldOverflow = _world?.OverflowCount ?? 0,
            WorldCandidates = _streamer?.Statistics.Candidates ?? 0,
            WorldSurface = _streamer?.Statistics.SurfaceBricks ?? 0,
            WorldSkipped = _streamer?.Statistics.Skipped ?? 0,
            WorldBudgetExhausted = _streamer?.Statistics.BudgetExhausted ?? false,
            WorldViewDistance = _clipmap?.Settings.ViewDistance ?? 0.0f,
            WorldCamera = _streamer is { } s
                ? $"{s.Camera.X:0.#}, {s.Camera.Y:0.#}, {s.Camera.Z:0.#}"
                : "n/a",
        };
    }
}
