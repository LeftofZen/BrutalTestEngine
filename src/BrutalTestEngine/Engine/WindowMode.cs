namespace BrutalTestEngine.Engine;

/// <summary>
/// How the main window is presented. There is no exclusive fullscreen mode: borderless fullscreen is
/// an undecorated window sized to the monitor.
/// </summary>
public enum WindowMode
{
    /// <summary>A decorated, resizable window.</summary>
    Windowed,

    /// <summary>An undecorated window covering the whole monitor.</summary>
    BorderlessFullscreen,
}
