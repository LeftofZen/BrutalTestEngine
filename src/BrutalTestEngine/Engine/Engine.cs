using System.Diagnostics;
using System.Runtime.CompilerServices;
using Brutal.GlfwApi;
using Brutal.Numerics;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Database;
using BrutalTestEngine.Host;
using BrutalTestEngine.Input;
using BrutalTestEngine.Io;
using BrutalTestEngine.Networking;
using BrutalTestEngine.Simulation;

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
    private readonly ConsoleCommandSource _consoleSource;
    private readonly RconServer _rconServer;
    private readonly WebCommandSource? _webSource;
    private readonly DatabaseCommandSource? _databaseSource;
    private readonly CommandSelfTest? _selfTest;
    private readonly StrongBox<long> _inputCommandsObserved;
    private readonly Command[] _logBuffer = new Command[CommandLogCapacity];
        private readonly ThreadTimerRegistry _timers;
        private readonly ThreadMetrics _mainFrameMetrics;
        private readonly ThreadMetrics _mainDrainMetrics;
        private readonly ThreadMetrics _mainWaitMetrics;

    private double _ticksPerSecond;
    private long _engineCommandsApplied;
    private double _lastEngineDrainMilliseconds;
    private double _maxEngineDrainMilliseconds;
    private bool _windowModeChanged;
    private string? _lastNotice;
    private bool _disposed;

    private const int CommandLogCapacity = 256;

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
        ConsoleCommandSource consoleSource,
        RconServer rconServer,
        WebCommandSource? webSource,
        DatabaseCommandSource? databaseSource,
        CommandSelfTest? selfTest,
                StrongBox<long> inputCommandsObserved,
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
        _consoleSource = consoleSource;
        _rconServer = rconServer;
        _webSource = webSource;
        _databaseSource = databaseSource;
        _selfTest = selfTest;
        _inputCommandsObserved = inputCommandsObserved;
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
        ConsoleCommandSource? consoleSource = null;
        RconServer? rconServer = null;
        WebCommandSource? webSource = null;
        DatabaseCommandSource? databaseSource = null;
        CommandSelfTest? selfTest = null;
        var bindings = KeyBindings.CreateDefault();

        // The key callback runs on the main thread before the Engine exists, so the count it maintains lives
        // in a box the Engine later reads for the console panel.
        var inputCommandsObserved = new StrongBox<long>(0);

        try
        {
            renderer = VulkanRenderer.Create(
                window.Handle,
                new int2(options.Width, options.Height),
                options.EnableValidation
            );

            // The ImGui context and its GLFW backend must be created on the main thread, before the render
            // thread starts, because the render thread's backend reads that context.
            debugUi = new DebugUi(window, renderer, options.Title);

            inputThread = new InputThread(commandBus, timers);
            ioThread = new IoThread(timers);
            networkThread = new NetworkThread(commandBus, timers);

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
        }
        catch
        {
            selfTest?.Dispose();
            databaseSource?.Dispose();
            webSource?.Dispose();
            rconServer?.Dispose();
            consoleSource?.Dispose();
            simulationThread?.Dispose();
            renderThread?.Dispose();
            networkThread?.Dispose();
            ioThread?.Dispose();
            inputThread?.Dispose();
            debugUi?.Dispose();
            renderer?.Dispose();
            window.Dispose();
            commandBus.Dispose();
            throw;
        }

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
            consoleSource,
            rconServer,
            webSource,
            databaseSource,
            selfTest,
                        inputCommandsObserved,
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
        /// Per-thread timing for the session, so a host can log or chart it without reaching into the threads.
        /// </summary>
        public ThreadTimerRegistry Timers => _timers;

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
                Console.Error.WriteLine(
                    $"engine command drain took {drainMilliseconds:0.0} ms for {applied} command(s)"
                );

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

        _commandBus.Complete();

        _simulationThread.Dispose();
        _inputThread.Dispose();
        _networkThread.Dispose();
        _ioThread.Dispose();
        _renderThread.Dispose();

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

    // Simulation commands belong to the simulation thread.

    void ICommandHandler.Handle(StepSimulationCommand command) => Ignore();

    void ICommandHandler.Handle(SetPausedCommand command) => Ignore();

    void ICommandHandler.Handle(ResetSimulationCommand command) => Ignore();

    void ICommandHandler.Handle(UnhandledCommand command) => _lastNotice = command.Describe();

    private static void Ignore()
    {
        // Nothing to do: the command is not addressed to this thread.
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
    /// Posts a line typed into the console. The console itself never reaches into the engine: a submitted
    /// line becomes a command on the bus, which is the same path every other source takes.
    /// </summary>
    private void SubmitConsoleLine(string line) =>
        _commandBus.Post(CommandParser.Parse(line, CommandSource.Remote));

    private ConsoleState BuildConsoleState()
    {
        var count = _commandBus.Log.Snapshot(_logBuffer);

        return new ConsoleState
        {
            Commands = _logBuffer,
            CommandCount = count,
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
            InputCommandsObserved = _inputCommandsObserved.Value,
            LastEngineDrainMilliseconds = _lastEngineDrainMilliseconds,
            MaxEngineDrainMilliseconds = _maxEngineDrainMilliseconds,
                        ThreadTimings = _timers.Snapshot(),
        };
    }
}
