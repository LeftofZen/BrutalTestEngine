using BrutalTestEngine.Input;

namespace BrutalTestEngine.Simulation;

/// <summary>
/// A view of the simulation at one tick, published by the simulation thread and read by the main thread.
/// Every field is a value type or an immutable reference, so the reader can use it without locking while
/// the simulation keeps running.
/// </summary>
/// <remarks>
/// Instances are reused by the triple buffer that carries them, so the setters are internal: the buffer
/// guarantees the writer and the reader never hold the same instance at the same time.
/// </remarks>
public sealed class SimulationSnapshot
{
    /// <summary>Number of ticks simulated so far.</summary>
    public long Tick { get; internal set; }

    /// <summary>Simulated time in seconds, advanced by the fixed timestep.</summary>
    public double ElapsedSeconds { get; internal set; }

    /// <summary>Rotation of the demo triangle in radians, driven by the simulation.</summary>
    public float RotationRadians { get; internal set; }

    /// <summary>Uniform scale applied to the demo triangle.</summary>
    public float Scale { get; internal set; } = 1.0f;

    /// <summary>Number of ticks the simulation managed in the last wall-clock second.</summary>
    public double TicksPerSecond { get; internal set; }

    /// <summary>Number of ticks that were dropped because the simulation fell behind.</summary>
    public long DroppedTicks { get; internal set; }

    /// <summary>Number of ticks the simulation thread has executed.</summary>
    public long TicksExecuted { get; internal set; }

    /// <summary>Number of simulation commands the simulation thread has applied.</summary>
    public long CommandsApplied { get; internal set; }

    /// <summary>Number of single-step ticks still queued.</summary>
    public long PendingSteps { get; internal set; }

    /// <summary>True while the passage of simulated time is halted.</summary>
    public bool Paused { get; internal set; }

    /// <summary>Number of frames the render thread has presented.</summary>
    public long FramesRendered { get; internal set; }

    /// <summary>Number of input commands the input thread has applied.</summary>
    public long InputCommandsApplied { get; internal set; }

    /// <summary>Number of IO jobs the IO thread has completed.</summary>
    public long IoJobsCompleted { get; internal set; }

    /// <summary>Number of network messages the network thread has processed.</summary>
    public long NetworkMessagesProcessed { get; internal set; }

    /// <summary>Number of engine commands still waiting for the main thread to apply them.</summary>
    public int PendingEngineCommands { get; internal set; }

    /// <summary>Current input state as seen by the simulation.</summary>
    public InputSnapshot Input { get; internal set; } = InputSnapshot.Empty;

    /// <summary>Creates an empty snapshot for the initial publish.</summary>
    public static SimulationSnapshot Empty { get; } = new();
}
