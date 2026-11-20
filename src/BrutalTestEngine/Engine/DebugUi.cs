using Brutal;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Input;
using BrutalTestEngine.Simulation;

namespace BrutalTestEngine.Engine;

/// <summary>
/// Owns the ImGui context and its GLFW platform backend, and builds the debug panel and command console.
/// Lives on the main thread.
/// </summary>
/// <remarks>
/// <para>
/// The ImGui context must live on the thread that owns GLFW event dispatch: the GLFW backend installs the
/// window callbacks and calls ImGui directly from them, and GLFW callbacks are main-thread-only. The Vulkan
/// half of ImGui is therefore split out into <see cref="ImGuiVulkanRenderer"/>, which the render thread owns.
/// </para>
/// <para>
/// The console panel is the human half of the command system. Submitting a line here does not call into the
/// engine: the engine posts it as a command, exactly as a keystroke, a datagram or a database row would. The
/// panel also renders the rolling command log, which is what makes the command stream inspectable rather
/// than a black box.
/// </para>
/// <para>
/// Labels are UTF-8 byte literals rather than strings. The engine is built with invariant globalisation, and
/// passing a UTF-8 literal avoids converting the same label from UTF-16 on every frame.
/// </para>
/// <para>
/// <see cref="BeginFrame"/> builds a frame and exposes its draw data for the render thread to record. The
/// caller must not build another frame until the render thread has consumed that draw data.
/// </para>
/// </remarks>
public sealed class DebugUi : IDisposable
{
    private const int ConsoleInputCapacity = 512;
    private const float ConsoleLogHeight = 200.0f;
    private const int MaxConsoleHistory = 64;

        /// <summary>
        /// The p99 at which a thread's work is highlighted as likely to cause a hitch. Roughly a third of a
        /// 60 Hz frame, so a bad sample is visible against the frame budget rather than arbitrary.
        /// </summary>
        private const double SlowSampleWarningMilliseconds = 5.0;

    private readonly Window _window;
    private readonly ImGuiContextPtr _context;
    private readonly GlfwBackend _glfwBackend;
    private readonly VulkanRenderer _renderer;
    private readonly string _deviceName;
    private readonly string _windowTitle;

    // The command line's editable buffer, reused every frame.
    private readonly ImInputString _consoleInput = new(ConsoleInputCapacity, ReadOnlySpan<byte>.Empty);
    private readonly List<string> _consoleHistory = [];

    // A line chosen from a menu, submitted on the next frame.
    private string? _pendingLine;
    private int _historyIndex = -1;
    private bool _scrollConsoleToBottom = true;
    private bool _showDemoWindow;
    private bool _showConsole = true;
    private bool _showHelp;
    private bool _disposed;

    public DebugUi(Window window, VulkanRenderer renderer, string windowTitle)
    {
        _window = window;
        _renderer = renderer;
        _deviceName = renderer.DeviceName;
        _windowTitle = windowTitle;

        _context = ImGui.CreateContext();

        // Don't write imgui.ini next to the executable.
        ImGui.GetIO().IniFilename = null;

        _glfwBackend = new GlfwBackend(window.Handle, GlfwBackend.ClientApi.Vulkan);
    }

    /// <summary>
    /// Starts an ImGui frame and builds the panels. Call once per frame on the main thread, before handing
    /// <see cref="DrawData"/> to the render thread.
    /// </summary>
    /// <param name="console">A value snapshot of the command system, gathered by the engine this frame.</param>
    /// <param name="submit">
    /// Called with a line the user submitted. The engine posts it as a command; this class never mutates
    /// engine state itself.
    /// </param>
    /// <returns>The window mode selected in the panel, or <c>null</c> when the user left it alone.</returns>
    public WindowMode? BeginFrame(
        SimulationSnapshot snapshot,
        InputSnapshot input,
        double fixedDeltaSeconds,
        ConsoleState console,
        Action<string>? submit
    )
    {
        WindowMode? requestedMode = null;

        _glfwBackend.NewFrame();
        ImGui.NewFrame();

        // Recycle the scratch memory that interpolated ImGui strings ($"...") use, once per frame.
        ImString.SharedStorage.Reset();

        DrawDebugPanel(snapshot, input, fixedDeltaSeconds, console, ref requestedMode);

        if (_showConsole)
            DrawConsolePanel(console, submit);

        if (_showDemoWindow)
            ImGui.ShowDemoWindow();

        ImGui.Render();

        return requestedMode;
    }

