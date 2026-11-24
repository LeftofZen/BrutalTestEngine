using BrutalTestEngine.Simulation;

namespace BrutalTestEngine.Tests.Simulation;

/// <summary>
/// Covers the data-oriented entity store: the dense layout, the swap-remove, and the flat array passes.
/// </summary>
/// <remarks>
/// These are the properties the layout exists for, so they are the ones worth asserting. A test that only
/// checked "an entity was added" would pass for an object graph too, and would not notice the store quietly
/// becoming one.
/// </remarks>
[TestFixture]
public sealed class EntityStoreTests
{
    [Test]
    public void A_new_store_is_empty()
    {
        var store = new EntityStore();

        Assert.That(store.Count, Is.Zero);
        Assert.That(store.PositionX.Length, Is.Zero);
    }

    [Test]
    public void Added_entities_are_dense_and_readable_through_the_arrays()
    {
        var store = new EntityStore();

        store.Add(1.0f, 2.0f, 3.0f, radius: 0.75f);
        store.Add(4.0f, 5.0f, 6.0f, radius: 1.25f);

        Assert.That(store.Count, Is.EqualTo(2));
        Assert.That(store.PositionX.ToArray(), Is.EqualTo(new[] { 1.0f, 4.0f }));
        Assert.That(store.PositionY.ToArray(), Is.EqualTo(new[] { 2.0f, 5.0f }));
        Assert.That(store.PositionZ.ToArray(), Is.EqualTo(new[] { 3.0f, 6.0f }));
        Assert.That(store.Radius.ToArray(), Is.EqualTo(new[] { 0.75f, 1.25f }));
    }

    [Test]
    public void The_store_grows_without_losing_entities()
    {
        var store = new EntityStore(capacity: 2);

        for (var i = 0; i < 100; i++)
            store.Add(i, 0.0f, 0.0f);

        Assert.That(store.Count, Is.EqualTo(100));
        Assert.That(store.Capacity, Is.GreaterThanOrEqualTo(100));

        for (var i = 0; i < 100; i++)
            Assert.That(store.PositionX[i], Is.EqualTo((float)i));
    }

    [Test]
    public void Removing_swaps_the_last_entity_into_the_gap_and_keeps_the_arrays_dense()
    {
        var store = new EntityStore();
        store.Add(10.0f, 0.0f, 0.0f);
        store.Add(20.0f, 0.0f, 0.0f);
        store.Add(30.0f, 0.0f, 0.0f);

        store.RemoveAt(0);

        Assert.That(store.Count, Is.EqualTo(2));
        Assert.That(store.PositionX.ToArray(), Is.EqualTo(new[] { 30.0f, 20.0f }));
    }

    [Test]
    public void Removing_the_last_entity_does_not_move_anything()
    {
        var store = new EntityStore();
        store.Add(10.0f, 0.0f, 0.0f);
        store.Add(20.0f, 0.0f, 0.0f);

        store.RemoveAt(1);

        Assert.That(store.Count, Is.EqualTo(1));
        Assert.That(store.PositionX[0], Is.EqualTo(10.0f));
    }

    [Test]
    public void Removing_out_of_range_throws()
    {
        var store = new EntityStore();
        store.Add(0.0f, 0.0f, 0.0f);

        Assert.That(() => store.RemoveAt(1), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => store.RemoveAt(-1), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Integrate_advances_position_by_velocity_and_velocity_by_gravity()
    {
        var store = new EntityStore();
        store.Add(0.0f, 0.0f, 0.0f, velocityX: 2.0f, velocityY: 0.0f, velocityZ: 0.0f);

        store.Integrate(0.5f, gravityY: -10.0f);

        Assert.That(store.PositionX[0], Is.EqualTo(1.0f).Within(1e-5f));
        Assert.That(store.VelocityY[0], Is.EqualTo(-5.0f).Within(1e-5f));
        Assert.That(store.PositionY[0], Is.EqualTo(-2.5f).Within(1e-5f));
        Assert.That(store.Age[0], Is.EqualTo(0.5f).Within(1e-5f));
    }

    [Test]
    public void ResolveFloor_clamps_position_and_reflects_velocity()
    {
        var store = new EntityStore();
        store.Add(0.0f, -1.0f, 0.0f, velocityY: -4.0f);

        store.ResolveFloor(floorY: 0.0f, restitution: 0.5f);

        Assert.That(store.PositionY[0], Is.EqualTo(0.0f));
        Assert.That(store.VelocityY[0], Is.EqualTo(2.0f).Within(1e-5f));
    }

    [Test]
    public void ResolveFloor_leaves_entities_above_the_floor_alone()
    {
        var store = new EntityStore();
        store.Add(0.0f, 5.0f, 0.0f, velocityY: -4.0f);

        store.ResolveFloor(floorY: 0.0f);

        Assert.That(store.PositionY[0], Is.EqualTo(5.0f));
        Assert.That(store.VelocityY[0], Is.EqualTo(-4.0f));
    }

    [Test]
    public void RemoveExpired_removes_every_entity_past_the_lifetime()
    {
        var store = new EntityStore();
        store.Add(1.0f, 0.0f, 0.0f);
        store.Add(2.0f, 0.0f, 0.0f);
        store.Add(3.0f, 0.0f, 0.0f);

        // Age the first and third past the limit, leaving the second alive.
        store.Age[0] = 10.0f;
        store.Age[2] = 10.0f;

        var removed = store.RemoveExpired(maxAgeSeconds: 5.0f);

        Assert.That(removed, Is.EqualTo(2));
        Assert.That(store.Count, Is.EqualTo(1));
        Assert.That(store.PositionX[0], Is.EqualTo(2.0f));
    }

    [Test]
    public void RemoveExpired_does_not_skip_the_entity_swapped_into_a_removed_slot()
    {
        // Iterating forwards would move the last entity into a slot already passed and never visit it, so
        // this is the case that distinguishes a correct implementation from a plausible one.
        var store = new EntityStore();
        store.Add(1.0f, 0.0f, 0.0f);
        store.Add(2.0f, 0.0f, 0.0f);

        store.Age[0] = 10.0f;
        store.Age[1] = 10.0f;

        var removed = store.RemoveExpired(maxAgeSeconds: 5.0f);

        Assert.That(removed, Is.EqualTo(2));
        Assert.That(store.Count, Is.Zero);
    }

    [Test]
    public void Clear_empties_the_store_but_keeps_its_capacity()
    {
        var store = new EntityStore();
        store.Add(1.0f, 0.0f, 0.0f);
        var capacity = store.Capacity;

        store.Clear();

        Assert.That(store.Count, Is.Zero);
        Assert.That(store.Capacity, Is.EqualTo(capacity));
    }

    [Test]
    public void Stepping_a_large_population_allocates_nothing()
    {
        // The claim the layout exists to support: a steady-state tick does no managed allocation. Measured
        // rather than asserted in prose, because an accidental boxing or closure would be invisible otherwise.
        var store = new EntityStore(capacity: 4096);
        for (var i = 0; i < 4096; i++)
            store.Add(i, 10.0f, 0.0f, velocityY: -1.0f);

        // Warm up, so the measurement is not dominated by first-call JIT.
        for (var i = 0; i < 10; i++)
            Step(store);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
            Step(store);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero, $"stepping allocated {allocated} byte(s)");
    }

    private static void Step(EntityStore store)
    {
        store.Integrate(1.0f / 60.0f, gravityY: -9.81f);
        store.ResolveFloor(floorY: 0.0f);
        store.RemoveExpired(maxAgeSeconds: 30.0f);
    }
}
