namespace BrutalTestEngine.Engine;

/// <summary>
/// A one-call summary of the engine, for a consumer that wants the headline numbers rather than every
/// variable.
/// </summary>
/// <remarks>
/// Deliberately a value type with no references to engine objects: it is produced on the main thread and read
/// by the MCP thread, so it must be a copy rather than a view. The per-thread timings are the one exception,
/// and they are already a snapshot taken by the registry.
/// </remarks>
/// <param name="Frames">Frames the render thread has presented.</param>
/// <param name="Ticks">Simulation ticks executed.</param>
/// <param name="TicksPerSecond">The simulation's measured tick rate.</param>
/// <param name="CommandsAccepted">Commands accepted from every source.</param>
/// <param name="CommandsPending">Commands accepted but not yet applied.</param>
/// <param name="WindowMode">The current window mode.</param>
/// <param name="NetworkTransport">The name of the network transport in use.</param>
/// <param name="NetworkEndpoint">The endpoint the transport is bound to.</param>
/// <param name="PhysicsBackend">The name of the physics backend.</param>
/// <param name="PhysicsAvailable">True when the physics backend initialized.</param>
/// <param name="AudioBackend">The name of the audio backend.</param>
/// <param name="AudioAvailable">True when an audio device was opened.</param>
/// <param name="RenderDocAvailable">True when RenderDoc's in-application API is attached.</param>
/// <param name="LogLevel">The log pipeline's current minimum level.</param>
/// <param name="ThreadTimings">Per-thread timing, keyed by metric name.</param>
/// <param name="PhysicsFault">The most recent physics thread failure, or <c>null</c> when it has not faulted.</param>
/// <param name="NetworkFault">The most recent network thread failure, or <c>null</c> when it has not faulted.</param>
public readonly record struct EngineStatus(
    long Frames,
    long Ticks,
    double TicksPerSecond,
    long CommandsAccepted,
    int CommandsPending,
    string WindowMode,
    string NetworkTransport,
    string NetworkEndpoint,
    string PhysicsBackend,
    bool PhysicsAvailable,
    string AudioBackend,
    bool AudioAvailable,
    bool RenderDocAvailable,
    string LogLevel,
    Dictionary<string, string> ThreadTimings,
    string? PhysicsFault,
    string? NetworkFault
);
