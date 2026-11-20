using BrutalTestEngine.Engine;

namespace BrutalTestEngine.Tests.Engine;

/// <summary>
/// The registry is what makes the timings comparable, so its two guarantees matter: a key always maps to
/// the same metric instance, and the enumeration order is stable enough to read.
/// </summary>
[TestFixture]
public class ThreadTimerRegistryTests
{
    [Test]
    public void The_same_key_returns_the_same_instance()
    {
        var timers = new ThreadTimerRegistry();

        // Callers resolve their metric once in a constructor and then record in a hot loop, so a second
        // Get that produced a new instance would silently split one thread's samples across two metrics.
        Assert.That(timers.Get("sim.tick"), Is.SameAs(timers.Get("sim.tick")));
    }

    [Test]
    public void Different_keys_are_independent()
    {
        var timers = new ThreadTimerRegistry();

        var tick = timers.Get(ThreadTimerRegistry.Keys.SimulationTick);
        var render = timers.Get(ThreadTimerRegistry.Keys.RenderFrame);

        Assert.That(tick, Is.Not.SameAs(render));

        tick.Record(1.0);

        Assert.Multiple(() =>
        {
            Assert.That(tick.Samples, Is.EqualTo(1));
            Assert.That(render.Samples, Is.Zero);
        });
    }

    [Test]
    public void Metrics_are_created_lazily_so_an_untouched_thread_costs_nothing()
    {
        var timers = new ThreadTimerRegistry();

        Assert.That(timers.Snapshot(), Is.Empty);

        timers.Get(ThreadTimerRegistry.Keys.IoJob);

        Assert.That(timers.Snapshot(), Has.Length.EqualTo(1));
    }

    [Test]
    public void Snapshot_is_ordered_by_key_so_the_panel_does_not_jump_around()
    {
        var timers = new ThreadTimerRegistry();

        // Registered deliberately out of order.
        timers.Get(ThreadTimerRegistry.Keys.SimulationTick);
        timers.Get(ThreadTimerRegistry.Keys.IoJob);
        timers.Get(ThreadTimerRegistry.Keys.MainFrame);

        var keys = timers.Snapshot().Select(pair => pair.Key).ToArray();

        Assert.That(keys, Is.Ordered);
    }

    [Test]
    public void A_blank_key_is_a_programming_error()
    {
        var timers = new ThreadTimerRegistry();

        Assert.Multiple(() =>
        {
            Assert.That(() => timers.Get(string.Empty), Throws.ArgumentException);
            Assert.That(() => timers.Get("   "), Throws.ArgumentException);

                // ArgumentNullException derives from ArgumentException, so this would pass either way; the
                // point is that a null key is rejected rather than silently creating a metric named "null".
                Assert.That(() => timers.Get(null!), Throws.ArgumentNullException);
        });
    }

    [Test]
    public void Describe_reports_an_unsampled_metric_rather_than_pretending_it_is_zero()
    {
        var timers = new ThreadTimerRegistry();
        timers.Get(ThreadTimerRegistry.Keys.DatabasePoll);

        var lines = new List<string>();
        timers.Describe(lines.Add);

        // A metric that never fired is meaningfully different from one that fired and measured 0 ms.
        Assert.That(lines, Has.Count.EqualTo(1));
        Assert.That(lines[0], Does.Contain("no samples"));
    }

    [Test]
    public void Describe_writes_one_aligned_line_per_metric()
    {
        var timers = new ThreadTimerRegistry();
        timers.Get(ThreadTimerRegistry.Keys.SimulationTick).Record(1.0);
        timers.Get(ThreadTimerRegistry.Keys.RenderFrame).Record(2.0);

        var lines = new List<string>();
        timers.Describe(lines.Add);

        Assert.That(lines, Has.Count.EqualTo(2));
        Assert.That(lines, Has.All.Contain("mean"));
    }

    [Test]
    public void Every_declared_key_is_a_distinct_non_empty_string()
    {
        // The keys are the panel's row labels and the console summary's left column, so a duplicate would
        // silently merge two metrics into one row.
        var keys = typeof(ThreadTimerRegistry.Keys)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(keys, Is.Not.Empty);
            Assert.That(keys, Has.All.Not.Null.And.Not.Empty);
                    Assert.That(keys.Distinct().ToArray(), Has.Length.EqualTo(keys.Length));
        });
    }

    [Test]
    public void Concurrent_gets_of_the_same_key_agree()
    {
        var timers = new ThreadTimerRegistry();
        var results = new ThreadMetrics[8];

        var threads = new Thread[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            var index = i;
            threads[i] = new Thread(() => results[index] = timers.Get(ThreadTimerRegistry.Keys.IoJob));
        }

        foreach (var thread in threads)
            thread.Start();

        foreach (var thread in threads)
            thread.Join();

        Assert.That(results.Distinct().ToArray(), Has.Length.EqualTo(1));
    }
}