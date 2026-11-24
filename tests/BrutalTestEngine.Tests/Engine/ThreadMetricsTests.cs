using BrutalTestEngine.Engine;

namespace BrutalTestEngine.Tests.Engine;

/// <summary>
/// These statistics are what an operator uses to decide which thread is costing a frame, so their
/// arithmetic has to be right: a mean that lags, a percentile that is off by one, or a max that forgets
/// a spike all send someone optimising the wrong thing.
/// </summary>
[TestFixture]
public class ThreadMetricsTests
{
    [Test]
    public void A_fresh_metric_reports_zero_rather_than_nan()
    {
        var metrics = new ThreadMetrics();

        // Dividing by zero samples would produce NaN, which formats as "NaN ms" in the panel and pins a
        // table column to the wrong width.
        Assert.Multiple(() =>
        {
            Assert.That(metrics.Samples, Is.Zero);
            Assert.That(metrics.LastMilliseconds, Is.Zero);
            Assert.That(metrics.MeanMilliseconds, Is.Zero);
            Assert.That(metrics.MaxMilliseconds, Is.Zero);
            Assert.That(metrics.LifetimeMeanMilliseconds, Is.Zero);
            Assert.That(metrics.PercentileMilliseconds(0.99), Is.Zero);
            Assert.That(metrics.Snapshot(), Is.Empty);
        });
    }

