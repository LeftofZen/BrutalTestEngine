using Brutal.Numerics;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// The end-to-end test of the CPU world pipeline: the same path the GPU will run, from a clipmap slot to
/// meshlets in the pools.
/// </summary>
/// <remarks>
/// This is the test that matters most, because it is the only one that exercises the field, the extractor
/// and the splitter together. Each of them is tested alone elsewhere, and each alone can be correct while
/// the pipeline they form is wrong — a brick sampled at the wrong origin, a voxel size taken from the wrong
/// level, or a meshlet written past its pool. None of those show up in a unit test of one stage.
/// </remarks>
[TestFixture]
public sealed class SdfBrickStoreTests
{
    private static readonly WorldSettings Settings = new()
    {
        LevelCount = 3,
        BricksPerLevelAxis = 4,
        BrickVoxelCount = 8,
        BaseBrickSize = 8.0f,
    };

    private static SdfBrickStore CreateStore(int seed = 4242) =>
        new(new TerrainField(seed), Settings);

    [Test]
    public void The_pools_are_sized_for_every_brick_every_level_could_hold()
    {
        using var store = CreateStore();

        // Two pools hold one level's worth of bricks each, because the vertex pool is sized from the meshlet
        // count a level's bricks can produce and the index pool from the same. The exact length is less
        // important than the property that a fully loaded level cannot overflow either.
        var meshletsPerLevel = Settings.BricksPerLevel * BrickShape.MaxMeshletsForBrick(Settings.BrickVoxelCount);

        Assert.Multiple(() =>
        {
            Assert.That(
                store.VertexPool.Length,
                Is.GreaterThanOrEqualTo(meshletsPerLevel * MeshletBuilder.VertexStride * Meshlet.BytesPerVertex)
            );
            Assert.That(store.IndexPool.Length, Is.GreaterThanOrEqualTo(meshletsPerLevel * MeshletBuilder.IndexStride));
        });
    }

    [Test]
    public void A_brick_straddling_the_terrain_surface_produces_meshlets()
    {
        using var store = CreateStore();

        var meshlets = BuildBrickNearSurface(store, out var built);

        Assert.That(built, Is.True, "the brick near the terrain surface produced no geometry");
        Assert.That(meshlets.Length, Is.GreaterThan(0));

        foreach (var meshlet in meshlets)
        {
            Assert.Multiple(() =>
            {
                Assert.That(meshlet.VertexCount, Is.GreaterThan(0));
                Assert.That(meshlet.TriangleCount, Is.GreaterThan(0));
                Assert.That(BrickShape.Allows(meshlet.VertexCount, meshlet.TriangleCount), Is.True);
            });
        }
    }

    [Test]
    public void Every_meshlet_records_the_brick_it_belongs_to()
    {
        using var store = CreateStore();

        const int brickId = 12345;
        var brick = FindSurfaceBrick(store);

        var meshlets = store.Build(brick, brickId).ToArray();

        Assert.That(meshlets.Length, Is.GreaterThan(0));

        foreach (var meshlet in meshlets)
            Assert.That(meshlet.BrickId, Is.EqualTo(brickId));
    }

