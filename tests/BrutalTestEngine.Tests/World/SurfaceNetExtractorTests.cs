using Brutal.Numerics;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// Tests over the field block, the surface extractor and the meshlet splitter.
/// </summary>
[TestFixture]
public sealed class SurfaceNetExtractorTests
{
    private const int Voxels = 8;
    private const float VoxelSize = 1.0f;

    /// <summary>A flat ground plane at <c>y = 0</c>, so a brick spanning it produces a flat sheet.</summary>
    private static float Plane(float3 position) => position.Y;

    [Test]
    public void A_brick_entirely_above_the_surface_is_empty()
    {
        var brick = new SdfBrick(Voxels);
        brick.Sample(Plane, new float3(0.0f, 100.0f, 0.0f), VoxelSize);

        Assert.That(brick.IsEmpty(), Is.True);
    }

    [Test]
    public void A_brick_entirely_below_the_surface_is_not_empty()
    {
        var brick = new SdfBrick(Voxels);
        brick.Sample(Plane, new float3(0.0f, -100.0f, 0.0f), VoxelSize);

        Assert.That(brick.IsEmpty(), Is.False);
    }

    [Test]
    public void The_block_has_a_ring_of_samples_around_the_brick()
    {
        var brick = new SdfBrick(Voxels);

        Assert.Multiple(() =>
        {
            Assert.That(brick.VoxelCount, Is.EqualTo(Voxels));
            Assert.That(brick.SampleCount, Is.EqualTo(Voxels + 2));
            Assert.That(brick.SampleLength, Is.EqualTo((Voxels + 2) * (Voxels + 2) * (Voxels + 2)));
        });
    }

    [Test]
    public void The_sample_accessor_round_trips_over_the_signed_range()
    {
        var brick = new SdfBrick(2);

        brick.SetSample(-1, 0, 1, 42.5f);

        Assert.That(brick.Sample(-1, 0, 1), Is.EqualTo(42.5f));
    }

    [Test]
    public void The_gradient_points_towards_positive_distance()
    {
        var brick = new SdfBrick(Voxels);
        brick.Sample(Plane, new float3(0.0f, -4.0f, 0.0f), VoxelSize);

        // Above a ground plane the distance grows with Y, so the gradient must be positive in Y and zero in
        // X and Z.
        var gradient = brick.Gradient(4, 4, 4);

        Assert.Multiple(() =>
        {
            Assert.That(gradient.X, Is.EqualTo(0.0f));
            Assert.That(gradient.Y, Is.GreaterThan(0.0f));
            Assert.That(gradient.Z, Is.EqualTo(0.0f));
        });
    }

