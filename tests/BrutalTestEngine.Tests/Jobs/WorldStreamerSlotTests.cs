using Brutal.Numerics;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Jobs;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.Jobs;

/// <summary>
/// Tests that a streamer pass fills the whole hierarchy rather than one level of it.
/// </summary>
/// <remarks>
/// <para>
/// The clipmap's slot table is indexed globally — level 0's slots first, then level 1's, and so on — because
/// the pool has one entry per slot of every level and hands out ids from that table. A streamer that passes
/// a per-level slot index instead of a global one makes every level address the same first
/// <c>BricksPerLevel</c> entries, so the hierarchy can never hold more than a single level's worth of
/// geometry no matter how many bricks it asks for.
/// </para>
/// <para>
/// That bug is invisible in every single-level test and in every counter except one: bricks keep being
/// requested, keep completing, and never accumulate. The world then renders as scattered fragments, which
/// looks exactly like a meshing fault.
/// </para>
/// </remarks>
[TestFixture]
public sealed class WorldStreamerSlotTests
{
    private static readonly WorldSettings Settings = new()
    {
        LevelCount = 4,
        BricksPerLevelAxis = 4,
        BrickVoxelCount = 8,
        BaseBrickSize = 8.0f,
        Seed = 20260401,
    };

    private static readonly WorldJobSystemOptions Options = new() { WorkerCount = 1 };

    [Test]
    public void A_pass_can_request_bricks_on_more_than_one_level()
    {
        var timers = new ThreadTimerRegistry();

        using var jobs = new WorldJobSystem(Settings, Options, timers);
        var streamer = new WorldStreamer(jobs, timers);
        var clipmap = new SdfClipmap(Settings, float3.Zero, Options.MaxResidentBricks);
        var field = TerrainField.ForSettings(Settings);

        streamer.Update(float3.Zero, clipmap, field);

        // Slots are handed out across the whole hierarchy, so the highest slot in use must be able to exceed
        // a single level's capacity. If it never does, the levels are aliasing onto level 0's table entries.
        var highestSlot = HighestAllocatedSlot(clipmap.Pool);

        Assert.That(
            highestSlot,
            Is.GreaterThanOrEqualTo(Settings.BricksPerLevel),
            "every allocated slot fell inside level 0's range, so the levels are sharing one slot table"
        );
    }

    [Test]
    public void Each_allocated_slot_belongs_to_the_level_its_brick_was_requested_for()
    {
        var timers = new ThreadTimerRegistry();

        using var jobs = new WorldJobSystem(Settings, Options, timers);
        var streamer = new WorldStreamer(jobs, timers);
        var clipmap = new SdfClipmap(Settings, float3.Zero, Options.MaxResidentBricks);
        var field = TerrainField.ForSettings(Settings);

        streamer.Update(float3.Zero, clipmap, field);

        var allocated = AllocatedSlots(clipmap.Pool);

        Assert.That(allocated, Is.Not.Empty, "the pass allocated no bricks at all");

        Assert.Multiple(() =>
        {
            foreach (var (slot, id) in allocated)
            {
                var record = jobs.Bricks[id];
                var expectedLevel = slot / Settings.BricksPerLevel;

                Assert.That(
                    record.Key.Level,
                    Is.EqualTo(expectedLevel),
                    $"slot {slot} belongs to level {expectedLevel} but holds a brick from level {record.Key.Level}"
                );
            }
        });
    }

    private static int HighestAllocatedSlot(SdfBrickPool pool)
    {
        var highest = -1;

        foreach (var (slot, _) in AllocatedSlots(pool))
            highest = Math.Max(highest, slot);

        return highest;
    }

    private static List<(int Slot, int Id)> AllocatedSlots(SdfBrickPool pool)
    {
        var slots = new List<(int, int)>();

        for (var slot = 0; slot < pool.Capacity; slot++)
        {
            var id = pool.IdOf(slot);

            if (id != SdfBrickPool.Empty)
                slots.Add((slot, id));
        }

        return slots;
    }
}
