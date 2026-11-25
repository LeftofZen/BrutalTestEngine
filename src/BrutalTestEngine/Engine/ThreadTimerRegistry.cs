using System.Diagnostics;

namespace BrutalTestEngine.Engine;

/// <summary>
/// The engine's timing registry: one named <see cref="ThreadMetrics"/> per piece of work, collected in
/// one place so the debug panel and the run summary describe every thread the same way.
/// </summary>
/// <remarks>
/// <para>
/// A registry rather than a metrics field on each thread, because the question the panel answers is
/// comparative — "which thread is costing me this frame?" — and that needs the numbers side by side.
/// It also keeps the instrumentation out of the threads' own public surfaces: a thread's job is to do
/// its work, not to know how it is being measured.
/// </para>
/// <para>
/// Keys are stable strings so the panel can render a fixed table and a user reading the console can
/// grep for one. They are deliberately not localised or reordered by insertion: a metric that moves
/// around the panel is a metric nobody can read.
/// </para>
/// </remarks>
public sealed class ThreadTimerRegistry
{
    /// <summary>Stable keys for every metric the engine records.</summary>
    public static class Keys
    {
        /// <summary>One simulation tick's work, measured inside the tick loop.</summary>
        public const string SimulationTick = "sim.tick";

        /// <summary>One full simulation loop cycle, including waiting for the next tick.</summary>
        public const string SimulationCycle = "sim.cycle";

        /// <summary>Draining and applying simulation-targeted commands.</summary>
        public const string SimulationDrain = "sim.drain";

        /// <summary>Building one ImGui frame on the main thread.</summary>
        public const string MainFrame = "main.frame";

        /// <summary>Draining and applying engine-targeted commands.</summary>
        public const string MainDrain = "main.drain";

        /// <summary>Blocked waiting for the render thread to finish with a frame.</summary>
        public const string MainWaitForRender = "main.wait";

        /// <summary>Recording and submitting one GPU frame on the render thread.</summary>
        public const string RenderFrame = "render.frame";

        /// <summary>Draining and applying input-targeted commands.</summary>
        public const string InputDrain = "input.drain";

        /// <summary>Publishing the input snapshot as a whole, including the rate limit.</summary>
        public const string InputPublish = "input.publish";

        /// <summary>Executing one IO job.</summary>
        public const string IoJob = "io.job";

        /// <summary>One network poll cycle: send the queue, then receive.</summary>
        public const string NetworkPoll = "net.poll";

        /// <summary>Decoding and dispatching one received datagram.</summary>
        public const string NetworkDecode = "net.decode";

        /// <summary>One database poll cycle.</summary>
        public const string DatabasePoll = "db.poll";

        /// <summary>Advancing the physics world by one iteration's worth of time.</summary>
        public const string PhysicsStep = "phys.step";

        /// <summary>Draining and applying physics-targeted commands.</summary>
        public const string PhysicsDrain = "phys.drain";

        /// <summary>One mixer update.</summary>
        public const string AudioUpdate = "audio.update";

        /// <summary>Draining and applying audio-targeted commands.</summary>
        public const string AudioDrain = "audio.drain";

        /// <summary>Waiting for the next MCP request to arrive.</summary>
        public const string McpPoll = "mcp.poll";

        /// <summary>Handling one MCP request, from parse to reply.</summary>
        public const string McpRequest = "mcp.request";

        /// <summary>Building one brick on a world-generation worker: field, extraction and splitting.</summary>
        public const string WorldBuild = "world.build";

        /// <summary>A world-generation worker waiting for a job to arrive.</summary>
        public const string WorldWait = "world.wait";

        /// <summary>One streamer pass: deciding which bricks a level needs and requesting them.</summary>
        public const string WorldStream = "world.stream";

        /// <summary>Committing a worker's finished geometry into the destination pools.</summary>
        public const string WorldCommit = "world.commit";
    }

    private readonly Dictionary<string, ThreadMetrics> _metrics = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// Returns the metric registered under <paramref name="key"/>, creating it on first use.
    /// </summary>
    /// <remarks>
    /// Creation is lazy and under a lock so callers can take a reference once in their constructor and
    /// then record in a hot loop without any dictionary lookup. Callers are expected to keep the
    /// reference; calling this per measurement would defeat the point.
    /// </remarks>
    public ThreadMetrics Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            if (_metrics.TryGetValue(key, out var existing))
                return existing;

            var created = new ThreadMetrics();
            _metrics[key] = created;
            return created;
        }
    }

    /// <summary>Every registered metric, ordered by key so the panel layout is stable.</summary>
    public KeyValuePair<string, ThreadMetrics>[] Snapshot()
    {
        lock (_gate)
            return [.. _metrics.OrderBy(pair => pair.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Writes every metric to <paramref name="write"/>, aligned by key, for the run summary.
    /// </summary>
    public void Describe(Action<string> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        foreach (var (key, metrics) in Snapshot())
        {
            if (metrics.Samples == 0)
            {
                write($"  {key,-16} no samples");
                continue;
            }

            write(
                $"  {key,-16} last {metrics.LastMilliseconds,7:0.000} ms   "
                    + $"mean {metrics.MeanMilliseconds,7:0.000} ms   "
                    + $"p99 {metrics.PercentileMilliseconds(0.99),7:0.000} ms   "
                    + $"max {metrics.MaxMilliseconds,8:0.000} ms   "
                    + $"{metrics.Samples} sample(s)"
            );
        }
    }
}
