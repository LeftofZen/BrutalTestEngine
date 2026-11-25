using Brutal.Numerics;

namespace BrutalTestEngine.World;

/// <summary>
/// Extracts a brick's surface as meshlet-ready geometry, using Surface Nets.
/// </summary>
/// <remarks>
/// <para>
/// Surface Nets rather than Marching Cubes or Dual Contouring, chosen for three properties that matter
/// here and one that does not.
/// </para>
/// <para>
/// It is manifold by construction: one vertex per cell the surface crosses, one quad per grid edge the
/// surface crosses, and every edge ends up in exactly two faces. Marching Cubes has 256 cases whose classic
/// ambiguous ones can produce holes, and a hole in this design shows up as a crack at a brick boundary —
/// the exact failure the architecture cannot tolerate, because brick boundaries are everywhere.
/// </para>
/// <para>
/// The vertex count is bounded by the cell count rather than by the corner count, which is what lets the
/// mesh's buffers be sized once from <see cref="BrickShape.MaxVerticesForBrick"/> instead of being grown.
/// </para>
/// <para>
/// It smooths, which is right for terrain and wrong for semantics. Dual Contouring can represent a sharp
/// crease inside a single cell by moving the vertex; Surface Nets place the vertex at the mass centroid of
/// the crossings and stay smooth. Terrain wants smooth. Where sharpness matters later — a carved wall, a
/// building — the answer is a finer clipmap level, not a different algorithm, and the clipmap already
/// provides those near the camera.
/// </para>
/// <para>
/// This implementation is the <em>reference</em>. It runs on the CPU, it is not in the frame path, and its
/// job is to be obviously correct so the compute shader that will replace it can be tested against it. That
/// is the same pattern the compute self-test already uses.
/// </para>
/// </remarks>
/// <para>
/// This implementation is the <em>reference</em>. It runs on the CPU, it is not in the frame path, and its
/// job is to be obviously correct so the compute shader that will replace it can be tested against it. That
/// is the same pattern the compute self-test already uses.
/// </para>
/// <para>
/// It is an instance rather than a set of static methods for one reason: its scratch is the mapping from a
/// cell to the vertex it produced, and that mapping is three kilobytes for an <c>8³</c> brick. Allocating it
/// per call would put the extractor's cost into the garbage collector, and every brick in a clipmap runs
/// through here. One instance per worker keeps the scratch alive across bricks and means brick building
/// allocates nothing after the first call.
/// </para>
/// </remarks>
public sealed class SurfaceNetExtractor
{
    /// <summary>The eight corners of a cell, as offsets from its minimum corner.</summary>
    private static readonly int3[] CellCorners = SdfBrick.CellCorners;

    /// <summary>The twelve edges of a cell, from the field block's single definition of them.</summary>
    private static readonly (int A, int B)[] CellEdges = SdfBrick.CellEdgeCorners;

    /// <summary>
    /// The four cells sharing a grid edge, as offsets from the cell whose minimum corner is the edge's
    /// start, in rotational order about the edge's axis.
    /// </summary>
    /// <remarks>
    /// The red face of every cell is its low-corner face, and the two edges in the <c>Y</c> and <c>Z</c>
    /// directions disappear when the corner is at a maximum on those axes. Stating that as a static table
    /// rather than as a loop with three axis cases is what makes the quad pass a flat walk over the brick
    /// with no branching on the edge's direction beyond indexing the table.
    /// </remarks>
    private static readonly int3[][] EdgeRings =
    [
        // Axis 0 (X): the ring lies in the Y-Z plane.
        [
            new(0, 0, 0), new(0, 0, -1), new(0, -1, -1), new(0, -1, 0),
        ],
        // Axis 1 (Y): the ring lies in the X-Z plane.
        [
            new(0, 0, 0), new(-1, 0, 0), new(-1, 0, -1), new(0, 0, -1),
        ],
        // Axis 2 (Z): the ring lies in the X-Y plane.
        [
            new(0, 0, 0), new(0, -1, 0), new(-1, -1, 0), new(-1, 0, 0),
        ],
    ];

