using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// Tests over the meshlet budget arithmetic the brick size and the pool sizing depend on.
/// </summary>
[TestFixture]
public sealed class BrickShapeTests
{
    [Test]
    public void The_vertex_bound_counts_every_cell_the_brick_can_place_a_vertex_in()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BrickShape.MaxVerticesForBrick(2), Is.EqualTo(27));
            Assert.That(BrickShape.MaxVerticesForBrick(8), Is.EqualTo(729));
        });
    }

    [Test]
    public void The_triangle_bound_is_two_per_grid_edge()
    {
        // An n-cube grid has n(n+1)^2 edges per axis, three axes, and each crossed edge becomes a quad.
        Assert.That(BrickShape.MaxTrianglesForBrick(8), Is.EqualTo(6 * 8 * 9 * 9));
    }

    [Test]
    public void An_eight_voxel_brick_needs_several_meshlets()
    {
        // The premise the architecture rests on, stated as a test so it cannot silently stop being true:
        // one brick is more geometry than one meshlet can hold, so the split is required, not optional.
        Assert.That(BrickShape.MaxVerticesForBrick(8), Is.GreaterThan(BrickShape.MaxMeshletVertices));
        Assert.That(BrickShape.MaxMeshletsForBrick(8), Is.GreaterThan(1));
    }

    [Test]
    public void The_meshlet_budgets_stay_below_the_hardware_ceiling()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BrickShape.MeshletVertexBudget, Is.LessThan(BrickShape.MaxMeshletVertices));
            Assert.That(BrickShape.MeshletTriangleBudget, Is.LessThan(BrickShape.MaxMeshletTriangles));
            Assert.That(BrickShape.MeshletVertexBudget, Is.GreaterThan(3));
        });
    }

    [Test]
    public void Allows_accepts_anything_inside_the_hardware_ceiling_and_rejects_anything_past_it()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BrickShape.Allows(0, 0), Is.True);
            Assert.That(BrickShape.Allows(BrickShape.MaxMeshletVertices, BrickShape.MaxMeshletTriangles), Is.True);
            Assert.That(BrickShape.Allows(BrickShape.MaxMeshletVertices + 1, 0), Is.False);
            Assert.That(BrickShape.Allows(0, BrickShape.MaxMeshletTriangles + 1), Is.False);
            Assert.That(BrickShape.Allows(-1, 0), Is.False);
        });
    }

    [Test]
    public void DivideRoundUp_covers_the_tail()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BrickShape.DivideRoundUp(120, 60), Is.EqualTo(2));
            Assert.That(BrickShape.DivideRoundUp(121, 60), Is.EqualTo(3));
            Assert.That(BrickShape.DivideRoundUp(0, 60), Is.EqualTo(0));
            Assert.That(BrickShape.DivideRoundUp(60, 60), Is.EqualTo(1));
        });
    }

    [Test]
    public void DivideRoundUp_rejects_a_zero_group_size()
    {
        Assert.That(() => BrickShape.DivideRoundUp(1, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
