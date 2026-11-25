using Brutal.Numerics;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// Tests over the clipmap's level geometry, slot addressing and re-anchoring rule.
/// </summary>
[TestFixture]
public sealed class SdfClipmapTests
{
    private static readonly WorldSettings Settings = new()
    {
        LevelCount = 4,
        BricksPerLevelAxis = 4,
        BrickVoxelCount = 8,
        BaseBrickSize = 8.0f,
    };

    [Test]
    public void Levels_double_in_brick_size_and_extent()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);

        for (var level = 1; level < clipmap.LevelCount; level++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(clipmap.Level(level).BrickSize, Is.EqualTo(clipmap.Level(level - 1).BrickSize * 2.0f));
                Assert.That(clipmap.Level(level).VoxelSize, Is.EqualTo(clipmap.Level(level - 1).VoxelSize * 2.0f));
                Assert.That(clipmap.Level(level).Extent, Is.EqualTo(clipmap.Level(level - 1).Extent * 2.0f));
            });
        }
    }

    [Test]
    public void Levels_nest_around_the_centre()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);

        for (var level = 1; level < clipmap.LevelCount; level++)
        {
            Assert.Multiple(() =>
            {
                Assert.That(clipmap.InnerRadius(level), Is.EqualTo(clipmap.Level(level - 1).HalfExtent));
                Assert.That(clipmap.InnerRadius(level), Is.LessThan(clipmap.OuterRadius(level)));
                Assert.That(clipmap.OuterRadius(level), Is.GreaterThan(clipmap.OuterRadius(level - 1)));
            });
        }
    }

    [Test]
    public void SelectLevel_picks_the_finest_level_covering_the_point()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);

        Assert.That(clipmap.SelectLevel(float3.Zero), Is.EqualTo(0));

        // Just outside the finest cube but inside the second, so the second level owns it.
        var justOutsideFinest = clipmap.Level(0).HalfExtent + 1.0f;

        Assert.That(clipmap.SelectLevel(new float3(justOutsideFinest, 0.0f, 0.0f)), Is.EqualTo(1));
    }

    [Test]
    public void SelectLevel_falls_back_to_the_coarsest_level_beyond_the_view_distance()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var far = new float3(clipmap.Settings.ViewDistance * 10.0f, 0.0f, 0.0f);

        Assert.That(clipmap.SelectLevel(far), Is.EqualTo(clipmap.LevelCount - 1));
    }

    [Test]
    public void Recenter_reports_nothing_while_the_centre_stays_within_a_brick()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);

        // A tenth of the finest brick, so no level's anchor can cross a boundary.
        var moved = clipmap.Recenter(new float3(Settings.BaseBrickSize * 0.1f, 0.0f, 0.0f)).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(moved.Length, Is.EqualTo(0));
            Assert.That(clipmap.RecenterCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void Recenter_reports_the_finest_level_first_when_it_crosses_a_boundary()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);

        // A whole brick, so the finest level must re-anchor. Coarser levels may or may not, depending on
        // where the boundary falls, which is why only the finest level is asserted.
        var moved = clipmap.Recenter(new float3(Settings.BaseBrickSize, 0.0f, 0.0f)).ToArray();

        Assert.That(moved, Is.Not.Empty);
        Assert.That(moved[0], Is.EqualTo(0));
        Assert.That(moved, Is.Ordered);
    }

    [Test]
    public void Recenter_is_idempotent_for_the_same_centre()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var center = new float3(100.0f, 0.0f, 100.0f);

        var first = clipmap.Recenter(center).ToArray();
        var second = clipmap.Recenter(center).ToArray();

        Assert.That(first, Is.Not.Empty);
        Assert.That(second, Is.Empty);
    }

    [Test]
    public void SlotIndex_is_stable_across_a_recentre_that_does_not_move_the_anchor()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var key = clipmap.Level(0).BrickKeyAt(new float3(4.0f, 4.0f, 4.0f));
        var before = clipmap.Level(0).SlotIndex(key);

        clipmap.Recenter(new float3(Settings.BaseBrickSize * 0.1f, 0.0f, 0.0f));

        Assert.That(clipmap.Level(0).SlotIndex(key), Is.EqualTo(before));
    }

    [Test]
    public void SlotIndex_wraps_a_full_grid_period_back_onto_itself()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var level = clipmap.Level(0);
        var lattice = new int3(1, 2, 3);

        var wrapped = level.SlotIndex(new int3(
            lattice.X + Settings.BricksPerLevelAxis,
            lattice.Y + Settings.BricksPerLevelAxis,
            lattice.Z + Settings.BricksPerLevelAxis
        ));

        Assert.That(wrapped, Is.EqualTo(level.SlotIndex(lattice)));
    }

    [Test]
    public void SlotBrickKey_inverts_SlotIndex()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var level = clipmap.Level(1);

        for (var slot = 0; slot < Settings.BricksPerLevel; slot++)
            Assert.That(level.SlotIndex(level.SlotBrickKey(slot)), Is.EqualTo(slot), $"slot {slot}");
    }

    [Test]
    public void BrickKeys_stay_exact_far_from_the_origin()
    {
        // A world position where a float multiply would lose the low bits of the lattice coordinate.
        var clipmap = new SdfClipmap(Settings, new float3(500_000.0f, 0.0f, 500_000.0f));
        var level = clipmap.Level(0);

        var key = level.BrickKeyAt(new float3(500_000.0f, 0.0f, 500_000.0f));
        var origin = level.BrickOrigin(key);

        Assert.That(MathF.Abs(origin.X - 500_000.0f), Is.LessThan(level.BrickSize));
    }

    [Test]
    public void Settings_validation_rejects_a_non_power_of_two_brick_count()
    {
        var settings = Settings with { BricksPerLevelAxis = 3 };

        Assert.That(settings.Validate, Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void A_bricks_voxels_tile_it_exactly_at_every_level()
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);

        // The property that makes the world a surface instead of a cloud of islands: bricks are placed on a
        // lattice spaced by BrickSize, so the voxel grid inside a brick has to span exactly that distance.
        // Any shortfall is a gap between every pair of neighbours.
        for (var level = 0; level < clipmap.LevelCount; level++)
        {
            Assert.That(
                clipmap.Level(level).VoxelSize * Settings.BrickVoxelCount,
                Is.EqualTo(clipmap.Level(level).BrickSize).Within(1e-3f),
                $"level {level}'s voxels do not fill the brick, so its bricks cannot meet"
            );
        }
    }

    [Test]
    public void Settings_validation_rejects_a_single_level()
    {
        var settings = Settings with { LevelCount = 1 };

        Assert.That(settings.Validate, Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
