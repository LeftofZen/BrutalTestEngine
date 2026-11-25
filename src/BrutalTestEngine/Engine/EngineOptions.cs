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
    /// Loopback port for the MCP server, or <c>null</c> to leave it off.
    /// </summary>
    /// <remarks>
    /// Off by default because it is a debugging surface, not part of the game. Like the web listener it binds
    /// to 127.0.0.1 only, for the same reason: it can issue commands, so exposing it publicly would be a
    /// vulnerability. See <see cref="Host.McpServerThread"/> for what it exposes.
    /// </remarks>
    public int? McpPort { get; init; }

    /// <summary>
    /// Creates the transport the network thread drives, or <c>null</c> for the default RakNet one.
    /// </summary>
    /// <remarks>
    /// The engine itself never names a networking library; this is the single hook a host uses to supply one.
    /// See <see cref="Networking.NetworkTransports"/> for the built-in choices and
    /// <see cref="Networking.INetworkTransport"/> for the contract a custom transport must satisfy.
    /// </remarks>
    public Func<Networking.INetworkTransport>? TransportFactory { get; init; }

    /// <summary>
    /// Port the network transport binds to, or zero to let the operating system choose.
    /// </summary>
    public int NetworkPort { get; init; }

    /// <summary>
    /// Creates the physics world the physics thread owns, or <c>null</c> for the default, which is
    /// <see cref="PhysicsWorlds.None"/>.
    /// </summary>
    /// <remarks>
    /// Not Jolt by default, because BRUTAL's Jolt binding currently faults inside <c>PhysicsSystem.Update</c>
    /// once a body exists. Pass <see cref="PhysicsWorlds.Jolt"/> to opt in; see that property for the detail.
    /// The engine never names a physics library; this is the hook a host uses to supply one, or to switch
    /// physics off entirely with <see cref="PhysicsWorlds.None"/>. See
    /// <see cref="Physics.IPhysicsWorld"/> for the contract.
    /// </remarks>
    public Func<Physics.IPhysicsWorld>? PhysicsFactory { get; init; }

    /// <summary>
    /// Creates the audio system the audio thread owns, or <c>null</c> for BRUTAL's FMOD backend.
    /// </summary>
    /// <remarks>
    /// Like physics, the library is chosen here and nowhere else. Audio degrades to unavailable when the
    /// platform has no device, which is the normal case on a headless server.
    /// </remarks>
    public Func<Audio.IAudioSystem>? AudioFactory { get; init; }

    /// <summary>
    /// Enables the Vulkan debug-utils messenger and command-buffer debug labels.
    /// </summary>
    /// <remarks>
    /// Both trace tools key off this. A GPU capture without labels is a flat list of draws with no idea which
    /// pass produced them, so the labels are what make a RenderDoc or Tracy capture readable rather than
    /// merely possible.
    /// </remarks>
    public bool EnableTracing { get; init; }

    /// <summary>
    /// Enables RenderDoc in-application capture when <c>renderdoc.dll</c> is loaded.
    /// </summary>
    /// <remarks>
    /// Safe to leave on: when RenderDoc is not attached the hook is a null check, and no capture can be
    /// triggered. It is off by default only because it is a genuinely optional tool.
    /// </remarks>
    public bool EnableRenderDoc { get; init; }

    /// <summary>
    /// Directory RenderDoc writes captures to, or <c>null</c> for RenderDoc's own default.
    /// </summary>
    public string? CaptureDirectory { get; init; }

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

    /// <summary>
    /// The world's shape, or <c>null</c> for <see cref="World.WorldSettings.Default"/>.
    /// </summary>
    /// <remarks>
    /// Exposed so a host can shrink the clipmap for a quick run. The default is nine levels of twelve bricks
    /// per axis, which is a view distance of nearly twenty-five kilometres and takes a few seconds of worker
    /// time to fill in from cold.
    /// </remarks>
    public World.WorldSettings? World { get; init; }

    /// <summary>
    /// The world job system's tuning, or <c>null</c> for <see cref="Jobs.WorldJobSystemOptions.Default"/>.
    /// </summary>
    public Jobs.WorldJobSystemOptions? WorldJobs { get; init; }

    /// <summary>
    /// Whether to generate and draw the world at all. On by default; off is useful for isolating a rendering
    /// problem from the world pipeline.
    /// </summary>
    public bool EnableWorld { get; init; } = true;
}