    [Test]
    public void Records_the_last_sample()
    {
        var metrics = new ThreadMetrics();

        metrics.Record(1.5);
        metrics.Record(2.5);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.Samples, Is.EqualTo(2));
            Assert.That(metrics.LastMilliseconds, Is.EqualTo(2.5));
        });
    }

    [Test]
    public void Max_is_a_session_high_water_mark_that_survives_the_window_rolling_over()
    {
        var metrics = new ThreadMetrics();

        // A single spike early in the run.
        metrics.Record(500.0);
        for (var i = 0; i < ThreadMetrics.WindowSize * 2; i++)
            metrics.Record(1.0);

        Assert.Multiple(() =>
        {
            // The mean has forgotten the spike...
            Assert.That(metrics.MeanMilliseconds, Is.EqualTo(1.0).Within(0.001));

            // ...but the max has not, which is the point: "the worst it has ever been" is a session fact.
            Assert.That(metrics.MaxMilliseconds, Is.EqualTo(500.0));
        });
    }

    [Test]
    public void Mean_describes_the_rolling_window_not_the_whole_session()
    {
        var metrics = new ThreadMetrics();

        for (var i = 0; i < ThreadMetrics.WindowSize; i++)
            metrics.Record(100.0);

        Assert.That(metrics.MeanMilliseconds, Is.EqualTo(100.0).Within(0.001));

        // Fill the window again with a different value; the mean must follow the recent behaviour.
        for (var i = 0; i < ThreadMetrics.WindowSize; i++)
            metrics.Record(2.0);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.MeanMilliseconds, Is.EqualTo(2.0).Within(0.001));
            Assert.That(metrics.LifetimeMeanMilliseconds, Is.EqualTo(51.0).Within(0.001));
        });
    }

    [Test]
    public void Percentiles_reflect_the_distribution()
    {
        var metrics = new ThreadMetrics();

        // 1..100, so the p50 is 50.5 and the p99 is 99.01 by linear interpolation.
        for (var i = 1; i <= 100; i++)
            metrics.Record(i);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.PercentileMilliseconds(0.5), Is.EqualTo(50.5).Within(0.001));
            Assert.That(metrics.PercentileMilliseconds(0.99), Is.EqualTo(99.01).Within(0.001));
            Assert.That(metrics.PercentileMilliseconds(1.0), Is.EqualTo(100.0).Within(0.001));
            Assert.That(metrics.PercentileMilliseconds(0.0), Is.EqualTo(1.0).Within(0.001));
        });
    }

    [Test]
    public void A_single_sample_is_every_percentile()
    {
        var metrics = new ThreadMetrics();
        metrics.Record(7.0);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.PercentileMilliseconds(0.0), Is.EqualTo(7.0));
            Assert.That(metrics.PercentileMilliseconds(0.5), Is.EqualTo(7.0));
            Assert.That(metrics.PercentileMilliseconds(0.99), Is.EqualTo(7.0));
            Assert.That(metrics.PercentileMilliseconds(1.0), Is.EqualTo(7.0));
        });
    }

    [Test]
    public void An_out_of_range_percentile_is_clamped_rather_than_throwing()
    {
        var metrics = new ThreadMetrics();
        metrics.Record(1.0);
        metrics.Record(2.0);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.PercentileMilliseconds(-1.0), Is.EqualTo(1.0));
            Assert.That(metrics.PercentileMilliseconds(5.0), Is.EqualTo(2.0));
        });
    }

    [Test]
    public void Sample_count_is_capped_by_the_window_but_the_total_keeps_growing()
    {
        var metrics = new ThreadMetrics();
        const int recorded = ThreadMetrics.WindowSize * 3;

        for (var i = 0; i < recorded; i++)
            metrics.Record(1.0);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.Samples, Is.EqualTo(recorded));

            // The window itself never exceeds its capacity, which is what keeps this type allocation-free.
            Assert.That(metrics.Snapshot(), Has.Length.EqualTo(ThreadMetrics.WindowSize));
        });
    }

    [Test]
    public void Non_finite_and_negative_samples_are_ignored()
    {
        var metrics = new ThreadMetrics();

        // A clock that steps backwards, or a division that produced NaN, must not poison the statistics:
        // one NaN would make every subsequent mean NaN as well.
        metrics.Record(double.NaN);
        metrics.Record(double.PositiveInfinity);
        metrics.Record(-1.0);
        metrics.Record(3.0);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.Samples, Is.EqualTo(1));
            Assert.That(metrics.LastMilliseconds, Is.EqualTo(3.0));
            Assert.That(metrics.MeanMilliseconds, Is.EqualTo(3.0));
            Assert.That(metrics.MaxMilliseconds, Is.EqualTo(3.0));
        });
    }

    [Test]
    public void RecordSince_measures_a_real_interval()
    {
        var metrics = new ThreadMetrics();
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        Thread.Sleep(20);
        metrics.RecordSince(startedAt);

        // Generous bounds: this asserts that the call measures elapsed time at all, not how accurate the
        // machine's clock is. A sleep of 20 ms cannot complete in under 10.
        Assert.That(metrics.LastMilliseconds, Is.GreaterThanOrEqualTo(10.0));
    }

    [Test]
    public void Describe_includes_every_figure_the_panel_shows()
    {
        var metrics = new ThreadMetrics();
        metrics.Record(1.25);

        var text = metrics.Describe("sim.tick");

        Assert.That(
            text,
            Does.Contain("sim.tick").And.Contain("last").And.Contain("mean").And.Contain("p99").And.Contain("max")
        );
    }

    [Test]
    public void Concurrent_records_do_not_corrupt_the_statistics()
    {
        var metrics = new ThreadMetrics();
        const int writers = 4;
        const int perWriter = 1000;

        var threads = new Thread[writers];
        for (var w = 0; w < writers; w++)
        {
            threads[w] = new Thread(() =>
            {
                for (var i = 0; i < perWriter; i++)
                    metrics.Record(1.0);
            });
        }

        foreach (var thread in threads)
            thread.Start();

        foreach (var thread in threads)
            thread.Join();

        // The type documents single-writer use, but a torn counter here would still be a silent wrong
        // number, so the totals are asserted to be exact rather than merely plausible.
        Assert.Multiple(() =>
        {
            Assert.That(metrics.Samples, Is.EqualTo(writers * perWriter));
            Assert.That(metrics.MeanMilliseconds, Is.EqualTo(1.0).Within(0.001));
            Assert.That(metrics.MaxMilliseconds, Is.EqualTo(1.0));
        });
    }
}
