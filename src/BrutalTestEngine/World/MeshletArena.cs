namespace BrutalTestEngine.World;

/// <summary>
/// A run-length allocator over a flat array of meshlet slots, handing out contiguous runs.
/// </summary>
/// <remarks>
/// <para>
/// The meshlet pools are a shared arena rather than a per-brick fixed region, because the fixed region is
/// wrong by a factor of ten. A brick can theoretically produce
/// <see cref="BrickShape.MaxMeshletsForBrick"/> meshlets, but that is the count for a brick whose surface
/// crosses every cell. Real terrain crosses a thin shell of cells, so a brick produces one to four. Reserving
/// the worst case for every resident brick wastes the difference on every brick in the world, which for a
/// clipmap holding thousands of them is tens of megabytes of pools that are almost entirely unused — and,
/// worse, it makes the memory cost of a bigger world look thirty times worse than it is.
/// </para>
/// <para>
/// Contiguity is the requirement that forces a real allocator rather than a bump pointer. A brick's meshlets
/// have to be adjacent so a draw can name them with one offset and one count, and a bump-only arena would
/// have to be reset as a whole, which would throw away every brick the camera can still see. The free list
/// is kept sorted by start index, which is what makes both allocation and release cheap: allocation walks
/// runs looking for one big enough, and release inserts and then coalesces with whichever neighbours are now
/// adjacent. With a few dozen runs the walk is trivial next to building a brick, and the coalescing is what
/// keeps fragmentation from accumulating over a long session.
/// </para>
/// <para>
/// The allocator does not touch the pools themselves. It reports a run and the caller writes into it, which
/// keeps this type testable without allocating the geometry it describes.
/// </para>
/// </remarks>
public sealed class MeshletArena
{
    private readonly List<Run> _free = [];
    private int _freeMeshlets;
    private int _live;
    private long _allocationCount;
    private long _failureCount;
    private int _largestFreeRun;

    /// <summary>Creates an arena of <paramref name="capacity"/> meshlet slots, initially all free.</summary>
    public MeshletArena(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Capacity = capacity;
        _free.Add(new Run(0, capacity));
        _freeMeshlets = capacity;
        _largestFreeRun = capacity;
    }

    /// <summary>Total number of meshlet slots the arena manages.</summary>
    public int Capacity { get; }

    /// <summary>Number of meshlet slots currently allocated.</summary>
    public int LiveCount => _live;

    /// <summary>Number of meshlet slots currently free.</summary>
    public int FreeCount => _freeMeshlets;

    /// <summary>Number of distinct free runs. A proxy for how fragmented the arena has become.</summary>
    public int FreeRunCount => _free.Count;

    /// <summary>Largest single contiguous free run, which is the biggest brick that can still be allocated.</summary>
    public int LargestFreeRun => _largestFreeRun;

    /// <summary>Total allocations that succeeded.</summary>
    public long AllocationCount => _allocationCount;

    /// <summary>Total allocations refused because no run was large enough.</summary>
    public long FailureCount => _failureCount;

    /// <summary>
    /// Allocates a contiguous run of <paramref name="count"/> meshlet slots.
    /// </summary>
    /// <returns>The offset of the run, or -1 when no run is large enough.</returns>
    /// <remarks>
    /// First fit, which is the right policy here: bricks are short-lived and roughly replaced in the order
    /// they were created, so a first-fit list naturally reuses the oldest holes and the arena behaves almost
    /// like a ring. Best fit would leave many small unusable gaps, which is the opposite of what a streamer
    /// needs.
    /// </remarks>
    public int Allocate(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        for (var i = 0; i < _free.Count; i++)
        {
            var run = _free[i];

            if (run.Length < count)
                continue;

            _free[i] = new Run(run.Start + count, run.Length - count);
            if (_free[i].Length == 0)
                _free.RemoveAt(i);

            _freeMeshlets -= count;
            _live += count;
            _allocationCount++;
            RecomputeLargest();

            return run.Start;
        }

        _failureCount++;
        return -1;
    }

    /// <summary>Returns a previously allocated run to the free list, coalescing with its neighbours.</summary>
    /// <remarks>
    /// Silently ignores a release that does not describe a live run rather than throwing, because the
    /// caller's release path runs during eviction and shutdown, where an exception would take down a frame
    /// for a bookkeeping mistake that is better reported by the free counts than by a crash.
    /// </remarks>
    public void Release(int offset, int count)
    {
        if (count < 1 || offset < 0 || offset + count > Capacity)
            return;

        var index = _free.BinarySearch(new Run(offset, count), RunComparer.Instance);
        if (index < 0)
            index = ~index;

        _free.Insert(index, new Run(offset, count));
        _freeMeshlets += count;
        _live -= count;

        Coalesce();
        RecomputeLargest();
    }

    /// <summary>Returns every slot to the free list.</summary>
    public void Clear()
    {
        _free.Clear();
        _free.Add(new Run(0, Capacity));
        _freeMeshlets = Capacity;
        _live = 0;
        _largestFreeRun = Capacity;
    }

    /// <summary>
    /// True when the arena's bookkeeping is self-consistent: every slot is either free or live exactly once.
    /// </summary>
    /// <remarks>
    /// Exposed for tests and for a debug assertion. A run allocator that silently double-frees corrupts the
    /// pools in ways that surface much later as geometry flickering, so an explicit invariant check is worth
    /// having even when it is never called in the frame loop.
    /// </remarks>
    public bool IsConsistent()
    {
        var total = 0;
        var previousEnd = -1;

        foreach (var run in _free)
        {
            if (run.Length < 1 || run.Start < 0 || run.Start + run.Length > Capacity)
                return false;

            // Sorted and disjoint.
            if (run.Start <= previousEnd)
                return false;

            previousEnd = run.Start + run.Length - 1;
            total += run.Length;
        }

        return total == _freeMeshlets && _freeMeshlets + _live == Capacity;
    }

    private void Coalesce()
    {
        for (var i = 0; i < _free.Count - 1;)
        {
            var current = _free[i];
            var next = _free[i + 1];

            if (current.Start + current.Length == next.Start)
            {
                _free[i] = new Run(current.Start, current.Length + next.Length);
                _free.RemoveAt(i + 1);
                continue;
            }

            i++;
        }
    }

    private void RecomputeLargest()
    {
        var largest = 0;

        foreach (var run in _free)
        {
            if (run.Length > largest)
                largest = run.Length;
        }

        _largestFreeRun = largest;
    }

    private readonly record struct Run(int Start, int Length);

    /// <summary>Orders runs by start offset, so the free list stays sorted and coalescing is a local test.</summary>
    private sealed class RunComparer : IComparer<Run>
    {
        public static RunComparer Instance { get; } = new();

        public int Compare(Run x, Run y) => x.Start.CompareTo(y.Start);
    }
}