    /// <summary>
    /// The draw data for the frame most recently built by <see cref="BeginFrame"/>. Valid until the next
    /// <see cref="BeginFrame"/> call.
    /// </summary>
    public ImDrawDataPtr DrawData => ImGui.GetDrawData();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _glfwBackend.Dispose();
        ImGui.DestroyContext(_context);
    }

    private void DrawDebugPanel(
        SimulationSnapshot snapshot,
        InputSnapshot input,
        double fixedDeltaSeconds,
            ConsoleState console,
        ref WindowMode? requestedMode
    )
    {
        ImGui.SetNextWindowSize(new float2(430, 0), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Debug"u8))
        {
            ImGui.End();
            return;
        }

        var io = ImGui.GetIO();
        ImGui.Text($"FPS: {io.Framerate:0.0} ({io.DeltaTime * 1000.0f:0.00} ms)");
        ImGui.Separator();
        ImGui.Text($"Framebuffer: {_window.FramebufferSize.X}x{_window.FramebufferSize.Y}");
        ImGui.Text($"Swapchain: {_renderer.Extent.Width}x{_renderer.Extent.Height}, {_renderer.ImageCount} images");
        ImGui.Text($"Device: {_deviceName}");

        ImGui.Separator();
        ImGui.Text("Threads"u8);
        ImGui.Text($"Simulation: {snapshot.TicksPerSecond:0.0} ticks/s (target {1.0 / fixedDeltaSeconds:0.0})");
        ImGui.Text($"Tick: {snapshot.Tick}  elapsed {snapshot.ElapsedSeconds:0.00}s");
        ImGui.Text($"Dropped ticks: {snapshot.DroppedTicks}");
        ImGui.Text($"Frames rendered: {snapshot.FramesRendered}");
        ImGui.Text($"Input commands: {snapshot.InputCommandsApplied}");
        ImGui.Text($"IO jobs: {snapshot.IoJobsCompleted}");
        ImGui.Text($"Network messages: {snapshot.NetworkMessagesProcessed}");

                DrawThreadTimings(console);

        ImGui.Separator();
        ImGui.Text("Simulation state"u8);
        ImGui.Text($"Rotation: {snapshot.RotationRadians:0.000} rad");
        ImGui.Text($"Scale: {snapshot.Scale:0.000}");
        ImGui.Text($"Cursor: {input.CursorPosition.X:0.0}, {input.CursorPosition.Y:0.0}");
        ImGui.Text($"Keys held: {input.KeysDown.Count}  Buttons held: {input.ButtonsDown.Count}");
        if (snapshot.Paused)
            ImGui.TextColored(new float4(1.0f, 0.7f, 0.2f, 1.0f), "Simulation paused"u8);
        if (snapshot.PendingSteps > 0)
            ImGui.Text($"Pending steps: {snapshot.PendingSteps}");
        ImGui.TextDisabled("Hold Left/Right arrow to steer the triangle."u8);

        ImGui.Separator();
        ImGui.Text("Window mode"u8);
        var mode = _window.Mode;
        if (ImGui.RadioButton("Windowed"u8, mode == WindowMode.Windowed))
            requestedMode = WindowMode.Windowed;
        if (ImGui.RadioButton("Fullscreen (borderless)"u8, mode == WindowMode.BorderlessFullscreen))
            requestedMode = WindowMode.BorderlessFullscreen;

        ImGui.Separator();
        ImGui.Text($"Window: {_windowTitle}");
        ImGui.Checkbox("ImGui demo window"u8, ref _showDemoWindow);
        ImGui.Checkbox("Command console"u8, ref _showConsole);
        ImGui.TextWrapped("Escape closes the window. F11 toggles fullscreen. Run 'help' for commands."u8);

        ImGui.End();
    }

    private void DrawConsolePanel(ConsoleState console, Action<string>? submit)
    {
        ImGui.SetNextWindowSize(new float2(660, 640), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Command console"u8, ImGuiWindowFlags.MenuBar))
        {
            ImGui.End();
            return;
        }

        DrawConsoleMenuBar();

        ImGui.Text(
            $"accepted {console.AcceptedCount}   pending {console.PendingCount}   "
            + $"engine {console.EngineAppliedCount} / input {console.InputAppliedCount} / sim {console.SimulationAppliedCount}"
        );

        // The drain figures are the frame-budget half of the command system: they say what applying commands
        // actually costs, separate from what the commands did.
        ImGui.Text(
            $"drain {console.LastEngineDrainMilliseconds:0.00} ms   worst {console.MaxEngineDrainMilliseconds:0.00} ms   "
            + $"keys seen {console.InputCommandsObserved}"
        );
        if (console.OverwrittenCount > 0)
            ImGui.TextDisabled($"log discarded {console.OverwrittenCount} older command(s)");

        ImGui.Separator();

        // The input line goes first, so it stays reachable however long the log below grows.
        DrawConsoleInput(submit);

        ImGui.Separator();
        DrawSourceTable(console);

        ImGui.Separator();
        DrawCommandLog(console);

        if (_showHelp)
        {
            ImGui.Separator();
            ImGui.TextWrapped(CommandParser.HelpText);
        }

        ImGui.End();
    }

    private void DrawConsoleMenuBar()
    {
        if (!ImGui.BeginMenuBar())
            return;

        if (ImGui.BeginMenu("Commands"u8))
        {
            // Menu items are shortcuts for typed lines. They go through the same parser and the same bus, so
            // there is no second path into the engine to keep in sync.
            Menu("pause");
            Menu("resume");
            Menu("step 1");
            Menu("step 10");
            Menu("step 60");
            Menu("reset-simulation");
            Menu("window-mode toggle");
            Menu("tick-rate 30");
            Menu("tick-rate 60");
            Menu("tick-rate 240");
            Menu("key down Left");
            Menu("key up Left");
            Menu("quit");

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("View"u8))
        {
            if (ImGui.MenuItem("Help"u8, selected: _showHelp))
                _showHelp = !_showHelp;

            if (ImGui.MenuItem("ImGui demo window"u8, selected: _showDemoWindow))
                _showDemoWindow = !_showDemoWindow;

            ImGui.EndMenu();
        }

        ImGui.EndMenuBar();
    }

    private void Menu(string line)
    {
        if (!ImGui.MenuItem(line))
            return;

        _pendingLine = line;
        _scrollConsoleToBottom = true;
    }

    private void DrawConsoleInput(Action<string>? submit)
    {
        if (_pendingLine is { } pending)
        {
            _consoleInput.SetValue(pending);
            _pendingLine = null;
        }

        ImGui.SetNextItemWidth(-150);
        var submitted = ImGui.InputText(
            "##command"u8,
            _consoleInput,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.EscapeClearsAll
        );

        ImGui.SameLine();
        if (ImGui.Button("Send"u8))
            submitted = true;

        if (_consoleHistory.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("<"u8))
                StepHistory(-1);

            ImGui.SameLine();
            if (ImGui.SmallButton(">"u8))
                StepHistory(1);
        }

        if (!submitted || _consoleInput.IsEmpty)
            return;

        var line = _consoleInput.ToString();
        _consoleInput.Clear();

        if (line.Trim().Length == 0)
            return;

        _scrollConsoleToBottom = true;
        _consoleHistory.Add(line);

        while (_consoleHistory.Count > MaxConsoleHistory)
            _consoleHistory.RemoveAt(0);

        _historyIndex = -1;
        submit?.Invoke(line);
    }

    private void StepHistory(int delta)
    {
        if (_consoleHistory.Count == 0)
            return;

        _historyIndex = _historyIndex < 0
            ? (delta < 0 ? _consoleHistory.Count - 1 : 0)
            : Math.Clamp(_historyIndex + delta, 0, _consoleHistory.Count - 1);

        _consoleInput.SetValue(_consoleHistory[_historyIndex]);
    }

        /// <summary>
        /// Renders the per-thread timing table.
        /// </summary>
        /// <remarks>
        /// Four columns rather than one "average", because they answer different questions. <c>last</c> is
        /// "what just happened", <c>mean</c> is "what normally happens", <c>p99</c> is "what happens on the
        /// bad frames" and <c>max</c> is "the worst it has ever been". A thread whose mean is tiny but whose
        /// p99 is large is the one causing hitches, and a table showing only a mean would hide it entirely.
        /// </remarks>
        private void DrawThreadTimings(ConsoleState console)
        {
            if (!ImGui.CollapsingHeader("Thread timings"u8, ImGuiTreeNodeFlags.DefaultOpen))
                return;

            if (!ImGui.BeginTable("timings"u8, 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.EndTable();
                return;
            }

            ImGui.TableSetupColumn("Thread / work"u8, ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Last ms"u8, ImGuiTableColumnFlags.WidthFixed, 62);
            ImGui.TableSetupColumn("Mean ms"u8, ImGuiTableColumnFlags.WidthFixed, 62);
            ImGui.TableSetupColumn("p99 ms"u8, ImGuiTableColumnFlags.WidthFixed, 62);
            ImGui.TableSetupColumn("Max ms"u8, ImGuiTableColumnFlags.WidthFixed, 62);

            ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
            ImGui.TableSetColumnIndex(0);
            ImGui.Text("Thread / work"u8);
            ImGui.TableSetColumnIndex(1);
            ImGui.Text("Last ms"u8);
            ImGui.TableSetColumnIndex(2);
            ImGui.Text("Mean ms"u8);
            ImGui.TableSetColumnIndex(3);
            ImGui.Text("p99 ms"u8);
            ImGui.TableSetColumnIndex(4);
            ImGui.Text("Max ms"u8);

            foreach (var (key, metrics) in console.ThreadTimings)
            {
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                ImGui.Text(key);

                if (metrics.Samples == 0)
                {
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextDisabled("-"u8);
                    continue;
                }

                // The p99 is tinted once it crosses roughly half a 60 Hz frame: at that point a single sample
                // is eating the budget the whole frame had to spare.
                var p99 = metrics.PercentileMilliseconds(0.99);
                var hot = p99 >= SlowSampleWarningMilliseconds;

                ImGui.TableSetColumnIndex(1);
                ImGui.Text($"{metrics.LastMilliseconds:0.000}");
                ImGui.TableSetColumnIndex(2);
                ImGui.Text($"{metrics.MeanMilliseconds:0.000}");
                ImGui.TableSetColumnIndex(3);
                if (hot)
                    ImGui.TextColored(new float4(1.0f, 0.7f, 0.2f, 1.0f), $"{p99:0.000}");
                else
                    ImGui.Text($"{p99:0.000}");
                ImGui.TableSetColumnIndex(4);
                ImGui.Text($"{metrics.MaxMilliseconds:0.000}");
            }

            ImGui.EndTable();
                    ImGui.TextDisabled($"samples over a rolling window of {ThreadMetrics.WindowSize}");
                }

    private void DrawSourceTable(ConsoleState console)
    {
        if (!ImGui.BeginTable("sources"u8, 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.EndTable();
            return;
        }

        ImGui.TableSetupColumn("Source"u8, ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("Commands"u8, ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Detail"u8, ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);
        ImGui.Text("Source"u8);
        ImGui.TableSetColumnIndex(1);
        ImGui.Text("Commands"u8);
        ImGui.TableSetColumnIndex(2);
        ImGui.Text("Detail"u8);

        Row("console", console.ConsoleLines, "stdin");
        Row("rcon", console.RconLines, $"{console.RconClients} client(s) on 127.0.0.1:{console.RconPort}");
        Row("network", console.NetworkCommands, $"{console.NetworkRejected} rejected datagram(s)");
        Row(
            "web",
            console.WebCommands,
            console.WebPort is { } port
                ? $"http://127.0.0.1:{port}/ with {console.WebRequests} request(s)"
                : "disabled (pass --web=PORT)"
        );
        Row(
            "database",
            console.DatabaseCommands,
            console.DatabaseError is { Length: > 0 } error
                ? $"{console.DatabasePolls} poll(s), {console.DatabaseFailures} failed: {error}"
                : console.DatabasePolls > 0
                    ? $"{console.DatabasePolls} poll(s)"
                    : "disabled (pass --database=PATH)"
        );

        ImGui.EndTable();

        void Row(string source, long count, string detail)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.Text(source);
            ImGui.TableSetColumnIndex(1);
            ImGui.Text($"{count}");
            ImGui.TableSetColumnIndex(2);
            ImGui.TextDisabled(detail);
        }
    }

    private void DrawCommandLog(ConsoleState console)
    {
        if (!ImGui.BeginChild("log"u8, new float2(0, ConsoleLogHeight), ImGuiChildFlags.Borders))
        {
            ImGui.EndChild();
            return;
        }

        for (var i = 0; i < console.CommandCount; i++)
        {
            var command = console.Commands[i];
            if (command is null)
                continue;

            // The sequence, source and target are what make the stream readable: they say when an intent was
            // accepted, where it came from, and which thread was allowed to act on it.
            ImGui.TextColored(
                SourceColor(command.Source),
                $"{command.Sequence,6} {command.Source,-8} {command.Target,-10}"
            );
            ImGui.SameLine();
            ImGui.Text(command.Describe());
        }

        if (_scrollConsoleToBottom)
        {
            ImGui.SetScrollHereY(1.0f);
            _scrollConsoleToBottom = false;
        }

        ImGui.EndChild();
    }

    private static float4 SourceColor(CommandSource source) =>
        source switch
        {
            CommandSource.Input => new float4(0.55f, 0.80f, 1.00f, 1.0f),
            CommandSource.Network => new float4(0.75f, 0.75f, 1.00f, 1.0f),
            CommandSource.Remote => new float4(1.00f, 0.85f, 0.55f, 1.0f),
            CommandSource.Web => new float4(0.60f, 1.00f, 0.75f, 1.0f),
            CommandSource.Database => new float4(1.00f, 0.60f, 0.90f, 1.0f),
            _ => new float4(0.90f, 0.90f, 0.90f, 1.0f),
        };
}

