using System.Diagnostics;

namespace BrutalTestEngine.Engine;

/// <summary>
/// A rolling summary of how long one thread's unit of work took, in milliseconds.
/// </summary>
/// <remarks>
/// <para>
/// Every thread in the engine already counts what it did; this records what it cost. The distinction
/// matters because a thread that keeps up can still be the one holding a frame together: 60 ticks a
/// second is healthy until each tick takes 15 ms, at which point the next stall is a mystery. Mean
/// alone would hide that, because one 200 ms outlier and a hundred ordinary ticks average out to
/// something reassuring.
/// </para>
/// <para>
/// The window is bounded and samples are folded in as they arrive, so recording a duration is a few
/// arithmetic operations and a fixed-size array write — cheap enough to sit inside a tick loop. Times
/// are kept as <see cref="double"/> milliseconds rather than raw timestamps because the numbers are
/// read by a human in the debug panel, not re-derived.
/// </para>
/// </remarks>
public sealed class ThreadMetrics
{
    /// <summary>Number of samples kept for the mean, maximum and percentile estimates.</summary>
    public const int WindowSize = 256;

    private readonly double[] _window = new double[WindowSize];
    private readonly Lock _gate = new();
    private int _next;
    private int _count;
    private long _samples;
    private double _windowSum;
    private double _windowMax;
    private double _totalMilliseconds;
    private double _lastMilliseconds;
    private double _maxMilliseconds;

    /// <summary>
    /// Records one completed unit of work. Safe to call from the owning thread only, which is the same
    /// discipline every other piece of per-thread state in the engine follows.
    /// </summary>
    public void Record(double elapsedMilliseconds)
    {
            // Reject anything that is not a real, non-negative duration. Infinity has to be excluded
            // explicitly, not just NaN: one infinite sample would make the mean and the maximum infinite for
            // the rest of the session, and the panel would show "∞ ms" with no way to recover but a restart.
            if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
            return;

        lock (_gate)
        {
            _lastMilliseconds = elapsedMilliseconds;
            _samples++;
            _totalMilliseconds += elapsedMilliseconds;

            if (elapsedMilliseconds > _maxMilliseconds)
                _maxMilliseconds = elapsedMilliseconds;

            // The window rolls, so the mean describes recent behaviour rather than the whole session.
            // A thread that was slow at startup and is now healthy should read as healthy.
            if (_count == WindowSize)
                _windowSum -= _window[_next];
            else
                _count++;

            _window[_next] = elapsedMilliseconds;
            _windowSum += elapsedMilliseconds;
            _next = (_next + 1) % WindowSize;

            if (elapsedMilliseconds > _windowMax)
                _windowMax = elapsedMilliseconds;
        }
    }

    /// <summary>Records the time elapsed since a <see cref="Stopwatch"/> timestamp.</summary>
    public void RecordSince(long startTimestamp) =>
        Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

    /// <summary>Units of work recorded since the process started.</summary>
    public long Samples
    {
        get
        {
            lock (_gate)
                return _samples;
        }
    }

    /// <summary>The most recent duration, in milliseconds.</summary>
    public double LastMilliseconds
    {
        get
        {
            lock (_gate)
                return _lastMilliseconds;
        }
    }

    /// <summary>Mean duration over the rolling window, in milliseconds.</summary>
    public double MeanMilliseconds
    {
        get
        {
            lock (_gate)
                return _count == 0 ? 0 : _windowSum / _count;
        }
    }

    /// <summary>Slowest duration ever recorded, in milliseconds. A session-wide high-water mark.</summary>
    public double MaxMilliseconds
    {
        get
        {
            lock (_gate)
                return _maxMilliseconds;
        }
    }

    /// <summary>
    /// Mean duration across every sample rather than the rolling window. Comparing this with
    /// <see cref="MeanMilliseconds"/> is how a regression shows up as "it used to be fine".
    /// </summary>
    public double LifetimeMeanMilliseconds
    {
        get
        {
            lock (_gate)
                return _samples == 0 ? 0 : _totalMilliseconds / _samples;
        }
    }

    /// <summary>
    /// An estimated percentile over the rolling window, in milliseconds.
    /// </summary>
    /// <remarks>
    /// The window is copied and sorted on demand rather than kept sorted, because this is called at
    /// most once per frame from the debug UI and sorting 256 doubles is trivial next to an ImGui frame.
    /// The p99 is the number that matters for "does this thread cause hitches": a mean of 1 ms with a
    /// p99 of 30 ms is a thread that stutters once a second, and the mean would never show it.
    /// </remarks>
    public double PercentileMilliseconds(double percentile)
    {
        var sorted = Snapshot();
        if (sorted.Length == 0)
            return 0;

        var rank = Math.Clamp(percentile, 0, 1) * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);

        return low == high ? sorted[low] : sorted[low] + ((sorted[high] - sorted[low]) * (rank - low));
    }

    /// <summary>Copies the rolling window, sorted ascending, for a caller that wants to plot it.</summary>
    public double[] Snapshot()
    {
        double[] copy;
        int count;

        lock (_gate)
        {
            count = _count;
            copy = new double[count];
            Array.Copy(_window, copy, count);
        }

        Array.Sort(copy);
        return copy;
    }

    /// <summary>One line summarising this metric, used by the debug panel and the run summary.</summary>
    public string Describe(string label) =>
        $"{label}: last {LastMilliseconds:0.000} ms, mean {MeanMilliseconds:0.000} ms, "
        + $"p99 {PercentileMilliseconds(0.99):0.000} ms, max {MaxMilliseconds:0.000} ms, {Samples} sample(s)";
}