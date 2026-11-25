using Brutal.Numerics;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// Tests that a real terrain brick's extracted geometry fills its brick.
/// </summary>
/// <remarks>
/// A brick's geometry has to span very nearly the brick's own size, because bricks are placed edge to edge. If
/// it spans much less, the world renders as detached islands of geometry with empty space between them, and
/// every counter in the engine — bricks built, meshlets produced, vertices written — still reports a healthy
/// world. That is why the property is asserted here rather than inferred from the counters.
/// </remarks>
[TestFixture]
public sealed class BrickGeometryExtentTests
{
    private static readonly WorldSettings Settings = new()
    {
        LevelCount = 3,
        BricksPerLevelAxis = 4,
        BrickVoxelCount = 8,
        BaseBrickSize = 8.0f,
        Seed = 4242,
    };

    [Test]
    public void A_terrain_bricks_geometry_spans_its_brick_in_both_horizontal_axes()
    {
        using var store = new SdfBrickStore(new TerrainField(Settings.Seed), Settings);

        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var level = clipmap.Level(0);
        var probe = new float3(0.0f, store.Field.HeightAt(0.0f, 0.0f), 0.0f);

        var meshlets = store.Build(new BrickKey(0, level.BrickKeyAt(probe)), 0);

        Assert.That(meshlets.IsEmpty, Is.False, "the brick over the camera produced no geometry");

        var min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new float3(float.MinValue, float.MinValue, float.MinValue);

        foreach (var meshlet in meshlets)
        {
            min = new float3(
                MathF.Min(min.X, meshlet.BoundsCenter.X - meshlet.Radius),
                MathF.Min(min.Y, meshlet.BoundsCenter.Y - meshlet.Radius),
                MathF.Min(min.Z, meshlet.BoundsCenter.Z - meshlet.Radius)
            );
            max = new float3(
                MathF.Max(max.X, meshlet.BoundsCenter.X + meshlet.Radius),
                MathF.Max(max.Y, meshlet.BoundsCenter.Y + meshlet.Radius),
                MathF.Max(max.Z, meshlet.BoundsCenter.Z + meshlet.Radius)
            );
        }

        var brickSize = Settings.BrickSize(0);

        // The meshlet bounds are spheres, so they overstate the extent rather than understating it. Erring
        // generous is fine here: the failure being tested produces geometry a fraction of a metre across when
        // it should be eight metres, a difference of an order of magnitude.
        Assert.Multiple(() =>
        {
            // Meshlet bounds are spheres around the meshlet's own vertices, so they overstate the extent
            // rather than understating it. The failure being tested leaves roughly a tenth of a brick, so a
            // third is a wide margin on both sides.
            Assert.That(
                max.X - min.X,
                        Is.GreaterThan(brickSize * 0.3f),
                        "the geometry spans almost none of the brick in X, so bricks cannot meet"
            );
            Assert.That(
                max.Z - min.Z,
                        Is.GreaterThan(brickSize * 0.3f),
                        "the geometry spans almost none of the brick in Z, so bricks cannot meet"
            );
        });
    }

    [Test]
    public void A_terrain_bricks_vertices_are_not_clustered_in_one_corner()
    {
        using var store = new SdfBrickStore(new TerrainField(Settings.Seed), Settings);

        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var level = clipmap.Level(0);
        var probe = new float3(0.0f, store.Field.HeightAt(0.0f, 0.0f), 0.0f);

        var meshlets = store.Build(new BrickKey(0, level.BrickKeyAt(probe)), 0);

        Assert.That(meshlets.IsEmpty, Is.False, "the brick over the camera produced no geometry");

        // Local coordinates must be spread across the brick's expanded [-1, VoxelCount] range. A centroid that
        // omits its cell's origin produces vertices only in [0,1], which is the same symptom as a decode scale
        // that is far too small.
        var min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new float3(float.MinValue, float.MinValue, float.MinValue);
        var bytesPerVertex = Meshlet.BytesPerVertex;

        foreach (var meshlet in meshlets)
        {
            for (var vertex = 0; vertex < meshlet.VertexCount; vertex++)
            {
                var offset = (meshlet.VertexOffset + vertex) * bytesPerVertex;

                var x = store.VertexPool[offset] / 255.0f * (Settings.BrickVoxelCount + 1) - 1.0f;
                var z = store.VertexPool[offset + 2] / 255.0f * (Settings.BrickVoxelCount + 1) - 1.0f;

                min = new float3(MathF.Min(min.X, x), min.Y, MathF.Min(min.Z, z));
                max = new float3(MathF.Max(max.X, x), max.Y, MathF.Max(max.Z, z));
            }
        }

        Assert.Multiple(() =>
        {
            // The threshold is a third of the brick rather than most of it, because a brick whose terrain
            // slopes out through its side genuinely holds a diagonal band of geometry rather than a full
            // footprint. What the test is catching is the order-of-magnitude failure — a collapse into one
            // cell, which measures under a tenth of a brick.
            Assert.That(
                max.X - min.X,
                        Is.GreaterThan(Settings.BrickVoxelCount * 0.3f),
                        "the packed vertices span almost no local X, so they collapsed into one cell"
            );
            Assert.That(
                max.Z - min.Z,
                        Is.GreaterThan(Settings.BrickVoxelCount * 0.3f),
                        "the packed vertices span almost no local Z, so they collapsed into one cell"
            );
        });
    }
}