    private static readonly int3[] EdgeSteps =
    [
        new(1, 0, 0),
        new(0, 1, 0),
        new(0, 0, 1),
    ];

    private int[] _vertexOfCell;
    private int _capacity;

    /// <summary>Creates an extractor sized for a brick of <paramref name="voxelCount"/> voxels.</summary>
    public SurfaceNetExtractor(int voxelCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(voxelCount, 1);

        VoxelCount = voxelCount;
        _vertexOfCell = [];
    }

    /// <summary>The brick shape this extractor is sized for.</summary>
    public int VoxelCount { get; }

    /// <summary>
    /// Extracts the surface of <paramref name="brick"/> into <paramref name="mesh"/>.
    /// </summary>
    /// <param name="brick">The brick's field. Its neighbour ring supplies the outside samples.</param>
    /// <param name="mesh">The mesh to write into. Its counts are reset first.</param>
    /// <returns>True when geometry was produced.</returns>
    /// <remarks>
    /// <para>
    /// The cells visited run over <c>[-1, n-1]</c> on every axis: one cell below the brick's own minimum,
    /// through its last cell. Reaching one cell below is what makes the brick's six negative faces its
    /// responsibility and its six positive faces its neighbour's. Each shared face is therefore meshed
    /// exactly once, by the brick on its negative side, and never twice — which is the difference between a
    /// watertight world and one with a doubled shell along every brick boundary.
    /// </para>
    /// <para>
    /// Local positions are in the expanded range <c>[-1, n]</c>, matching what
    /// <see cref="BrickMesh.AddVertex"/> expects.
    /// </para>
    /// </remarks>
    public bool Extract(SdfBrick brick, BrickMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(brick);
        ArgumentNullException.ThrowIfNull(mesh);

        if (brick.VoxelCount != VoxelCount || mesh.VoxelCount != VoxelCount)
        {
            throw new ArgumentException(
                "The field, the mesh and the extractor must all be sized for the same brick shape.",
                nameof(mesh)
            );
        }

        mesh.Clear();

        var cellCount = VoxelCount + 1;
        EnsureCapacity(cellCount * cellCount * cellCount);
        Array.Fill(_vertexOfCell, -1);

        if (!ExtractVertices(brick, mesh, cellCount) || mesh.VertexCount == 0)
            return false;

        ExtractQuads(brick, mesh, cellCount);

        return !mesh.IsEmpty;
    }

    private void EnsureCapacity(int length)
    {
        if (_capacity >= length)
            return;

        _vertexOfCell = new int[length];
        _capacity = length;
    }

    private bool ExtractVertices(SdfBrick brick, BrickMesh mesh, int cellCount)
    {
        // Pass one: one vertex per cell the surface crosses, at the mass centroid of the crossings.
        for (var cz = 0; cz < cellCount; cz++)
        {
            for (var cy = 0; cy < cellCount; cy++)
            {
                for (var cx = 0; cx < cellCount; cx++)
                {
                    var x = cx - 1;
                    var y = cy - 1;
                    var z = cz - 1;

                    var sum = float3.Zero;
                    var crossings = 0;

                    foreach (var (a, b) in CellEdges)
                    {
                        var ca = CellCorners[a];
                        var cb = CellCorners[b];

                        var da = brick.Sample(x + ca.X, y + ca.Y, z + ca.Z);
                        var db = brick.Sample(x + cb.X, y + cb.Y, z + cb.Z);

                        if (!TryCrossing(da, db, out var t))
                            continue;

                        sum += new float3(
                            ca.X + (cb.X - ca.X) * t,
                            ca.Y + (cb.Y - ca.Y) * t,
                            ca.Z + (cb.Z - ca.Z) * t
                        );

                        crossings++;
                    }

                    if (crossings == 0)
                        continue;

                    // The centroid is of the crossings within this cell, so it lands in [0,1]³. The cell's own
                    // lattice origin has to be added to put the vertex where the cell actually is: without
                    // this the whole brick's surface collapses into a one-voxel patch near its minimum
                    // corner, and the world draws as scattered specks instead of terrain.
                    var local = sum / crossings + new float3(x, y, z);
                    var normal = Normalise(brick.Gradient(x, y, z));

                    var vertex = mesh.AddVertex(local.X, local.Y, local.Z, normal.X, normal.Y, normal.Z);

                    _vertexOfCell[(cz * cellCount + cy) * cellCount + cx] = vertex;
                }
            }
        }

        return mesh.VertexCount > 0;
    }

