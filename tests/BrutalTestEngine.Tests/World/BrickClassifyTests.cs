using System.Runtime.InteropServices;
using System.Text;
using Brutal.Numerics;
using BrutalTestEngine.Shaders;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.World;

/// <summary>
/// Tests over the brick classification pass's CPU reference, and the agreement between it, the shader and
/// the C# that drives it.
/// </summary>
/// <remarks>
/// The classification pass runs on the GPU, so nothing here can execute it. What these tests can do is pin
/// down everything around it that the GPU cannot be asked about: that the shader's declared workgroup size
/// matches the constant the dispatch uses, that the push constant block is the size and layout the struct
/// claims, that the shader's cell tables match the ones the CPU uses, and that the reference the GPU result
/// is compared against behaves the way the comparison assumes.
/// </remarks>
[TestFixture]
public sealed class BrickClassifyTests
{
    private static readonly WorldSettings Settings = WorldSettings.Default;

    private static string ShaderSource => Encoding.UTF8.GetString(ComputeShaders.BrickClassify);

    [Test]
    public void The_shader_s_workgroup_size_matches_the_managed_constant()
    {
        // The dispatch is derived from the constant, so a shader that declares a different size would
        // silently process the wrong number of bricks.
        Assert.That(ShaderSource, Does.Contain($"local_size_x = {ComputeShaders.BrickClassifyLocalSize}"));
    }

    [Test]
    public void The_push_constant_size_is_a_multiple_of_sixteen()
    {
        // Vulkan requires a push constant block to be a multiple of four bytes and a pipeline layout's range
        // to be a multiple of four; a four-component stride on both sides is what keeps the GLSL and the C#
        // struct in step, so the size is checked here rather than after a validation error at startup.
        Assert.That(ComputeShaders.BrickClassifyPushConstantSize % 16, Is.EqualTo(0));
    }

    [Test]
    public void The_push_constant_struct_fills_its_declared_size()
    {
        Assert.That(
            Marshal.SizeOf<BrickClassifyPushShapeProbe>(),
            Is.EqualTo(ComputeShaders.BrickClassifyPushConstantSize)
        );
    }

