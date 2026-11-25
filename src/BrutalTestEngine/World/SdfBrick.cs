using Brutal.Numerics;

namespace BrutalTestEngine.World;

/// <summary>
/// The signed distance field samples for one brick, including a one-voxel ring of neighbour data.
/// </summary>
/// <remarks>
/// <para>
/// The block holds <c>(n + 2)³</c> samples for a brick of <c>n³</c> cells: the brick itself plus one extra
/// voxel on the negative and positive side of every axis. That ring is not padding for safety, it is what
/// makes the extracted mesh watertight. A surface extraction pass emits a polygon per grid edge the surface
/// crosses, and the polygon's corners are the dual vertices of the four cells around that edge. For an edge
/// on the brick's own boundary, two of those cells lie in the neighbouring brick — so without the ring
/// those quads would be missing and every brick boundary would show as a crack.
/// </para>
/// <para>
/// Samples are distances in world units, not normalised. Distance in metres keeps the field meaningful
/// when it is sampled, filtered, blended or marched, and lets a ray march step by the value it read without
/// a conversion — which is the whole reason an SDF is useful for more than meshing.
/// </para>
/// <para>
/// Block coordinates are indexed so that sample <c>(0,0,0)</c> sits one voxel before the brick's minimum
/// corner. The brick's own cells then occupy block coordinates <c>[0, n-1]</c> and its own samples
/// <c>[1, n]</c>. The signed <see cref="Sample"/> accessor accepts <c>[-1, n]</c> so an extraction pass
/// never has to think about the offset.
/// </para>
/// </remarks>
public sealed class SdfBrick
{
    private readonly float[] _samples;
    private readonly int _stride;

    /// <summary>Creates a brick block with every sample set to <paramref name="initialValue"/>.</summary>
    /// <param name="voxelCount">Number of cells along each axis of the brick.</param>
    /// <param name="initialValue">
    /// The initial distance. The default is <see cref="EmptyDistance"/>, meaning "solid air", so an
    /// unevaluated brick produces no geometry rather than a block of noise at the world origin.
    /// </param>
    public SdfBrick(int voxelCount, float initialValue = EmptyDistance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(voxelCount, 1);

        VoxelCount = voxelCount;
        _stride = voxelCount + 2;

        _samples = new float[_stride * _stride * _stride];

        // float.MaxValue would overflow the interpolation differences a surface pass computes, so the
        // "empty" value is a large but finite distance.
        _samples.AsSpan().Fill(Math.Min(initialValue, EmptyDistance));
    }

    /// <summary>The distance a brick's samples start at when it has not been evaluated.</summary>
    public const float EmptyDistance = 1.0e6f;

    /// <summary>Number of cells along each axis of the brick that this brick owns.</summary>
    public int VoxelCount { get; }

    /// <summary>Number of samples along each axis, including the neighbour ring.</summary>
    public int SampleCount => _stride;

    /// <summary>Total number of samples in the block.</summary>
    public int SampleLength => _samples.Length;

    /// <summary>The block's samples, in block order, for upload to the GPU.</summary>
    public ReadOnlySpan<float> AsSpan() => _samples;

