namespace BrutalTestEngine.Engine;

/// <summary>
/// Configuration for a <see cref="Engine"/> run.
/// </summary>
public sealed record EngineOptions
{
    public string Title { get; init; } = "Brutal Test Engine";

    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;

    /// <summary>
    /// Number of frames to render before exiting, or <c>null</c> to run until the window is closed.
    /// Useful for smoke-testing the whole startup/render/shutdown path.
    /// </summary>
    public int? FrameLimit { get; init; }

    /// <summary>
    /// Enables the Vulkan validation layer when it's installed.
    /// </summary>
    public bool EnableValidation { get; init; }

    /// <summary>
    /// Starts in borderless fullscreen instead of a windowed, resizable window.
    /// </summary>
    public bool StartFullscreen { get; init; }

    /// <summary>
    /// Simulation ticks per second. The simulation runs on its own thread at this fixed rate,
    /// independently of the frame rate.
    /// </summary>
    public double TicksPerSecond { get; init; } = 60.0;

    /// <summary>
    /// Loopback port for the web command listener, or <c>null</c> to leave it off. The listener is bound to
    /// 127.0.0.1 only: an unauthenticated command endpoint on a public interface would be a vulnerability.
    /// </summary>
    public int? WebPort { get; init; }

    /// <summary>
    /// Path to a SQLite database whose command table is watched for new rows, or <c>null</c> to leave the
    /// database source off.
    /// </summary>
    public string? DatabasePath { get; init; }

    /// <summary>
    /// Drives every command source from outside the engine and reports which one delivered what, then exits.
    /// Used to prove the command system end to end in a single run.
    /// </summary>
    public bool RunCommandSelfTest { get; init; }
}