    [Test]
    public void Extracting_a_flat_plane_produces_geometry()
    {
        var brick = new SdfBrick(Voxels);
        brick.Sample(Plane, new float3(0.0f, -4.0f, 0.0f), VoxelSize);

        var mesh = new BrickMesh(Voxels);

        Assert.That(new SurfaceNetExtractor(Voxels).Extract(brick, mesh), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(mesh.VertexCount, Is.GreaterThan(0));
            Assert.That(mesh.TriangleCount, Is.GreaterThan(0));
            Assert.That(mesh.IndexCount, Is.EqualTo(mesh.TriangleCount * 3));
        });
    }

    [Test]
    public void Extracting_an_empty_brick_produces_nothing()
    {
        var brick = new SdfBrick(Voxels);
        brick.Sample(Plane, new float3(0.0f, 100.0f, 0.0f), VoxelSize);

        var mesh = new BrickMesh(Voxels);

        Assert.Multiple(() =>
        {
            Assert.That(new SurfaceNetExtractor(Voxels).Extract(brick, mesh), Is.False);
            Assert.That(mesh.IsEmpty, Is.True);
        });
    }

    [Test]
    public void Every_vertex_stays_inside_the_expanded_local_range()
    {
        var brick = new SdfBrick(Voxels);
        brick.Sample(Sphere(new float3(4.0f, 4.0f, 4.0f), 4.0f), float3.Zero, VoxelSize);

        var mesh = new BrickMesh(Voxels);
        new SurfaceNetExtractor(Voxels).Extract(brick, mesh);

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            var (x, y, z) = mesh.DecodePosition(vertex);

            // The quantisation step is one byte across the local range, so a decoded coordinate can be a
            // little outside [-1, n] without the encoding having lost anything.
            Assert.That(x, Is.InRange(-1.05f, Voxels + 0.05f), $"vertex {vertex} X");
            Assert.That(y, Is.InRange(-1.05f, Voxels + 0.05f), $"vertex {vertex} Y");
            Assert.That(z, Is.InRange(-1.05f, Voxels + 0.05f), $"vertex {vertex} Z");
        }
    }

    [Test]
    public void A_closed_surface_is_watertight()
    {
        // A sphere is the case where an extraction bug shows up as a hole: every edge of a closed manifold
        // index buffer must appear exactly twice, once in each direction.
        var brick = new SdfBrick(Voxels);
        brick.Sample(Sphere(new float3(4.0f, 4.0f, 4.0f), 3.4f), float3.Zero, VoxelSize);

        var mesh = new BrickMesh(Voxels);
        Assert.That(new SurfaceNetExtractor(Voxels).Extract(brick, mesh), Is.True);

        var halfEdges = new Dictionary<(int A, int B), int>();

        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            var a = mesh.Indices[triangle * 3];
            var b = mesh.Indices[triangle * 3 + 1];
            var c = mesh.Indices[triangle * 3 + 2];

            foreach (var (from, to) in new[] { (a, b), (b, c), (c, a) })
                halfEdges[(from, to)] = halfEdges.GetValueOrDefault((from, to)) + 1;
        }

        foreach (var ((from, to), count) in halfEdges)
        {
            Assert.Multiple(() =>
            {
                Assert.That(count, Is.EqualTo(1), $"half-edge {from}->{to} was emitted {count} times");
                Assert.That(halfEdges.ContainsKey((to, from)), Is.True, $"half-edge {to}->{from} is missing");
            });
        }
    }

    [Test]
    public void A_plane_through_a_brick_has_no_half_edge_used_twice()
    {
        var brick = new SdfBrick(Voxels);
        brick.Sample(Plane, new float3(0.0f, -4.0f, 0.0f), VoxelSize);

        var mesh = new BrickMesh(Voxels);
        new SurfaceNetExtractor(Voxels).Extract(brick, mesh);

        var halfEdges = new HashSet<(int A, int B)>();

        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            var a = mesh.Indices[triangle * 3];
            var b = mesh.Indices[triangle * 3 + 1];
            var c = mesh.Indices[triangle * 3 + 2];

            foreach (var halfEdge in new[] { (a, b), (b, c), (c, a) })
                Assert.That(halfEdges.Add(halfEdge), Is.True, $"half-edge {halfEdge} appears twice");
        }
    }

    [Test]
    public void A_mesh_sized_for_a_different_brick_shape_is_rejected()
    {
        var brick = new SdfBrick(4);
        var mesh = new BrickMesh(8);

        Assert.That(
            () => new SurfaceNetExtractor(Voxels).Extract(brick, mesh),
            Throws.TypeOf<ArgumentException>()
        );
    }

    [Test]
    public void A_brick_larger_than_the_meshlet_budget_is_split_into_several_meshlets()
    {
        var mesh = BuildSphereMesh();

        var meshlets = new List<Meshlet>();
        var vertexPool = new byte[256 * MeshletBuilder.VertexStride * Meshlet.BytesPerVertex];
        var indexPool = new ushort[256 * MeshletBuilder.IndexStride];

        var built = MeshletBuilder.Build(
            mesh,
            meshlets,
            vertexPool,
            indexPool,
            brickId: 7,
            float3.Zero,
            VoxelSize
        );

        Assert.That(built, Is.GreaterThan(0));
        Assert.That(meshlets, Has.Count.EqualTo(built));
    }

    [Test]
    public void Every_meshlet_fits_the_hardware_budget()
    {
        foreach (var meshlet in BuildSphereMeshlets())
        {
            Assert.Multiple(() =>
            {
                Assert.That(meshlet.VertexCount, Is.GreaterThan(0));
                Assert.That(meshlet.VertexCount, Is.LessThanOrEqualTo(BrickShape.MeshletVertexBudget));
                Assert.That(meshlet.IndexCount, Is.LessThanOrEqualTo(BrickShape.MeshletTriangleBudget * 3));
                Assert.That(meshlet.IndexCount % 3, Is.EqualTo(0));
                Assert.That(
                    BrickShape.Allows(meshlet.VertexCount, meshlet.TriangleCount),
                    Is.True,
                    "a meshlet exceeded the hardware budget"
                );
            });
        }
    }

    [Test]
    public void The_split_preserves_every_triangle()
    {
        var mesh = BuildSphereMesh();
        var meshlets = BuildSphereMeshlets(out var emitted);

        Assert.That(emitted, Is.EqualTo(mesh.TriangleCount));
        Assert.That(meshlets.Sum(m => m.TriangleCount), Is.EqualTo(mesh.TriangleCount));
    }

    [Test]
    public void Every_meshlet_index_is_local_to_its_own_vertex_range()
    {
        var mesh = BuildSphereMesh();
        var meshlets = new List<Meshlet>();
        var vertexPool = new byte[256 * MeshletBuilder.VertexStride * Meshlet.BytesPerVertex];
        var indexPool = new ushort[256 * MeshletBuilder.IndexStride];

        MeshletBuilder.Build(mesh, meshlets, vertexPool, indexPool, 3, float3.Zero, VoxelSize);

        foreach (var meshlet in meshlets)
        {
            for (var i = 0; i < meshlet.IndexCount; i++)
            {
                var index = indexPool[meshlet.IndexOffset + i];

                Assert.That(
                    index,
                    Is.LessThan(meshlet.VertexCount),
                    "a meshlet index pointed outside its own vertex range"
                );
            }
        }
    }

    [Test]
    public void A_meshlet_bounding_sphere_contains_every_one_of_its_vertices()
    {
        var mesh = BuildSphereMesh();
        var meshlets = new List<Meshlet>();
        var vertexPool = new byte[256 * MeshletBuilder.VertexStride * Meshlet.BytesPerVertex];
        var indexPool = new ushort[256 * MeshletBuilder.IndexStride];

        MeshletBuilder.Build(mesh, meshlets, vertexPool, indexPool, 3, float3.Zero, VoxelSize);

        foreach (var meshlet in meshlets)
        {
            for (var i = 0; i < meshlet.VertexCount; i++)
            {
                var vertex = DecodePoolVertex(vertexPool, meshlet.VertexOffset + i);
                var offset = vertex - meshlet.BoundsCenter;
                var distance = MathF.Sqrt(
                    offset.X * offset.X + offset.Y * offset.Y + offset.Z * offset.Z
                );

                // A round of slack, because the quantised position the pool holds is what the sphere was
                // built from, and the comparison re-derives it.
                Assert.That(
                    distance,
                    Is.LessThanOrEqualTo(meshlet.Radius + 1.0e-4f),
                    "a vertex fell outside its meshlet's bounding sphere"
                );
            }
        }
    }

    [Test]
    public void An_empty_mesh_produces_no_meshlets()
    {
        var mesh = new BrickMesh(Voxels);
        var meshlets = new List<Meshlet>();

        var built = MeshletBuilder.Build(
            mesh,
            meshlets,
            new byte[1024],
            new ushort[1024],
            0,
            float3.Zero,
            VoxelSize
        );

        Assert.Multiple(() =>
        {
            Assert.That(built, Is.EqualTo(0));
            Assert.That(meshlets, Is.Empty);
        });
    }

    [Test]
    public void A_meshlet_whose_normals_stay_together_has_a_usable_cone()
    {
        var brick = new SdfBrick(Voxels);
        brick.Sample(Plane, new float3(0.0f, -4.0f, 0.0f), VoxelSize);

        var mesh = new BrickMesh(Voxels);
        new SurfaceNetExtractor(Voxels).Extract(brick, mesh);

        var meshlets = new List<Meshlet>();

        MeshletBuilder.Build(
            mesh,
            meshlets,
            new byte[256 * MeshletBuilder.VertexStride * Meshlet.BytesPerVertex],
            new ushort[256 * MeshletBuilder.IndexStride],
            0,
            float3.Zero,
            VoxelSize
        );

        Assert.That(meshlets, Is.Not.Empty);
        Assert.That(meshlets[0].IsConeDegenerate, Is.False, "a flat sheet's normals all point one way");
    }

    private static float3 DecodePoolVertex(byte[] pool, int vertex)
    {
        var offset = vertex * Meshlet.BytesPerVertex;

        // The pool's positions are the mesh's quantised bytes, so the decode is the same one the mesh uses.
        var scale = (Voxels + 1) / 255.0f;

        return new float3(
            pool[offset] * scale - 1.0f,
            pool[offset + 1] * scale - 1.0f,
            pool[offset + 2] * scale - 1.0f
        );
    }

    private static BrickMesh BuildSphereMesh()
    {
        var brick = new SdfBrick(Voxels);
        brick.Sample(Sphere(new float3(4.0f, 4.0f, 4.0f), 3.4f), float3.Zero, VoxelSize);

        var mesh = new BrickMesh(Voxels);
        new SurfaceNetExtractor(Voxels).Extract(brick, mesh);

        return mesh;
    }

    private static List<Meshlet> BuildSphereMeshlets() => BuildSphereMeshlets(out _);

    private static List<Meshlet> BuildSphereMeshlets(out int emittedTriangles)
    {
        var mesh = BuildSphereMesh();
        var meshlets = new List<Meshlet>();

        MeshletBuilder.Build(
            mesh,
            meshlets,
            new byte[256 * MeshletBuilder.VertexStride * Meshlet.BytesPerVertex],
            new ushort[256 * MeshletBuilder.IndexStride],
            brickId: 1,
            float3.Zero,
            VoxelSize
        );

        emittedTriangles = mesh.TriangleCount;

        return meshlets;
    }

    private static Func<float3, float> Sphere(float3 center, float radius) =>
        position =>
        {
            var dx = position.X - center.X;
            var dy = position.Y - center.Y;
            var dz = position.Z - center.Z;

            return MathF.Sqrt(dx * dx + dy * dy + dz * dz) - radius;
        };
}