    private void ExtractQuads(SdfBrick brick, BrickMesh mesh, int cellCount)
    {
        // Allocated once rather than inside the loop; the loop runs once per cell and a stack allocation per
        // iteration is how a deep brick turns into a stack overflow.
        Span<int> vertices = stackalloc int[4];

        // Pass two: one quad per grid edge the surface crosses, joining the four cells that share it. A quad
        // whose four cells are not all in the cell range belongs to a neighbouring brick, which will emit it
        // from its own side, so it is skipped rather than half-written.
        for (var axis = 0; axis < 3; axis++)
        {
            var ring = EdgeRings[axis];
            var step = EdgeSteps[axis];

            for (var cz = 0; cz < cellCount; cz++)
            {
                for (var cy = 0; cy < cellCount; cy++)
                {
                    for (var cx = 0; cx < cellCount; cx++)
                    {
                        var cell = new int3(cx - 1, cy - 1, cz - 1);

                        var a = brick.Sample(cell.X, cell.Y, cell.Z);
                        var b = brick.Sample(cell.X + step.X, cell.Y + step.Y, cell.Z + step.Z);

                        if (!TryCrossing(a, b, out _))
                            continue;

                        var complete = true;
                        for (var i = 0; i < 4; i++)
                        {
                            var neighbour = cell + ring[i];

                            if (!InCellRange(neighbour, cellCount))
                            {
                                complete = false;
                                break;
                            }

                            var vertex = _vertexOfCell[
                                ((neighbour.Z + 1) * cellCount + neighbour.Y + 1) * cellCount + neighbour.X + 1
                            ];

                            if (vertex < 0)
                            {
                                complete = false;
                                break;
                            }

                            vertices[i] = vertex;
                        }

                        if (!complete)
                            continue;

                        // The ring is ordered anticlockwise about the edge's positive direction, so the
                        // winding only flips when the field falls along the edge rather than rising. Pinned by
                        // SurfaceNetWindingPlaneTests, which extracts a known horizontal plane and asserts that
                        // every triangle's geometric normal points up — outward, away from the solid.
                        if (a > b)
                        {
                            mesh.AddTriangle(vertices[0], vertices[1], vertices[2]);
                            mesh.AddTriangle(vertices[0], vertices[2], vertices[3]);
                        }
                        else
                        {
                            mesh.AddTriangle(vertices[0], vertices[3], vertices[2]);
                            mesh.AddTriangle(vertices[0], vertices[2], vertices[1]);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Places the crossing on the edge from <paramref name="a"/> to <paramref name="b"/>.
    /// </summary>
    /// <remarks>
    /// The sign test is <see cref="SdfBrick.HasCrossing"/>, so the interpolation and the crossing test agree
    /// by construction about which side a sample of exactly zero is on.
    /// </remarks>
    private static bool TryCrossing(float a, float b, out float t)
    {
        if (!SdfBrick.HasCrossing(a, b))
        {
            t = 0.0f;
            return false;
        }

        var denominator = a - b;
        t = MathF.Abs(denominator) <= 1.0e-12f ? 0.5f : a / denominator;

        return true;
    }

    private static bool InCellRange(int3 cell, int cellCount) =>
        cell.X >= -1 && cell.X < cellCount - 1
        && cell.Y >= -1 && cell.Y < cellCount - 1
        && cell.Z >= -1 && cell.Z < cellCount - 1;

    private static float3 Normalise(float3 value)
    {
        var length = MathF.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
        if (length <= 1.0e-12f)
            return float3.Zero;

        return new float3(value.X / length, value.Y / length, value.Z / length);
    }
}
