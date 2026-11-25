using Brutal.Numerics;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// Tests over dirty-brick tracking and the edit overlap query.
/// </summary>
[TestFixture]
public sealed class DirtyBrickSetTests
{
    private static readonly WorldSettings Settings = new()
    {
        LevelCount = 3,
        BricksPerLevelAxis = 4,
        BrickVoxelCount = 8,
        BaseBrickSize = 8.0f,
    };

    [Test]
    public void A_fresh_set_is_empty()
    {
        var set = new DirtyBrickSet();

        Assert.Multiple(() =>
        {
            Assert.That(set.IsEmpty, Is.True);
            Assert.That(set.Count, Is.EqualTo(0));
            Assert.That(set.Bricks.Length, Is.EqualTo(0));
        });
    }

    [Test]
    public void Marking_the_same_brick_twice_marks_it_once()
    {
        var set = new DirtyBrickSet();
        var brick = new BrickKey(0, new int3(1, 2, 3));

        Assert.Multiple(() =>
        {
            Assert.That(set.Mark(brick), Is.True);
            Assert.That(set.Mark(brick), Is.False);
            Assert.That(set.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void Bricks_come_back_in_mark_order()
    {
        var set = new DirtyBrickSet();

        set.Mark(new BrickKey(0, new int3(3, 0, 0)));
        set.Mark(new BrickKey(0, new int3(1, 0, 0)));
        set.Mark(new BrickKey(0, new int3(2, 0, 0)));

        var bricks = set.Bricks.ToArray();

        Assert.That(bricks.Select(b => b.Lattice.X), Is.EqualTo(new[] { 3, 1, 2 }));
    }

    [Test]
    public void Clear_of_one_brick_leaves_the_others()
    {
        var set = new DirtyBrickSet();
        var first = new BrickKey(0, new int3(1, 0, 0));
        var second = new BrickKey(0, new int3(2, 0, 0));

        set.Mark(first);
        set.Mark(second);

        Assert.Multiple(() =>
        {
            Assert.That(set.Clear(first), Is.True);
            Assert.That(set.Clear(first), Is.False);
            Assert.That(set.Contains(first), Is.False);
            Assert.That(set.Contains(second), Is.True);
            Assert.That(set.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void MarkSphere_marks_every_brick_the_volume_overlaps()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var set = new DirtyBrickSet();

        // A radius spanning more than one brick at the finest level, centred on a brick corner so the
        // overlap reaches into all eight quadrants.
        var radius = Settings.BaseBrickSize * 1.5f;
        var marked = set.MarkSphere(clipmap, float3.Zero, radius, level: 0);

        // The query covers the inclusive lattice range from floor(-radius / brick) to floor(+radius / brick),
        // so the count per axis is decided by both ends rather than by the radius alone.
        var low = (int)MathF.Floor(-radius / Settings.BaseBrickSize);
        var high = (int)MathF.Floor(radius / Settings.BaseBrickSize);
        var perAxis = high - low + 1;

        Assert.That(marked, Is.EqualTo(perAxis * perAxis * perAxis));
        Assert.That(set.Count, Is.EqualTo(marked));
    }

    [Test]
    public void MarkSphere_is_idempotent_across_overlapping_edits()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var set = new DirtyBrickSet();

        var first = set.MarkSphere(clipmap, float3.Zero, 12.0f, level: 0);
        var second = set.MarkSphere(clipmap, new float3(2.0f, 2.0f, 2.0f), 12.0f, level: 0);

        Assert.That(first, Is.GreaterThan(0));
        Assert.That(second, Is.EqualTo(0), "a fully overlapping edit should add nothing new");
        Assert.That(set.Count, Is.EqualTo(first));
    }

    [Test]
    public void MarkAt_ignores_a_brick_this_level_does_not_own()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var set = new DirtyBrickSet();

        // A brick at the coarsest level sitting right on the centre is owned by the finest level, so marking
        // it at the coarse level must be dropped rather than queued as work that would never be drawn.
        var centerKey = clipmap.BrickAt(float3.Zero);
        var coarse = new BrickKey(clipmap.LevelCount - 1, centerKey.Lattice);

        Assert.Multiple(() =>
        {
            Assert.That(set.MarkAt(clipmap, coarse), Is.False);
            Assert.That(set.MarkAt(clipmap, centerKey), Is.True);
        });
    }

    [Test]
    public void MarkAt_ignores_a_brick_outside_the_clipmap()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var set = new DirtyBrickSet();

        var far = new BrickKey(0, new int3(1_000_000, 0, 0));

        Assert.That(set.MarkAt(clipmap, far), Is.False);
    }

    [Test]
    public void Snapshot_writes_the_dirty_bricks_and_reports_the_count()
    {
        var set = new DirtyBrickSet();
        set.Mark(new BrickKey(0, new int3(1, 0, 0)));
        set.Mark(new BrickKey(1, new int3(2, 0, 0)));

        var destination = new BrickKey[8];
        var written = set.Snapshot(destination);

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(2));
            Assert.That(destination[0].Lattice.X, Is.EqualTo(1));
            Assert.That(destination[1].Level, Is.EqualTo(1));
        });
    }

    [Test]
    public void Snapshot_is_bounded_by_the_destination()
    {
        var set = new DirtyBrickSet();

        for (var i = 0; i < 10; i++)
            set.Mark(new BrickKey(0, new int3(i, 0, 0)));

        Assert.That(set.Snapshot(new BrickKey[3]), Is.EqualTo(3));
    }

    [Test]
    public void Clear_empties_the_set()
    {
        var set = new DirtyBrickSet();
        set.Mark(new BrickKey(0, new int3(1, 0, 0)));

        set.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(set.IsEmpty, Is.True);
            Assert.That(set.Count, Is.EqualTo(0));
        });
    }

    [Test]
    public void A_negative_radius_is_rejected()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var set = new DirtyBrickSet();

        Assert.That(
            () => set.MarkSphere(clipmap, float3.Zero, -1.0f, level: 0),
            Throws.TypeOf<ArgumentOutOfRangeException>()
        );
    }
}
