using Serilog.Events;

namespace BrutalTestEngine.Logging;

/// <summary>
/// Configuration for the engine's logging pipeline.
/// </summary>
/// <remarks>
/// A separate record from <c>EngineOptions</c> because the log pipeline is set up before the engine exists:
/// the host configures logging, then the engine is created, and anything that fails in between still needs
/// somewhere to go.
/// </remarks>
public sealed record EngineLogOptions
{
    /// <summary>
    /// Minimum level accepted by the pipeline at startup. Use <see cref="EngineLog.MinimumLevel"/> to change
    /// it on a running engine.
    /// </summary>
    public LogEventLevel MinimumLevel { get; init; } = LogEventLevel.Information;

    /// <summary>
    /// Writes log events to the console. On by default because a headless run has nowhere else to look.
    /// </summary>
    public bool Console { get; init; } = true;

    /// <summary>Path of the rolling log file, or <c>null</c> for no file sink.</summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// How many events the asynchronous buffer holds before new events are dropped.
    /// </summary>
    /// <remarks>
    /// The buffer is bounded and never blocks the caller, so a thread in a hot loop cannot be stalled by a
    /// slow disk or a paused console. When it is full, events are dropped rather than delayed: a log line
    /// that arrives late misrepresents when the work happened, which is worse than one that never arrives.
    /// </remarks>
    public int AsyncBufferSize { get; init; } = 16 * 1024;

    /// <summary>
    /// Records caller file, line and member on every event. Off by default: it walks the stack once per
    /// event, which is exactly the cost a hot-loop log call must not pay.
    /// </summary>
    public bool IncludeCallerInfo { get; init; }
}
