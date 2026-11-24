using Brutal.GlfwApi;
using Brutal.Numerics;

namespace BrutalTestEngine.Engine;

/// <summary>
/// Owns the GLFW library and a single resizable window created without a graphics API, ready for a
/// Vulkan surface. Windowed and borderless fullscreen are both plain window states; exclusive
/// fullscreen (switching the monitor's video mode) is deliberately not offered.
/// </summary>
public sealed class Window : IDisposable
{
    private readonly GlfwWindow _window;
    private bool _disposed;

    // Geometry to restore when leaving borderless fullscreen.
    private int2 _windowedPosition;
    private int2 _windowedSize;

    private Window(GlfwWindow window)
    {
        _window = window;
        Mode = WindowMode.Windowed;
        _windowedPosition = window.Position;
        _windowedSize = window.Size;
    }

    public GlfwWindow Handle => _window;

    public WindowMode Mode { get; private set; }

    public bool ShouldClose
    {
        get => _window.ShouldClose;
        set => _window.ShouldClose = value;
    }

    /// <summary>
    /// The framebuffer size in pixels. Matches the swapchain extent, or zero on either axis while the
    /// window is minimised.
    /// </summary>
    public int2 FramebufferSize => _window.FramebufferSize;

    /// <summary>
    /// True when there is nothing to draw: minimised, or a zero-sized framebuffer.
    /// </summary>
    public bool IsMinimised => _window.IsIconified || FramebufferSize.X <= 0 || FramebufferSize.Y <= 0;

    public static Window Create(EngineOptions options)
    {
        if (!Glfw.Init())
        {
            var errorCode = Glfw.GetError(out var errorMessage);
            throw new InvalidOperationException($"Failed to initialize GLFW: {errorCode}, {errorMessage}");
        }

        try
        {
            // Vulkan owns all rendering, so GLFW must not create an OpenGL context.
            Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_NO_API);
            Glfw.WindowHint(GlfwWindowHint.Resizable, Glfw.Constants.GLFW_TRUE);

            var window = Glfw.CreateWindow(
                new GlfwWindow.CreateInfo
                {
                    Title = options.Title,
                    Size = new int2(options.Width, options.Height),
                    Monitor = GlfwMonitor.Null,
                    Share = null,
                }
            );

            window.OnKey += (handle, key, _, _, _) =>
            {
                if (key == GlfwKey.Escape)
                    handle.ShouldClose = true;
            };

            var result = new Window(window);
            if (options.StartFullscreen)
                result.SetMode(WindowMode.BorderlessFullscreen);

            return result;
        }
        catch
        {
            Glfw.Terminate();
            throw;
        }
    }

    /// <summary>
    /// Switches between windowed and borderless fullscreen. Borderless fullscreen undecorates the
    /// window and covers the monitor it currently sits on; windowed restores the previous geometry.
    /// </summary>
    public void SetMode(WindowMode mode)
    {
        if (mode == Mode)
            return;

        switch (mode)
        {
            case WindowMode.BorderlessFullscreen:
                // Only remember geometry while genuinely windowed, so repeated toggles keep it.
                _windowedPosition = _window.Position;
                _windowedSize = _window.Size;

                var monitor = FindCurrentMonitor();

                // Undecorate first: decorations shift the client area, so geometry has to come after.
                _window.SetAttribute(GlfwWindowAttribute.Decorated, Glfw.Constants.GLFW_FALSE);
                _window.SetAttribute(GlfwWindowAttribute.Resizable, Glfw.Constants.GLFW_FALSE);
                _window.SetAttribute(GlfwWindowAttribute.Floating, Glfw.Constants.GLFW_TRUE);

                // Cover the monitor exactly rather than its work area, so the taskbar is hidden behind.
                _window.Position = monitor.Position;
                _window.Size = monitor.Size;
                break;

            case WindowMode.Windowed:
                _window.SetAttribute(GlfwWindowAttribute.Floating, Glfw.Constants.GLFW_FALSE);
                _window.SetAttribute(GlfwWindowAttribute.Decorated, Glfw.Constants.GLFW_TRUE);
                _window.SetAttribute(GlfwWindowAttribute.Resizable, Glfw.Constants.GLFW_TRUE);

                _window.Size = _windowedSize;
                _window.Position = _windowedPosition;
                break;
        }

        Mode = mode;
    }

    public void PollEvents() => Glfw.PollEvents();

    /// <summary>
    /// Finds the monitor holding the largest part of the window, falling back to the primary one.
    /// </summary>
    private GlfwMonitor FindCurrentMonitor()
    {
        var windowPosition = _window.Position;
        var windowSize = _window.Size;
        var best = Glfw.PrimaryMonitor;
        var bestOverlap = 0L;

        foreach (var monitor in Glfw.GetMonitors())
        {
            var overlapX = Math.Max(
                0,
                Math.Min(windowPosition.X + windowSize.X, monitor.Position.X + monitor.Size.X)
                    - Math.Max(windowPosition.X, monitor.Position.X)
            );
            var overlapY = Math.Max(
                0,
                Math.Min(windowPosition.Y + windowSize.Y, monitor.Position.Y + monitor.Size.Y)
                    - Math.Max(windowPosition.Y, monitor.Position.Y)
            );

            var overlap = (long)overlapX * overlapY;
            if (overlap <= bestOverlap)
                continue;

            best = monitor;
            bestOverlap = overlap;
        }

        return best;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _window.Dispose();
        Glfw.Terminate();
    }
}
