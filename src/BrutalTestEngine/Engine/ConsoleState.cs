using BrutalTestEngine.Commands;
using BrutalTestEngine.Logging;

namespace BrutalTestEngine.Engine;

/// <summary>
/// A read-only view of the command system for the debug console, gathered on the main thread.
/// </summary>
/// <remarks>
/// The UI needs the recent command stream and the per-source counts, but it must not reach into the bus, the
/// sockets or the database from inside ImGui. The main thread collects a value snapshot once per frame and
/// the panel renders that, so the panel cannot race a source thread and the source threads never touch ImGui.
/// </remarks>
public sealed class ConsoleState
{
    /// <summary>The most recent commands, oldest first. Owned by the engine and reused each frame.</summary>
    public required Command[] Commands { get; init; }

    /// <summary>Number of valid entries in <see cref="Commands"/>.</summary>
    public required int CommandCount { get; init; }

    /// <summary>Total commands accepted from every source.</summary>
    public required long AcceptedCount { get; init; }

    /// <summary>Commands accepted but not yet applied.</summary>
    public required int PendingCount { get; init; }

    /// <summary>Engine commands the main thread has applied.</summary>
    public required long EngineAppliedCount { get; init; }

    /// <summary>Input commands the input thread has applied.</summary>
    public required long InputAppliedCount { get; init; }

    /// <summary>Simulation commands the simulation thread has applied.</summary>
    public required long SimulationAppliedCount { get; init; }

    /// <summary>Commands overwritten in the log because it was full.</summary>
    public required long OverwrittenCount { get; init; }

    /// <summary>The tick rate the engine is currently configured for.</summary>
    public required double TicksPerSecond { get; init; }

    /// <summary>Loopback port of the remote console listener.</summary>
    public required int RconPort { get; init; }

    /// <summary>Clients currently connected to the remote console.</summary>
    public required int RconClients { get; init; }

    /// <summary>Lines received over the remote console.</summary>
    public required long RconLines { get; init; }

    /// <summary>Lines read from the host console.</summary>
    public required long ConsoleLines { get; init; }

    /// <summary>Commands decoded from network datagrams.</summary>
    public required long NetworkCommands { get; init; }

    /// <summary>Datagrams that could not be decoded.</summary>
    public required long NetworkRejected { get; init; }

    /// <summary>Web listener port, when enabled.</summary>
    public required int? WebPort { get; init; }

    /// <summary>HTTP requests received.</summary>
    public required long WebRequests { get; init; }

    /// <summary>Commands posted from web requests.</summary>
    public required long WebCommands { get; init; }

    /// <summary>Commands posted from watched database rows.</summary>
    public required long DatabaseCommands { get; init; }

    /// <summary>MCP listener port, when enabled.</summary>
    public required int? McpPort { get; init; }

    /// <summary>Variables the MCP server exposes.</summary>
    public required int McpCvarCount { get; init; }

    /// <summary>MCP requests received.</summary>
    public required long McpRequests { get; init; }

    /// <summary>Commands posted from MCP tool calls.</summary>
    public required long McpCommands { get; init; }

    /// <summary>Completed database poll cycles.</summary>
    public required long DatabasePolls { get; init; }

    /// <summary>Database polls that threw.</summary>
    public required long DatabaseFailures { get; init; }

    /// <summary>The most recent database error, if any.</summary>
    public required string? DatabaseError { get; init; }

    /// <summary>Key commands the GLFW callback has posted, including ones a binding also acted on.</summary>
    public required long InputCommandsObserved { get; init; }

    /// <summary>Time the most recent engine drain took, in milliseconds.</summary>
    public required double LastEngineDrainMilliseconds { get; init; }

    /// <summary>Slowest engine drain seen so far, in milliseconds.</summary>
    public required double MaxEngineDrainMilliseconds { get; init; }

    /// <summary>Name of the network transport the engine is running on, e.g. "raknet".</summary>
    public required string NetworkTransport { get; init; }

    /// <summary>Endpoint the network transport is bound to.</summary>
    public required string NetworkEndpoint { get; init; }

    /// <summary>The most recent network thread failure, or <c>null</c> when it has not faulted.</summary>
    public required string? NetworkFault { get; init; }

    /// <summary>True when the Vulkan debug messenger and debug labels are active.</summary>
    public required bool TracingEnabled { get; init; }

    /// <summary>True when RenderDoc's in-application API is available.</summary>
    public required bool RenderDocAvailable { get; init; }