    /// <summary>The eight corners of a cell, as offsets from its minimum corner.</summary>
    public static readonly int3[] CellCorners =
    [
        new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0),
        new(0, 0, 1), new(1, 0, 1), new(0, 1, 1), new(1, 1, 1),
    ];

    /// <summary>
    /// The twelve edges of a cell, as pairs of corner indices into <see cref="CellCorners"/>.
    /// </summary>
    /// <remarks>
    /// The single source of truth for which edges a cell has. The extraction pass, the classification mask
    /// and the GLSL that mirrors them all have to agree on this list, so it is stated once here and the
    /// shader's copy is checked against <see cref="CellEdgeCount"/> and the corner table by a test.
    /// </remarks>
    public static readonly (int A, int B)[] CellEdgeCorners =
    [
        (0, 1), (2, 3), (4, 5), (6, 7), // along X
        (0, 2), (1, 3), (4, 6), (5, 7), // along Y
        (0, 4), (1, 5), (2, 6), (3, 7), // along Z
    ];

    /// <summary>Number of edges in a cell, and therefore the number of bits in a crossing mask.</summary>
    public const int CellEdgeCount = 12;

    /// <summary>Number of samples along each axis of the cell lattice a brick can place a vertex in.</summary>
    public int CellCount => VoxelCount + 1;

    /// <summary>
    /// A 12-bit mask of which of a cell's edges the surface crosses, one bit per entry in
    /// <see cref="CellEdgeCorners"/>.
    /// </summary>
    /// <remarks>
    /// This is the cheapest complete description of a cell: twelve bits say everything the quad pass needs
    /// to know about the cell's topology, and a mask is small enough that a compute shader can write one per
    /// cell and have the CPU reduce the whole brick without a read-back of the field. The GPU pass uses it
    /// for exactly that, and the CPU extraction is checked against it.
    /// </remarks>
    public int CellCrossingMask(int i, int j, int k)
    {
        var mask = 0;

        for (var edge = 0; edge < CellEdgeCorners.Length; edge++)
        {
            var (a, b) = CellEdgeCorners[edge];
            var ca = CellCorners[a];
            var cb = CellCorners[b];

            if (HasCrossing(Sample(i + ca.X, j + ca.Y, k + ca.Z), Sample(i + cb.X, j + cb.Y, k + cb.Z)))
                mask |= 1 << edge;
        }

        return mask;
    }

    /// <summary>
    /// The sample at a signed cell-lattice coordinate, accepting <c>[-1, n]</c> on each axis.
    /// </summary>
    public float Sample(int i, int j, int k) =>
        _samples[(i + 1) + _stride * ((j + 1) + _stride * (k + 1))];

    /// <summary>
    /// Writes the sample at a signed cell-lattice coordinate, accepting <c>[-1, n]</c> on each axis.
    /// </summary>
    public void SetSample(int i, int j, int k, float value) =>
        _samples[(i + 1) + _stride * ((j + 1) + _stride * (k + 1))] = value;

    /// <summary>
    /// The gradient of the field at a cell-lattice coordinate, by central differences on the block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An SDF's gradient is its normal, so the surface pass gets normals for free rather than averaging face
    /// normals afterwards. That is one of the concrete reasons to store distance rather than occupancy, and
    /// it is why a meshlet's normal cone can be computed without a second pass over the triangles.
    /// </para>
    /// <para>
    /// The stencil is clamped rather than allowed to walk off the block. A cell at the very edge of the
    /// extraction range asks for samples one past what the block holds, and clamping narrows the difference
    /// instead of throwing. The normal there is one-sided rather than central, which costs a little accuracy
    /// on the brick's own boundary and costs nothing at all in the interior — and the alternative, widening
    /// the block by another voxel on every side, would put 700 extra samples behind every brick to fix a
    /// handful of normals that the neighbouring brick computes properly anyway.
    /// </para>
    /// </remarks>
    public float3 Gradient(int i, int j, int k)
    {
        var last = VoxelCount;

        return new float3(
            Sample(Clamp(i + 1, last), j, k) - Sample(Clamp(i - 1, last), j, k),
            Sample(i, Clamp(j + 1, last), k) - Sample(i, Clamp(j - 1, last), k),
            Sample(i, j, Clamp(k + 1, last)) - Sample(i, j, Clamp(k - 1, last))
        );
    }

    private static int Clamp(int value, int last) => value < -1 ? -1 : value > last ? last : value;

    /// <summary>
    /// True when the field changes sign across any of the twelve edges of the cell whose minimum-corner
    /// lattice coordinate is <c>(i, j, k)</c>.
    /// </summary>
    public bool HasCrossing(int i, int j, int k)
    {
        var s0 = Sample(i, j, k);

        return HasCrossing(s0, Sample(i + 1, j, k))
            || HasCrossing(s0, Sample(i, j + 1, k))
            || HasCrossing(s0, Sample(i, j, k + 1))
            || HasCrossing(Sample(i + 1, j, k), Sample(i + 1, j + 1, k))
            || HasCrossing(Sample(i + 1, j, k), Sample(i + 1, j, k + 1))
            || HasCrossing(Sample(i, j + 1, k), Sample(i + 1, j + 1, k))
            || HasCrossing(Sample(i, j + 1, k), Sample(i, j + 1, k + 1))
            || HasCrossing(Sample(i, j, k + 1), Sample(i + 1, j, k + 1))
            || HasCrossing(Sample(i, j, k + 1), Sample(i, j + 1, k + 1))
            || HasCrossing(Sample(i + 1, j + 1, k), Sample(i + 1, j + 1, k + 1))
            || HasCrossing(Sample(i + 1, j, k + 1), Sample(i + 1, j + 1, k + 1))
            || HasCrossing(Sample(i, j + 1, k + 1), Sample(i + 1, j + 1, k + 1));
    }

    /// <summary>
    /// True when a sample pair straddles the surface, treating a sample of exactly zero as outside.
    /// </summary>
    /// <remarks>
    /// The tie-break for an exact zero is not an edge case to tolerate, it is the common case: terrain with a
    /// whole-metre voxel grid and integer sample positions lands on zero constantly. Counting zero as
    /// outside means the surface is owned by the cells below it, exactly once, so an exact hit produces one
    /// sheet of geometry instead of none or two.
    /// </remarks>
    public static bool HasCrossing(float a, float b) => a < 0.0f != b < 0.0f;

    /// <summary>
    /// Fills the brick and its neighbour ring by evaluating <paramref name="field"/> at every sample's
    /// world position.
    /// </summary>
    /// <param name="field">The distance function, in world space.</param>
    /// <param name="origin">World-space minimum corner of the brick itself.</param>
    /// <param name="voxelSize">World-space size of one voxel, the sample spacing.</param>
    public void Sample(Func<float3, float> field, float3 origin, float voxelSize)
    {
        ArgumentNullException.ThrowIfNull(field);

        var stride = _stride;

        for (var z = 0; z < stride; z++)
        {
            var worldZ = origin.Z + (z - 1) * voxelSize;

            for (var y = 0; y < stride; y++)
            {
                var worldY = origin.Y + (y - 1) * voxelSize;
                var row = (z * stride + y) * stride;

                for (var x = 0; x < stride; x++)
                {
                    var world = new float3(origin.X + (x - 1) * voxelSize, worldY, worldZ);
                    _samples[row + x] = field(world);
                }
            }
        }
    }

    /// <summary>True when every sample is positive, so the brick contains no surface.</summary>
    /// <remarks>
    /// The check that decides whether a brick is allocated at all. A brick of air and a brick of solid rock
    /// both fail it, and neither needs a slab of memory, a meshlet or a slot in the pool.
    /// </remarks>
    public bool IsEmpty()
    {
        foreach (var sample in _samples)
        {
            if (sample <= 0.0f)
                return false;
        }

        return true;
    }
}
