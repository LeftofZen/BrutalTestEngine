using Brutal.Numerics;
using System.Runtime.InteropServices;

namespace BrutalTestEngine.World;

/// <summary>
/// The CPU side of the world pipeline: builds a brick's surface data, extracts its geometry and splits it
/// into meshlets, writing the result into pools that are uploaded to the GPU.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that turns a clipmap slot into GPU-ready bytes. It owns every intermediate buffer —
/// the field samples, the extraction scratch, the vertex and index pools — and reuses all of them across
/// bricks, so a steady-state stream of brick builds allocates nothing.
/// </para>
/// <para>
/// It is deliberately single-threaded and internal-buffered rather than static. Bricks are built on the IO
/// thread precisely so the render thread never waits for one, and a builder that owned no state would not
/// be able to be handed one brick at a time from a queue. If brick building later moves to a pool, each
/// worker gets its own instance and no locking is needed.
/// </para>
/// <para>
/// The pipeline it runs mirrors one for one what the compute shader will do: sample the field into a
/// block, extract the surface, split it into meshlets. Keeping the CPU version structurally identical to
/// the intended GPU version is what makes the GPU version testable against it — the same discipline the
/// compute self-test already applies to the field step.
/// </para>
/// </remarks>
public sealed class SdfBrickStore : IDisposable
{
    private readonly TerrainField _field;
    private readonly SurfaceNetExtractor _extractor;
    private readonly SdfBrick _samples;
    private readonly BrickMesh _mesh;
    private readonly float[] _heightScratch;
    private readonly int _heightScratchStride;
    private readonly List<Meshlet> _meshlets = [];

    private bool _disposed;

    /// <summary>Creates a store that builds bricks of the shape described by <paramref name="settings"/>.</summary>
    public SdfBrickStore(TerrainField field, WorldSettings settings)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        _field = field;
        Settings = settings;

        _samples = new SdfBrick(settings.BrickVoxelCount);
        _mesh = new BrickMesh(settings.BrickVoxelCount);
        _extractor = new SurfaceNetExtractor(settings.BrickVoxelCount);

        // Sized from the configured brick shape rather than a constant, so a host that changes the brick size
        // gets a scratch long enough for it.
        _heightScratch = new float[TerrainField.HeightScratchLength(settings.BrickVoxelCount)];
        _heightScratchStride = settings.BrickVoxelCount + 2;

        var maxMeshlets = settings.BricksPerLevel * BrickShape.MaxMeshletsForBrick(settings.BrickVoxelCount);

        VertexPool = new byte[maxMeshlets * MeshletBuilder.VertexStride * Meshlet.BytesPerVertex];
        IndexPool = new ushort[maxMeshlets * MeshletBuilder.IndexStride];
    }

    /// <summary>The settings the store was built for.</summary>
    public WorldSettings Settings { get; }

    /// <summary>The field the store samples.</summary>
    public TerrainField Field => _field;

    /// <summary>The shared vertex pool, in the layout the mesh shader expects.</summary>
    public byte[] VertexPool { get; }

    /// <summary>The shared index pool, holding meshlet-local indices.</summary>
    public ushort[] IndexPool { get; }

    /// <summary>Number of bricks the store has built since it was created.</summary>
    public long BricksBuilt { get; private set; }

    /// <summary>Number of bricks whose surface turned out to be empty and produced no geometry.</summary>
    public long EmptyBricks { get; private set; }

    /// <summary>Total number of meshlets produced across every brick built.</summary>
    public long MeshletsProduced { get; private set; }

    /// <summary>
    /// Builds a brick's geometry and returns its meshlets, or an empty span when the brick is empty.
    /// </summary>
    /// <param name="brick">The brick to build.</param>
    /// <param name="brickId">The physical brick id to stamp on each meshlet.</param>
    /// <remarks>
    /// The returned span aliases the store's internal list and is valid until the next call. That is the
    /// right contract here because the caller's job is to copy the meshlets into an upload buffer and drop
    /// the reference before building the next brick; handing out a fresh list per brick would allocate once
    /// per brick, which is exactly what this type exists to avoid.
    /// </remarks>
    public ReadOnlySpan<Meshlet> Build(BrickKey brick, int brickId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _meshlets.Clear();

        var level = brick.Level;
        var origin = brick.Origin(Settings);
        var voxelSize = Settings.VoxelSize(level);

        SampleBrick(origin, voxelSize);

        if (_samples.IsEmpty())
        {
            EmptyBricks++;
            BricksBuilt++;

            return [];
        }

        _extractor.Extract(_samples, _mesh);
        if (_mesh.IsEmpty)
        {
            EmptyBricks++;
            BricksBuilt++;

            return [];
        }

        var produced = MeshletBuilder.Build(
            _mesh,
            _meshlets,
            VertexPool,
            IndexPool,
            brickId,
            origin,
            voxelSize
        );

        MeshletsProduced += produced;
        BricksBuilt++;

        return CollectionsMarshal.AsSpan(_meshlets);
    }

    /// <summary>
    /// The number of meshlets a brick can produce, the stride the GPU's meshlet buffer must use.
    /// </summary>
    public static int MaxMeshletsPerBrick(WorldSettings settings) =>
        BrickShape.MaxMeshletsForBrick(settings.BrickVoxelCount);

    private void SampleBrick(float3 origin, float voxelSize)
    {
        // The height scratch covers the whole expanded footprint, so a brick's samples cost one noise
        // evaluation per column rather than one per sample.
        _field.BuildHeightScratch(_heightScratch, origin, voxelSize);

        var stride = _samples.SampleCount;

        for (var z = 0; z < stride; z++)
        {
            var row = z * _heightScratchStride;

            for (var x = 0; x < stride; x++)
            {
                var height = _heightScratch[row + x];

                for (var y = 0; y < stride; y++)
                {
                    var worldY = origin.Y + (y - 1) * voxelSize;
                    _samples.SetSample(x - 1, y - 1, z - 1, worldY - height);
                }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
