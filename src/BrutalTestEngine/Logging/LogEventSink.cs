using System.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace BrutalTestEngine.Logging;

/// <summary>
/// Feeds the debug window's ring buffer from the Serilog pipeline.
/// </summary>
/// <remarks>
/// Registered inside an asynchronous sink, so <see cref="Emit"/> runs on the pipeline's background writer
/// rather than on the thread that logged. Rendering the message here is therefore free as far as the caller
/// is concerned, which is what lets the window show the same events the console does without reintroducing
/// the cost the asynchronous pipeline exists to avoid.
/// </remarks>
internal sealed class LogEventSink(LogEventBuffer buffer) : ILogEventSink
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        buffer.Add(
            new LogEntry(
                logEvent.Level,
                _clock.Elapsed.TotalSeconds,
                Read(logEvent, "SourceContext") ?? "engine",
                Read(logEvent, "ThreadName") ?? "-",
                logEvent.RenderMessage()
            )
        );
    }

    /// <summary>
    /// Reads a string-valued property, or <c>null</c> when it is absent or not a scalar string. Both
    /// properties this reads are added by the pipeline's own configuration, so a missing one means the
    /// configuration changed rather than that the event is unusual.
    /// </summary>
    private static string? Read(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: string text }
            ? text
            : null;
}
