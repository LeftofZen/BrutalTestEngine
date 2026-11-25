using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// Tests over the brick indirection table.
/// </summary>
[TestFixture]
public sealed class SdfBrickPoolTests
{
    [Test]
    public void A_fresh_pool_has_every_slot_empty()
    {
        var pool = new SdfBrickPool(64);

        Assert.Multiple(() =>
        {
            Assert.That(pool.Capacity, Is.EqualTo(64));
            Assert.That(pool.AllocatedCount, Is.EqualTo(0));
            Assert.That(pool.IsFull, Is.False);
        });

        for (var slot = 0; slot < pool.Capacity; slot++)
            Assert.That(pool.IdOf(slot), Is.EqualTo(SdfBrickPool.Empty));

        // The sentinel matters because the table is uploaded to the GPU, where an unallocated slot has to be
        // distinguishable from the valid id 0.
        Assert.That(SdfBrickPool.Empty, Is.Negative);
    }

    [Test]
    public void Allocate_returns_the_same_id_for_the_same_slot()
    {
        var pool = new SdfBrickPool(16);

        var first = pool.Allocate(3);
        var second = pool.Allocate(3);

        Assert.Multiple(() =>
        {
            Assert.That(pool.IdOf(3), Is.EqualTo(first));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(pool.AllocatedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Ids_are_unique_across_slots()
    {
        var pool = new SdfBrickPool(32);
        var seen = new HashSet<int>();

        for (var slot = 0; slot < 8; slot++)
            Assert.That(seen.Add(pool.Allocate(slot)), Is.True, $"slot {slot} reused an id");
    }

    [Test]
    public void Allocate_reports_full_rather_than_throwing()
    {
        var pool = new SdfBrickPool(2);
        pool.Allocate(0);
        pool.Allocate(1);

        Assert.Multiple(() =>
        {
            Assert.That(pool.IsFull, Is.True);
            Assert.That(pool.Allocate(0), Is.EqualTo(0));
        });
    }

    [Test]
    public void Release_returns_the_freed_id_and_makes_it_reusable()
    {
        var pool = new SdfBrickPool(1);
        var id = pool.Allocate(0);

        var released = pool.Release(0);

        Assert.Multiple(() =>
        {
            Assert.That(released, Is.EqualTo(id));
            Assert.That(pool.IdOf(0), Is.EqualTo(SdfBrickPool.Empty));
            Assert.That(pool.SlotOf(id), Is.EqualTo(SdfBrickPool.Empty));
            Assert.That(pool.AllocatedCount, Is.EqualTo(0));
            Assert.That(pool.Allocate(0), Is.EqualTo(id));
        });
    }

    [Test]
    public void Release_of_an_empty_slot_changes_nothing()
    {
        var pool = new SdfBrickPool(4);

        Assert.Multiple(() =>
        {
            Assert.That(pool.Release(2), Is.EqualTo(SdfBrickPool.Empty));
            Assert.That(pool.AllocatedCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void Clear_returns_every_id_to_the_free_list()
    {
        var pool = new SdfBrickPool(8);

        for (var slot = 0; slot < 8; slot++)
            pool.Allocate(slot);

        Assert.That(pool.IsFull, Is.True);

        pool.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(pool.AllocatedCount, Is.EqualTo(0));
            Assert.That(pool.IsFull, Is.False);
        });

        for (var slot = 0; slot < 8; slot++)
            Assert.That(pool.Allocate(slot), Is.Not.EqualTo(SdfBrickPool.Empty), $"slot {slot}");
    }

    [Test]
    public void SnapshotTable_matches_the_slot_to_id_mapping()
    {
        var pool = new SdfBrickPool(5);

        var id = pool.Allocate(2);
        var table = new int[5];

        var written = pool.SnapshotTable(table);

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(5));
            Assert.That(table[2], Is.EqualTo(id));
            Assert.That(table[0], Is.EqualTo(SdfBrickPool.Empty));
        });
    }

    [Test]
    public void SnapshotTable_writes_only_what_fits_in_the_destination()
    {
        var pool = new SdfBrickPool(16);
        var table = new int[4];

        Assert.That(pool.SnapshotTable(table), Is.EqualTo(4));
    }

    [Test]
    public void Out_of_range_slots_and_ids_are_rejected()
    {
        var pool = new SdfBrickPool(4);

        Assert.Multiple(() =>
        {
            Assert.That(() => pool.IdOf(4), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => pool.IdOf(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => pool.Allocate(4), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => pool.SlotOf(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }
}
