using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// Tests for the run-length meshlet arena.
/// </summary>
/// <remarks>
/// The properties worth pinning are the ones a bump allocator would not have: that runs come back
/// contiguously, that a release coalesces with both neighbours, and that the free list never overlaps itself.
/// A double-free or an overlap corrupts the pools in a way that shows up much later as flickering geometry,
/// so the consistency invariant is checked after every operation rather than only at the end.
/// </remarks>
[TestFixture]
public sealed class MeshletArenaTests
{
    [Test]
    public void A_fresh_arena_is_one_run_covering_everything()
    {
        var arena = new MeshletArena(64);

        Assert.Multiple(
            () =>
            {
                Assert.That(arena.Capacity, Is.EqualTo(64));
                Assert.That(arena.FreeCount, Is.EqualTo(64));
                Assert.That(arena.LiveCount, Is.EqualTo(0));
                Assert.That(arena.FreeRunCount, Is.EqualTo(1));
                Assert.That(arena.LargestFreeRun, Is.EqualTo(64));
                Assert.That(arena.IsConsistent(), Is.True);
            }
        );
    }

    [Test]
    public void Allocations_are_contiguous_and_disjoint()
    {
        var arena = new MeshletArena(64);

        var first = arena.Allocate(4);
        var second = arena.Allocate(4);
        var third = arena.Allocate(4);

        Assert.Multiple(
            () =>
            {
                Assert.That(first, Is.EqualTo(0));
                Assert.That(second, Is.EqualTo(4));
                Assert.That(third, Is.EqualTo(8));
                Assert.That(arena.LiveCount, Is.EqualTo(12));
                Assert.That(arena.FreeCount, Is.EqualTo(52));
                Assert.That(arena.IsConsistent(), Is.True);
            }
        );
    }

    [Test]
    public void A_released_run_is_reallocated_at_the_same_offset()
    {
        var arena = new MeshletArena(64);
        var first = arena.Allocate(8);
        var second = arena.Allocate(8);

        arena.Release(first, 8);

        var reallocated = arena.Allocate(8);

        Assert.Multiple(
            () =>
            {
                Assert.That(reallocated, Is.EqualTo(first));
                Assert.That(reallocated, Is.Not.EqualTo(second));
                Assert.That(arena.IsConsistent(), Is.True);
            }
        );
    }

    [Test]
    public void Releasing_a_run_coalesces_with_both_neighbours()
    {
        var arena = new MeshletArena(64);

        var first = arena.Allocate(8);
        var middle = arena.Allocate(8);
        var last = arena.Allocate(8);

        arena.Release(first, 8);
        arena.Release(last, 8);

        // Two separate runs, because the allocated middle block splits them.
        Assert.That(arena.FreeRunCount, Is.EqualTo(2));

        arena.Release(middle, 8);

        // All three are now one run again, which is the property that keeps fragmentation from accumulating
        // over a long session of streaming bricks in and out.
        Assert.Multiple(
            () =>
            {
                Assert.That(arena.FreeRunCount, Is.EqualTo(1));
                Assert.That(arena.FreeCount, Is.EqualTo(64));
                Assert.That(arena.LargestFreeRun, Is.EqualTo(64));
                Assert.That(arena.IsConsistent(), Is.True);
            }
        );
    }

    [Test]
    public void Allocation_refuses_when_no_run_is_large_enough_and_reports_it()
    {
        var arena = new MeshletArena(16);

        Assert.That(arena.Allocate(8), Is.EqualTo(0));
        Assert.That(arena.Allocate(8), Is.EqualTo(8));

        Assert.Multiple(
            () =>
            {
                Assert.That(arena.Allocate(1), Is.EqualTo(-1));
                Assert.That(arena.FailureCount, Is.EqualTo(1));
                Assert.That(arena.AllocationCount, Is.EqualTo(2));
                Assert.That(arena.IsConsistent(), Is.True);
            }
        );
    }

    [Test]
    public void Fragmented_allocations_leave_no_overlap()
    {
        var arena = new MeshletArena(32);
        var live = new List<(int Offset, int Length)>();

        // Alternate allocate and release so the free list is forced through many coalesce paths. Every
        // live run is tracked so the final layout can be checked for overlap rather than only counted.
        for (var size = 1; size <= 4; size++)
        {
            for (var i = 0; i < 4; i++)
            {
                var offset = arena.Allocate(size);
                if (offset < 0)
                    break;

                live.Add((offset, size));
            }
        }

        for (var i = 0; i < live.Count; i += 2)
            arena.Release(live[i].Offset, live[i].Length);

        Assert.That(arena.IsConsistent(), Is.True);

        // Every remaining live run must be disjoint from every other, which is what a double-free or a bad
        // coalesce would break.
        var remaining = live.Where((_, index) => index % 2 == 1).ToArray();

        for (var i = 0; i < remaining.Length; i++)
        {
            for (var j = i + 1; j < remaining.Length; j++)
            {
                var a = remaining[i];
                var b = remaining[j];

                Assert.That(
                    a.Offset + a.Length <= b.Offset || b.Offset + b.Length <= a.Offset,
                    Is.True,
                    $"runs [{a.Offset},{a.Offset + a.Length}) and [{b.Offset},{b.Offset + b.Length}) overlap"
                );
            }
        }
    }

    [Test]
    public void Clear_returns_every_slot()
    {
        var arena = new MeshletArena(32);

        arena.Allocate(8);
        arena.Allocate(8);
        arena.Clear();

        Assert.Multiple(
            () =>
            {
                Assert.That(arena.FreeCount, Is.EqualTo(32));
                Assert.That(arena.LiveCount, Is.EqualTo(0));
                Assert.That(arena.FreeRunCount, Is.EqualTo(1));
                Assert.That(arena.IsConsistent(), Is.True);
            }
        );
    }

    [Test]
    public void A_release_outside_the_arena_is_ignored_rather_than_fatal()
    {
        var arena = new MeshletArena(16);

        arena.Release(-1, 4);
        arena.Release(0, 100);
        arena.Release(12, 8);

        Assert.Multiple(
            () =>
            {
                Assert.That(arena.FreeCount, Is.EqualTo(16));
                Assert.That(arena.IsConsistent(), Is.True);
            }
        );
    }

    [Test]
    public void A_zero_or_negative_allocation_is_rejected()
    {
        var arena = new MeshletArena(16);

        Assert.Multiple(
            () =>
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => arena.Allocate(0));
                Assert.Throws<ArgumentOutOfRangeException>(() => arena.Allocate(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => new MeshletArena(0));
            }
        );
    }

    [Test]
    public void Largest_free_run_tracks_the_fragmentation()
    {
        var arena = new MeshletArena(16);

        arena.Allocate(4);
        arena.Allocate(4);
        arena.Allocate(4);

        // Three four-slot runs have been taken, so the remaining contiguous space is four slots. A brick
        // needing more than that cannot be allocated, which is exactly what the largest-run figure reports.
        Assert.That(arena.LargestFreeRun, Is.EqualTo(4));
        Assert.That(arena.Allocate(8), Is.EqualTo(-1));
    }
}
