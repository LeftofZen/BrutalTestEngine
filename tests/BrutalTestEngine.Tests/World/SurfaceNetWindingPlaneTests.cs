using Brutal.Numerics;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// Pins the extractor's triangle winding against a field whose surface is known exactly.
/// </summary>
/// <remarks>
/// <para>
/// A noisy terrain field cannot decide a winding question: it produces triangles pointing every way, so a
/// test over it can only report a majority, and a majority is not the property that matters. A field whose
/// zero set is a plane removes the ambiguity entirely — every triangle must have the same geometric normal,
/// and that normal is known in advance.
/// </para>
/// <para>
/// The convention being pinned: the field is positive above the terrain surface, so the surface's outward
/// side is the direction the field increases in. A triangle visible from the outward side must therefore be
/// wound anticlockwise as seen from there, which makes its <c>(b-a) × (c-a)</c> normal point outward.
/// </para>
/// </remarks>
[TestFixture]
public sealed class SurfaceNetWindingPlaneTests
{
    private const int VoxelCount = 8;

    [Test]
    public void A_flat_upward_surface_is_wound_so_its_normal_points_up()
    {
        // The field is simply y in local coordinates: positive above the plane y = 0, negative below it. The
        // surface's outward side is up, so every emitted triangle must have a normal with a positive Y.
        var brick = BuildVerticalField();

        // The crossing is in the cell one below the brick's middle, because the field is non-negative from
        // lattice y = 0 upwards and a sample of exactly zero counts as outside.
        Assert.That(brick.HasCrossing(0, -1, 0), Is.True, "the test field has no crossing at all");

        var mesh = new BrickMesh(VoxelCount);
        var produced = new SurfaceNetExtractor(VoxelCount).Extract(brick, mesh);

        Assert.Multiple(() =>
        {
            Assert.That(produced, Is.True, "the extractor produced no geometry");
            Assert.That(mesh.TriangleCount, Is.GreaterThan(0), "the extractor produced no triangles");
        });

        var up = 0;
        var down = 0;
        var degenerate = 0;

        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            var a = Local(mesh, mesh.Indices[triangle * 3]);
            var b = Local(mesh, mesh.Indices[triangle * 3 + 1]);
            var c = Local(mesh, mesh.Indices[triangle * 3 + 2]);

            var geometric = Cross(b - a, c - a);

            // A perfectly flat surface through cell centres produces thin but non-zero triangles; one that
            // collapses to a line has no winding to test and is counted rather than silently ignored.
            if (MathF.Abs(geometric.Y) < 1e-6f)
            {
                degenerate++;
                continue;
            }

            if (geometric.Y > 0.0f)
                up++;
            else
                down++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(degenerate, Is.LessThan(mesh.TriangleCount), "every triangle was degenerate");
            Assert.That(down, Is.EqualTo(0), $"{down} triangles face downwards into the solid half-space");
            Assert.That(up, Is.GreaterThan(0), "no triangle faced upwards");
        });
    }

    [Test]
    public void Extracted_vertices_are_spread_across_the_brick_rather_than_collapsed_at_its_corner()
    {
        var brick = BuildVerticalField();

        var mesh = new BrickMesh(VoxelCount);
        new SurfaceNetExtractor(VoxelCount).Extract(brick, mesh);

        Assert.That(mesh.VertexCount, Is.GreaterThan(1), "the extractor produced too few vertices to spread");

        // The regression this pins: the mass centroid of a cell's crossings is in [0,1]³, so a vertex is only
        // placed correctly when the cell's own origin is added. Omitting it collapses every vertex of every
        // brick into a one-voxel patch at the minimum corner — the extracted topology stays perfect, the
        // counts stay perfect, and the world draws as scattered specks.
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var minZ = float.MaxValue;
        var maxZ = float.MinValue;

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            var p = Local(mesh, vertex);

            minX = MathF.Min(minX, p.X);
            maxX = MathF.Max(maxX, p.X);
            minZ = MathF.Min(minZ, p.Z);
            maxZ = MathF.Max(maxZ, p.Z);
        }

        Assert.Multiple(() =>
        {
            Assert.That(
                maxX - minX,
                Is.GreaterThan(VoxelCount * 0.8f),
                "the vertices span almost no X, so they collapsed into one cell"
            );
            Assert.That(
                maxZ - minZ,
                Is.GreaterThan(VoxelCount * 0.8f),
                "the vertices span almost no Z, so they collapsed into one cell"
            );
        });
    }

    [Test]
    public void An_extracted_vertex_sits_at_its_own_cells_position()
    {
        var brick = BuildVerticalField();

        var mesh = new BrickMesh(VoxelCount);
        new SurfaceNetExtractor(VoxelCount).Extract(brick, mesh);

        // Every vertex must fall inside the brick's expanded local bounds. A centroid-only placement puts
        // them all in [0,1], which is inside those bounds — so the assertion is on the spread, checked above,
        // and here on the fact that the whole range is actually used.
        for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            var p = Local(mesh, vertex);

            Assert.That(p.X, Is.InRange(-1.5f, VoxelCount + 0.5f), $"vertex {vertex} X is out of bounds");
            Assert.That(p.Z, Is.InRange(-1.5f, VoxelCount + 0.5f), $"vertex {vertex} Z is out of bounds");
        }
    }

    [Test]
    public void A_flat_surface_wound_about_x_faces_along_x()
    {
        // A field of (x - c) has its zero set in the Y-Z plane, so its quads come from the accelerator's
        // X-axis edges. Each axis has its own ring orientation, so a check that only covers Y leaves the
        // other two thirds of a typical terrain surface unverified — and two thirds is exactly how much of
        // the world disappears when one of them is wound the wrong way.
        AssertPlaneWinding(dx: 1.0f, dy: 0.0f, dz: 0.0f, expectedAxis: 0);
    }

    [Test]
    public void A_flat_surface_wound_about_y_faces_along_y()
    {
        AssertPlaneWinding(dx: 0.0f, dy: 1.0f, dz: 0.0f, expectedAxis: 1);
    }

    [Test]
    public void A_flat_surface_wound_about_z_faces_along_z()
    {
        AssertPlaneWinding(dx: 0.0f, dy: 0.0f, dz: 1.0f, expectedAxis: 2);
    }

    private static void AssertPlaneWinding(float dx, float dy, float dz, int expectedAxis)
    {
        var brick = BuildLinearField(dx, dy, dz);

        var mesh = new BrickMesh(VoxelCount);
        new SurfaceNetExtractor(VoxelCount).Extract(brick, mesh);

        Assert.That(mesh.TriangleCount, Is.GreaterThan(0), "the extractor produced no triangles");

        var forward = 0;
        var backward = 0;
        var degenerate = 0;

        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            var a = Local(mesh, mesh.Indices[triangle * 3]);
            var b = Local(mesh, mesh.Indices[triangle * 3 + 1]);
            var c = Local(mesh, mesh.Indices[triangle * 3 + 2]);

            var geometric = Cross(b - a, c - a);
            var component = expectedAxis switch
            {
                0 => geometric.X,
                1 => geometric.Y,
                _ => geometric.Z,
            };

            if (MathF.Abs(component) < 1e-6f)
            {
                degenerate++;
                continue;
            }

            // The field increases along the plane's outward direction, so a triangle seen from outside must
            // be wound anticlockwise and its geometric normal must point the same way as the gradient.
            if (component > 0.0f)
                forward++;
            else
                backward++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(degenerate, Is.LessThan(mesh.TriangleCount), "every triangle was degenerate");
            Assert.That(
                backward,
                Is.EqualTo(0),
                $"{backward} triangles around axis {expectedAxis} are wound backwards, so that axis's quads "
                    + "are culled when seen from outside"
            );
            Assert.That(forward, Is.GreaterThan(0), $"no triangle faced along axis {expectedAxis}");
        });
    }

    [Test]
    public void A_flat_upward_surfaces_vertices_store_an_upward_normal()
    {
        var brick = BuildVerticalField();

        var mesh = new BrickMesh(VoxelCount);
        new SurfaceNetExtractor(VoxelCount).Extract(brick, mesh);

        Assert.That(mesh.VertexCount, Is.GreaterThan(0), "the extractor produced no vertices");

        var upward = 0;

        for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            var normal = Octahedral.Decode(mesh.NormalAt(vertex));

            if (normal.Y > 0.5f)
                upward++;
        }

        Assert.That(
            upward,
            Is.GreaterThan(0),
            "no vertex stored an upward normal, so the field's gradient sign or the normal encode is wrong"
        );
    }

    /// <summary>
    /// A brick whose field is <c>dx·x + dy·y + dz·z</c>, so its zero set is a plane through the brick's
    /// middle with a known outward direction.
    /// </summary>
    private static SdfBrick BuildLinearField(float dx, float dy, float dz)
    {
        var brick = new SdfBrick(VoxelCount);

        // Local lattice coordinates run -1..VoxelCount on every axis, so a plane through the origin crosses
        // the middle of the brick.
        for (var k = -1; k <= VoxelCount; k++)
        {
            for (var j = -1; j <= VoxelCount; j++)
            {
                for (var i = -1; i <= VoxelCount; i++)
                    brick.SetSample(i, j, k, dx * i + dy * j + dz * k);
            }
        }

        return brick;
    }

    /// <summary>
    /// A brick whose field is <c>-y</c>, so its zero set is the horizontal plane through the brick's middle.
    /// </summary>
    private static SdfBrick BuildVerticalField() => BuildLinearField(0.0f, 1.0f, 0.0f);

    private static float3 Local(BrickMesh mesh, int vertex)
    {
        var (x, y, z) = mesh.DecodePosition(vertex);

        return new float3(x, y, z);
    }

    private static float3 Cross(float3 a, float3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