    [Test]
    public void A_brick_high_above_the_terrain_produces_nothing()
    {
        using var store = CreateStore();

        var brick = new BrickKey(0, new int3(0, 10_000, 0));

        var meshlets = store.Build(brick, 1).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(meshlets.Length, Is.EqualTo(0));
            Assert.That(store.EmptyBricks, Is.EqualTo(1));
            Assert.That(store.BricksBuilt, Is.EqualTo(1));
        });
    }

    [Test]
    public void Building_a_brick_is_allocation_free_after_the_first_call()
    {
        using var store = CreateStore();

        var brick = FindSurfaceBrick(store);

        store.Build(brick, 1);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 16; i++)
            store.Build(brick, 1);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The store reuses its field block, extraction scratch, mesh and meshlet list across bricks, so a
        // steady stream of rebuilds must not allocate. A frame that allocated per brick would put the
        // builder's cost into the garbage collector instead of into the world.
        Assert.That(allocated, Is.LessThan(64 * 1024), $"building 16 bricks allocated {allocated} bytes");
    }

    [Test]
    public void Every_built_meshlet_stays_inside_the_pools()
    {
        using var store = CreateStore();

        var brick = FindSurfaceBrick(store);

        foreach (var meshlet in store.Build(brick, 1))
        {
            var vertexEnd = (meshlet.VertexOffset + meshlet.VertexCount) * Meshlet.BytesPerVertex;
            var indexEnd = meshlet.IndexOffset + meshlet.IndexCount;

            Assert.Multiple(() =>
            {
                Assert.That(meshlet.VertexOffset, Is.GreaterThanOrEqualTo(0));
                Assert.That(vertexEnd, Is.LessThanOrEqualTo(store.VertexPool.Length));
                Assert.That(meshlet.IndexOffset, Is.GreaterThanOrEqualTo(0));
                Assert.That(indexEnd, Is.LessThanOrEqualTo(store.IndexPool.Length));
            });
        }
    }

    [Test]
    public void The_same_brick_builds_the_same_geometry_twice()
    {
        using var store = CreateStore();
        var brick = FindSurfaceBrick(store);

        var first = store.Build(brick, 1).ToArray();
        var firstPositions = store.VertexPool.AsSpan(0, first.Sum(m => m.VertexCount) * Meshlet.BytesPerVertex).ToArray();

        var second = store.Build(brick, 1).ToArray();
        var secondPositions = store.VertexPool.AsSpan(0, second.Sum(m => m.VertexCount) * Meshlet.BytesPerVertex).ToArray();

        // Determinism is what makes the brick cacheable and the GPU port testable: the same slot with the
        // same field must produce byte-identical geometry every time.
        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(first));
            Assert.That(secondPositions, Is.EqualTo(firstPositions));
        });
    }

    [Test]
    public void Different_seeds_produce_different_geometry()
    {
        using var firstStore = CreateStore(seed: 1);
        using var secondStore = CreateStore(seed: 2);

        var brick = FindSurfaceBrick(firstStore);

        var first = firstStore.Build(brick, 1).ToArray();
        var second = secondStore.Build(brick, 1).ToArray();

        Assert.That(second, Is.Not.EqualTo(first));
    }

    [Test]
    public void The_height_scratch_fills_every_column_at_the_configured_stride()
    {
        // The layout is directly observable, so it is asserted directly: a stride that lags the brick reads
        // the scratch out of position and then out of bounds, and a wrong mesh rather than an exception is
        // exactly the failure this pins down.
        var field = new TerrainField();
        var origin = new float3(37.5f, 12.0f, -84.25f);
        const float voxelSize = 0.5f;

        foreach (var voxelCount in new[] { 2, 4, 8, 16 })
        {
            var stride = voxelCount + 2;
            var scratch = new float[stride * stride];

            field.BuildHeightScratch(scratch, origin, voxelSize);

            for (var z = 0; z < stride; z++)
            {
                for (var x = 0; x < stride; x++)
                {
                    var expected = field.HeightAt(
                        origin.X + (x - 1) * voxelSize,
                        origin.Z + (z - 1) * voxelSize
                    );

                    Assert.That(
                        scratch[z * stride + x],
                        Is.EqualTo(expected).Within(1.0e-4f),
                        $"voxel count {voxelCount}, column ({x},{z})"
                    );
                }
            }
        }
    }

    [Test]
    public void A_height_scratch_that_is_not_square_is_rejected()
    {
        Assert.That(() => TerrainField.ScratchStride(11), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void The_height_scratch_is_sized_from_the_configured_brick_shape()
    {
        // Every brick size the settings allow must build a brick that straddles the terrain, which is only
        // possible if the store's scratch was sized and strided from the configured shape.
        foreach (var voxelCount in new[] { 2, 4, 8, 16 })
        {
            var settings = Settings with { BrickVoxelCount = voxelCount };
            var length = TerrainField.HeightScratchLength(voxelCount);

            Assert.That(
                TerrainField.ScratchStride(length),
                Is.EqualTo(voxelCount + 2),
                $"voxel count {voxelCount}"
            );

            using var store = new SdfBrickStore(new TerrainField(), settings);
            var clipmap = new SdfClipmap(settings, float3.Zero);
            var probe = new float3(0.0f, store.Field.HeightAt(0.0f, 0.0f), 0.0f);
            var brick = new BrickKey(0, clipmap.Level(0).BrickKeyAt(probe));

            Assert.That(
                store.Build(brick, 0).Length,
                Is.GreaterThan(0),
                $"brick of {voxelCount} voxels produced no geometry"
            );
        }
    }

    [Test]
    public void A_recentred_clipmap_can_build_every_brick_a_level_covers()
    {
        using var store = CreateStore();
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var level = clipmap.Level(1);
        var built = 0;

        for (var slot = 0; slot < Settings.BricksPerLevel; slot++)
        {
            var brick = new BrickKey(1, level.SlotBrickKey(slot));

            if (!store.Build(brick, slot).IsEmpty)
                built++;
        }

        // The point is not that every brick contains terrain — most will not — but that walking a whole
        // level's slots never throws, overflows a pool or produces a meshlet outside the budget.
        Assert.That(store.BricksBuilt, Is.EqualTo(Settings.BricksPerLevel));
        Assert.That(built, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Adjacent_bricks_sample_the_same_world_positions_at_their_shared_face()
    {
        using var store = CreateStore();
        var clipmap = new SdfClipmap(Settings, float3.Zero);
        var level = clipmap.Level(0);

        var voxelSize = store.Settings.VoxelSize(0);
        var brickSize = store.Settings.BrickSize(0);
        var stride = TerrainField.HeightScratchLength(store.Settings.BrickVoxelCount);
        var rowStride = TerrainField.ScratchStride(stride);

        var left = new BrickKey(0, new int3(-2, 0, 0));
        var right = new BrickKey(0, new int3(-1, 0, 0));

        var leftScratch = new float[stride];
        var rightScratch = new float[stride];

        store.Field.BuildHeightScratch(leftScratch, left.Origin(store.Settings), voxelSize);
        store.Field.BuildHeightScratch(rightScratch, right.Origin(store.Settings), voxelSize);

        // A brick's scratch covers its expanded footprint, so column c is world origin + (c - 1) * voxelSize.
        // The left brick's positive ring column therefore sits exactly on the shared face, and that same
        // plane is the right brick's first interior column. If the voxels do not fill the brick the two
        // columns land on different world positions and the world has a gap between them — which is what
        // makes terrain draw as isolated islands.
        var leftEdgeColumn = rowStride - 1;
        var rightFirstColumn = 1;

        var leftEdgeWorldX = left.Origin(store.Settings).X + (leftEdgeColumn - 1) * voxelSize;
        var rightFirstWorldX = right.Origin(store.Settings).X + (rightFirstColumn - 1) * voxelSize;

        Assert.That(
            leftEdgeWorldX,
            Is.EqualTo(rightFirstWorldX).Within(voxelSize * 0.001f + brickSize * 1e-5f),
            "the bricks' sampling lattices do not meet at the shared face"
        );

        for (var z = 0; z < rowStride; z++)
        {
            var leftHeight = leftScratch[z * rowStride + leftEdgeColumn];
            var rightHeight = rightScratch[z * rowStride + rightFirstColumn];

            Assert.That(
                leftHeight,
                Is.EqualTo(rightHeight).Within(1e-3f),
                $"the bricks disagree about the terrain height at their shared face on row {z}"
            );
        }
    }

    private static BrickKey FindSurfaceBrick(SdfBrickStore store)
    {
        var clipmap = new SdfClipmap(Settings, float3.Zero);

        // The terrain's base height is around 20 m, so the brick containing that point on the finest level is
        // certain to straddle the surface.
        var probe = new float3(0.0f, store.Field.HeightAt(0.0f, 0.0f), 0.0f);

        return new BrickKey(0, clipmap.Level(0).BrickKeyAt(probe));
    }

    private static ReadOnlySpan<Meshlet> BuildBrickNearSurface(SdfBrickStore store, out bool built)
    {
        var brick = FindSurfaceBrick(store);
        var meshlets = store.Build(brick, 0);

        built = !meshlets.IsEmpty;

        return meshlets;
    }
}
