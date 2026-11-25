using Brutal.Numerics;
using BrutalTestEngine.Procedural.Noise;

namespace BrutalTestEngine.World;

/// <summary>
/// The world's terrain, as a continuous signed distance function evaluated anywhere in space.
/// </summary>
/// <remarks>
/// <para>
/// The field is the definition of the world; the brick clipmap is a cache of it. That ordering matters,
/// because it means terrain exists everywhere without being stored anywhere, and the clipmap's job is
/// purely to decide how finely to resolve it near the camera. There is no origin, no edge and no seam: the
/// clipmap can be centred anywhere and the terrain is continuous across the boundary, because both sides
/// are evaluating the same function.
/// </para>
/// <para>
/// Heights come from two octaves of value noise at different scales. Two is deliberate — enough to break up
/// the flatness of a single octave without the cost of a full fractal sum, which will be the wrong tool
/// once real terrain generation lands. This exists so the pipeline has something worth meshing.
/// </para>
/// <para>
/// The height cache is the only mutable state, and it exists because a brick's samples are evaluated in
/// X-major order: the noise is sampled once per <c>(x, z)</c> column rather than once per sample. For an
/// <c>8³</c> brick that is 729 samples collapsing to 100 noise evaluations, which is the difference
/// between a background job and a stall.
/// </para>
/// <para>
/// The field holds no mutable state at all, which is what makes it safe to evaluate from any number of
/// threads at once with no locking. That is not incidental: brick building runs on its own thread precisely
/// because the field imposes nothing to contend over.
/// </para>
/// </remarks>
public sealed class TerrainField
{
    /// <summary>The base frequency of the terrain's finest octave, in cycles per metre.</summary>
    private const float BaseFrequency = 0.004f;

    private const int Octaves = 5;
    private const float Lacunarity = 2.0f;
    private const float Gain = 0.5f;

    /// <summary>Metres of height the noise is scaled to.</summary>
    private const float HeightScale = 120.0f;

    /// <summary>Metres added to the noise so the terrain sits above sea level.</summary>
    private const float HeightOffset = 20.0f;

    private readonly FastNoiseLite _detail;

    /// <summary>Creates a terrain field with the given seed.</summary>
    public TerrainField(int seed = 1337)
    {
        _detail = new FastNoiseLite(seed);
        _detail.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        _detail.SetFrequency(BaseFrequency);
        _detail.SetFractalType(FastNoiseLite.FractalType.FBm);
        _detail.SetFractalOctaves(Octaves);
        _detail.SetFractalLacunarity(Lacunarity);
        _detail.SetFractalGain(Gain);

        // The fractal sum is normalised by the total amplitude of its octaves, so the noise itself stays
        // within [-1, 1] no matter how many octaves are configured. That normalisation is what makes the
        // bounds below derivable rather than empirical, which matters because the streamer's surface test
        // is only conservative if the bound it uses is a real bound.
        ShortestWavelength = 1.0f / (BaseFrequency * MathF.Pow(Lacunarity, Octaves - 1));
    }

    /// <summary>The height of the terrain at a horizontal position, in metres.</summary>
    public float HeightAt(float x, float z) => _detail.GetNoise(x, z) * HeightScale + HeightOffset;

    /// <summary>
    /// The lowest height the terrain function can return, in metres.
    /// </summary>
    /// <remarks>
    /// A real bound rather than a measured minimum, so the streamer can decide that a brick is entirely air
    /// without sampling the terrain at all. Sampling would only give the minimum over the samples taken, and
    /// rejecting a brick on that basis is how a world acquires holes.
    /// </remarks>
    public float MinimumHeight => HeightOffset - HeightScale;

    /// <summary>The highest height the terrain function can return, in metres.</summary>
    public float MaximumHeight => HeightOffset + HeightScale;

    /// <summary>The total vertical range the terrain can occupy, in metres.</summary>
    public float HeightRange => MaximumHeight - MinimumHeight;

    /// <summary>
    /// The wavelength of the terrain's finest octave, in metres, which is the shortest distance over which
    /// the height can change appreciably.
    /// </summary>
    /// <remarks>
    /// This is what makes the streamer's surface test sound. Two samples closer together than the shortest
    /// wavelength cannot differ by the full range, so the bound on what lies between them is tighter than
    /// the global one — and that tightening is exactly what lets a fine level reject the many bricks that
    /// hold only air or only rock.
    /// </remarks>
    public float ShortestWavelength { get; }