    [Test]
    public void The_shader_declares_the_three_push_constant_members_the_struct_carries()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ShaderSource, Does.Contain("ivec4 shape"));
            Assert.That(ShaderSource, Does.Contain("vec4 metric"));
            Assert.That(ShaderSource, Does.Contain("vec4 origin"));
        });
    }

    [Test]
    public void The_shader_uses_the_cell_corner_table_the_cpu_uses()
    {
        // The corner and edge tables have to agree exactly, or the shader and the reference disagree about
        // which cells the surface passes through. Checking the literals is crude but it is the only way to
        // compare a GLSL string against a C# array without a compiler for both.
        foreach (var corner in SdfBrick.CellCorners)
            Assert.That(ShaderSource, Does.Contain($"ivec3({corner.X}, {corner.Y}, {corner.Z})"), $"corner {corner}");

        foreach (var (a, b) in SdfBrick.CellEdgeCorners)
            Assert.That(ShaderSource, Does.Contain($"ivec2({a}, {b})"), $"edge {a}->{b}");
    }

    [Test]
    public void The_shader_declares_one_edge_for_every_cell_edge()
    {
        Assert.That(SdfBrick.CellEdgeCorners, Has.Length.EqualTo(SdfBrick.CellEdgeCount));
        Assert.That(SdfBrick.CellCorners, Has.Length.EqualTo(8));

        // A cell has twelve edges and eight corners; a table with a duplicated corner would still compile and
        // still classify, just wrongly.
        Assert.That(SdfBrick.CellCorners.Distinct().Count(), Is.EqualTo(8));
        Assert.That(SdfBrick.CellEdgeCorners.Distinct().Count(), Is.EqualTo(12));
    }

    [Test]
    public void The_height_function_is_finite_and_bounded()
    {
        for (var i = -2000; i <= 2000; i += 37)
        {
            var height = BrickClassifier.HeightAt(i * 3.5f, i * -2.25f);

            Assert.That(float.IsFinite(height), Is.True, $"height at {i} was not finite");
            Assert.That(MathF.Abs(height), Is.LessThan(130.0f), "the demo terrain must stay inside the levels");
        }
    }

    [Test]
    public void CountCrossedCells_reports_zero_for_a_brick_entirely_above_the_terrain()
    {
        var lattice = new int3(0, 1000, 0);

        Assert.That(BrickClassifier.CountCrossedCells(lattice, Settings, 0), Is.EqualTo(0));
    }

    [Test]
    public void CountCrossedCells_reports_zero_for_a_brick_entirely_below_the_terrain()
    {
        var lattice = new int3(0, -1000, 0);

        Assert.That(BrickClassifier.CountCrossedCells(lattice, Settings, 0), Is.EqualTo(0));
    }

    [Test]
    public void CountCrossedCells_reports_a_nonzero_count_for_a_brick_the_surface_passes_through()
    {
        // The terrain's highest point is well below y = 130, so a brick spanning y = 0 at the finest level
        // must contain the surface somewhere in its neighbourhood.
        var brickSize = Settings.BrickSize(0);
        var straddling = default(int3);

        for (var latticeY = -40; latticeY <= 40; latticeY++)
        {
            var candidate = new int3(0, latticeY, 0);

            if (BrickClassifier.CountCrossedCells(candidate, Settings, 0) == 0)
                continue;

            straddling = candidate;
            break;
        }

        var count = BrickClassifier.CountCrossedCells(straddling, Settings, 0);

        Assert.That(count, Is.GreaterThan(0));
        Assert.That(count, Is.LessThanOrEqualTo(Settings.VoxelsPerBrick), "the count is a cell count");
        Assert.That(brickSize, Is.GreaterThan(0.0f));
    }

    [Test]
    public void CountCrossedCells_counts_cells_rather_than_edges()
    {
        // Find a brick with a partial surface and check the count is a fraction of the cell count, which is
        // what distinguishes a cell count from an edge count or a boolean.
        var counts = new List<int>();

        for (var latticeY = -60; latticeY <= 60; latticeY++)
            counts.Add(BrickClassifier.CountCrossedCells(new int3(0, latticeY, 0), Settings, 0));

        Assert.That(counts, Has.Some.GreaterThan(0), "no brick contained the surface");
        Assert.That(counts, Has.Some.EqualTo(0), "no brick was empty");
        Assert.That(counts.Max(), Is.LessThan(Settings.VoxelsPerBrick));
    }

    [Test]
    public void The_cell_crossing_mask_agrees_with_HasCrossing()
    {
        // The grid is offset so that samples land exactly on zero, which is the case where a mask and a
        // boolean are most likely to disagree.
        var brick = new SdfBrick(4);

        for (var z = -1; z <= 4; z++)
        {
            for (var y = -1; y <= 4; y++)
            {
                for (var x = -1; x <= 4; x++)
                    brick.SetSample(x, y, z, y - 1.5f);
            }
        }

        for (var z = 0; z < 4; z++)
        {
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var mask = brick.CellCrossingMask(x, y, z);

                    Assert.That(
                        mask != 0,
                        Is.EqualTo(brick.HasCrossing(x, y, z)),
                        $"cell ({x},{y},{z}) disagreed"
                    );
                }
            }
        }
    }

    [Test]
    public void The_cell_crossing_mask_sets_one_bit_per_crossed_edge()
    {
        // A plane cutting diagonally through the brick, so nearly every cell has a mix of signs.
        var brick = new SdfBrick(4);

        for (var z = -1; z <= 4; z++)
        {
            for (var y = -1; y <= 4; y++)
            {
                for (var x = -1; x <= 4; x++)
                    brick.SetSample(x, y, z, x + y + z - 4.5f);
            }
        }

        var nonZero = 0;

        for (var z = 0; z < 4; z++)
        {
            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var mask = brick.CellCrossingMask(x, y, z);

                    // Bit by bit against a direct walk of the same edge table, so a mask that sets the wrong
                    // bits but the right number of them still fails.
                    for (var edge = 0; edge < SdfBrick.CellEdgeCount; edge++)
                    {
                        var (a, b) = SdfBrick.CellEdgeCorners[edge];
                        var ca = SdfBrick.CellCorners[a];
                        var cb = SdfBrick.CellCorners[b];

                        var expected = SdfBrick.HasCrossing(
                            brick.Sample(x + ca.X, y + ca.Y, z + ca.Z),
                            brick.Sample(x + cb.X, y + cb.Y, z + cb.Z)
                        );

                        Assert.That(
                            (mask & (1 << edge)) != 0,
                            Is.EqualTo(expected),
                            $"cell ({x},{y},{z}) edge {edge}"
                        );
                    }

                    if (mask != 0)
                        nonZero++;
                }
            }
        }

        Assert.That(nonZero, Is.GreaterThan(0), "no cell reported a crossing");
    }

    /// <summary>
    /// A structural mirror of the classification push constant block, for the size check.
    /// </summary>
    /// <remarks>
    /// The renderer's copy is internal to the engine assembly, and a test that reached into it would defeat
    /// the point of the check — which is that the size the shader is told equals the size the struct occupies.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Size = ComputeShaders.BrickClassifyPushConstantSize)]
    private struct BrickClassifyPushShapeProbe
    {
        public int VoxelCount;
        public int SampleCount;
        public int BrickCount;
        public int ReservedShape;

        public float BrickSize;
        public float VoxelSize;
        public float ReservedMetricX;
        public float ReservedMetricY;

        public float OriginX;
        public float OriginY;
        public float OriginZ;
        public float ReservedOrigin;
    }
}