    /// <summary>The RenderDoc version, when it is attached.</summary>
    public required string? RenderDocVersion { get; init; }

    /// <summary>Captures RenderDoc has written this session.</summary>
    public required uint CaptureCount { get; init; }

    /// <summary>The log pipeline's current minimum level.</summary>
    public required string LogLevel { get; init; }

    /// <summary>Log events dropped because the asynchronous buffer was full.</summary>
    public required long LogDroppedCount { get; init; }

    /// <summary>
    /// The most recent log events, oldest first. Owned by the engine and reused each frame, exactly like
    /// <see cref="Commands"/>.
    /// </summary>
    public required LogEntry[] LogEvents { get; init; }

    /// <summary>Number of valid entries in <see cref="LogEvents"/>.</summary>
    public required int LogEventCount { get; init; }

    /// <summary>Log events overwritten in the window's ring buffer because it was full.</summary>
    public required long LogEventOverwrittenCount { get; init; }

    /// <summary>The most recent physics thread failure, or <c>null</c> when it has not faulted.</summary>
    public required string? PhysicsFault { get; init; }

    /// <summary>Name of the physics backend, e.g. "jolt".</summary>
    public required string PhysicsBackend { get; init; }

    /// <summary>True when a physics backend initialized and can be stepped.</summary>
    public required bool PhysicsAvailable { get; init; }

    /// <summary>Bodies currently in the physics world.</summary>
    public required int PhysicsBodies { get; init; }

    /// <summary>Physics steps performed this session.</summary>
    public required long PhysicsSteps { get; init; }

    /// <summary>Name of the audio backend, e.g. "fmod".</summary>
    public required string AudioBackend { get; init; }

    /// <summary>True when an audio device was opened.</summary>
    public required bool AudioAvailable { get; init; }

    /// <summary>Channels the mixer is currently playing.</summary>
    public required int AudioChannels { get; init; }

    /// <summary>Mixer updates performed this session.</summary>
    public required long AudioUpdates { get; init; }

    /// <summary>
    /// Per-thread timing for the session, ordered by key. Shared by reference rather than copied because
    /// the panel only reads it for display; nothing here mutates it.
    /// </summary>
    public required KeyValuePair<string, ThreadMetrics>[] ThreadTimings { get; init; }

    /// <summary>True when the world is being generated and drawn at all.</summary>
    public required bool WorldEnabled { get; init; }

    /// <summary>World-generation worker threads.</summary>
    public required int WorldWorkers { get; init; }

    /// <summary>Bricks currently holding geometry.</summary>
    public required long WorldResidentBricks { get; init; }

    /// <summary>Meshlets built this session.</summary>
    public required long WorldMeshlets { get; init; }

    /// <summary>Meshlet slots the arena has handed out.</summary>
    public required int WorldArenaLive { get; init; }

    /// <summary>Meshlet slots the arena can hand out.</summary>
    public required int WorldArenaCapacity { get; init; }

    /// <summary>Largest contiguous run of free meshlet slots.</summary>
    public required int WorldArenaLargestFree { get; init; }

    /// <summary>Brick builds queued but not yet finished.</summary>
    public required long WorldInFlight { get; init; }

    /// <summary>Finished bricks waiting to be committed.</summary>
    public required int WorldWaiting { get; init; }

    /// <summary>Bricks that produced no geometry because they held no surface.</summary>
    public required long WorldEmpty { get; init; }

    /// <summary>Results discarded because a recentring invalidated them.</summary>
    public required long WorldStale { get; init; }

    /// <summary>Commits that could not get a contiguous run of meshlet slots.</summary>
    public required long WorldOverflow { get; init; }

    /// <summary>Slots the last streamer pass considered.</summary>
    public required int WorldCandidates { get; init; }

    /// <summary>Slots the last streamer pass believes contain surface.</summary>
    public required int WorldSurface { get; init; }

    /// <summary>Slots the last streamer pass skipped as all air or all rock.</summary>
    public required int WorldSkipped { get; init; }

    /// <summary>True when the last streamer pass stopped early because the pool or queue was full.</summary>
    public required bool WorldBudgetExhausted { get; init; }

    /// <summary>Radius the coarsest clipmap level covers, in metres.</summary>
    public required float WorldViewDistance { get; init; }

    /// <summary>The position the last streamer pass ran for.</summary>
    public required string WorldCamera { get; init; }
}