    /// <summary>
    /// The world's terrain field, the single definition every subsystem samples.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One instance, created from the settings' seed, because the terrain function has to be identical in
    /// every place that samples it: the workers that mesh it, the streamer that decides which bricks are
    /// near the surface, and the debug UI that reports a height. Three copies of the same formula drift the
    /// moment one of them is tuned, and a drifted copy produces terrain that is subtly wrong in a way no
    /// test can catch without knowing which copy is authoritative.
    /// </para>
    /// <para>
    /// Sharing it across threads is safe because the field holds no mutable state. The only mutable buffer
    /// in the pipeline is the per-call height scratch, which the caller supplies precisely so that two
    /// threads cannot contend over it.
    /// </para>
    /// </remarks>
    public static TerrainField ForSettings(WorldSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new TerrainField(settings.Seed);
    }

    /// <summary>The signed distance to the terrain surface at a world position, in metres.</summary>
    /// <remarks>
    /// Positive above the surface, negative below. A plain vertical difference is not a true Euclidean
    /// distance — it overestimates near a steep slope — but it is what every voxel terrain uses, because a
    /// true distance would mean a search per sample. The consequence is that the extracted surface shifts
    /// slightly on steep ground, and a carve tool's reach is a vertical measure rather than a spherical one.
    /// Both are acceptable for terrain, and both are worth knowing before a gameplay feature relies on them.
    /// </remarks>
    public float Distance(float3 position) => position.Y - HeightAt(position.X, position.Z);

    /// <summary>
    /// The number of floats a height scratch must hold for a brick of <paramref name="voxelCount"/>.
    /// </summary>
    /// <remarks>
    /// The stride is the sample count, not the voxel count: the scratch covers the brick's expanded footprint
    /// including the neighbour ring, so it is one voxel wider on every side. Deriving it here rather than
    /// hardcoding it is what keeps a brick of 4 or 16 voxels working — a fixed stride silently reads the
    /// scratch out of position, and then out of bounds, as soon as the brick size changes.
    /// </remarks>
    public static int HeightScratchLength(int voxelCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(voxelCount, 1);

        var stride = voxelCount + 2;

        return stride * stride;
    }

    /// <summary>
    /// Fills <paramref name="scratch"/> with the terrain height over a brick's expanded footprint, so a
    /// single noise evaluation serves every sample in that column.
    /// </summary>
    /// <param name="scratch">
    /// A buffer of at least <see cref="HeightScratchLength"/> floats for the same brick shape, owned by the
    /// caller.
    /// </param>
    /// <param name="origin">World-space minimum corner of the brick. The scratch starts one voxel before it.</param>
    /// <param name="voxelSize">World-space size of one voxel.</param>
    /// <remarks>
    /// <para>
    /// The buffer is supplied rather than held, because a shared one would make the field stateful and the
    /// whole point of it being stateless is that several bricks can be built concurrently. A brick's samples
    /// are evaluated in X-major order, so caching the height per column turns a brick's <c>(n+2)³</c> noise
    /// evaluations into <c>(n+2)²</c> — for an <c>8³</c> brick, a thousand evaluations collapse to a hundred.
    /// </para>
    /// <para>
    /// The scratch is indexed <c>z * stride + x</c> with the stride taken from the buffer's own length, so the
    /// caller's brick shape decides the layout and there is no second constant to keep in step.
    /// </para>
    /// </remarks>
    public void BuildHeightScratch(Span<float> scratch, float3 origin, float voxelSize)
    {
        var stride = ScratchStride(scratch.Length);
        var required = stride * stride;

        if (scratch.Length < required)
        {
            throw new ArgumentException(
                $"The height scratch buffer must hold at least {required} floats for a stride of {stride}.",
                nameof(scratch)
            );
        }

        for (var z = 0; z < stride; z++)
        {
            var worldZ = origin.Z + (z - 1) * voxelSize;
            var row = z * stride;

            for (var x = 0; x < stride; x++)
                scratch[row + x] = HeightAt(origin.X + (x - 1) * voxelSize, worldZ);
        }
    }

    /// <summary>
    /// The row stride a height scratch of <paramref name="length"/> floats uses.
    /// </summary>
    /// <remarks>
    /// Derived by inverting the length rather than passed in, so a caller cannot supply a buffer and a stride
    /// that disagree — which is precisely the bug this replaced.
    /// </remarks>
    public static int ScratchStride(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

        var stride = (int)Math.Sqrt(length);

        if (stride * stride != length)
        {
            throw new ArgumentException(
                $"A height scratch must be square; {length} floats is not.",
                nameof(length)
            );
        }

        return stride;
    }
}

