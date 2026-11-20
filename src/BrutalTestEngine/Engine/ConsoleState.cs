using BrutalTestEngine.Commands;

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

        /// <summary>
        /// Per-thread timing for the session, ordered by key. Shared by reference rather than copied because
        /// the panel only reads it for display; nothing here mutates it.
        /// </summary>
        public required KeyValuePair<string, ThreadMetrics>[] ThreadTimings { get; init; }
}