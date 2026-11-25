using Brutal.Numerics;

namespace BrutalTestEngine.World;

/// <summary>
/// One ring of the SDF clipmap: a brick grid of uniform resolution, anchored by the global lattice
/// coordinate of its minimum brick.
/// </summary>
/// <remarks>
/// <para>
/// The level does not move continuously with the camera. It is re-anchored in whole bricks, and only when
/// the camera has crossed a brick boundary. That quantisation is the whole point: the brick under a given
/// piece of terrain keeps the same key frame to frame, so a brick that was meshed last frame is not
/// discarded because the camera shifted by a centimetre. Without the snap, nothing would ever be cacheable
/// and the whole design collapses back to re-meshing every frame.
/// </para>
/// <para>
/// The anchor is stored as a brick lattice coordinate rather than as a world-space position, so all of the
/// addressing arithmetic is exact integer work. Only the final conversion to metres — for the bounding box
/// and the vertex positions — touches floating point, and it does so in double precision so a far-out level
/// does not lose its origin to rounding.
/// </para>
/// <para>
/// Which level owns a given brick is decided by <see cref="SdfClipmap.SelectLevel"/>, not here. A level
/// knows its own geometry; the selection rule is a relationship between levels and belongs to the
/// hierarchy.
/// </para>
/// </remarks>
public readonly struct ClipmapLevel
{
    /// <summary>Index of this level in the hierarchy, zero being the finest.</summary>
    public int Index { get; }

    /// <summary>World-space size of one brick at this level, in metres.</summary>
    public float BrickSize { get; }

    /// <summary>World-space size of one voxel at this level, in metres.</summary>
    public float VoxelSize { get; }

    /// <summary>Number of voxels along each axis of a brick at this level.</summary>
    public int VoxelCount { get; }

    /// <summary>Number of bricks along each axis of this level's grid.</summary>
    public int BricksPerAxis { get; }

    /// <summary>
    /// Lattice coordinate, in bricks, of the grid cube's minimum brick along each axis.
    /// </summary>
    /// <remarks>
    /// This is the anchor the camera snap produced. It can legitimately be negative or enormous, because
    /// the world is treated as unbounded; the voxel values are stored brick-local so the absolute position
    /// never has to survive a float.
    /// </remarks>
    public int3 OriginBrick { get; }

    /// <summary>World-space size of this level's grid cube along one axis, in metres.</summary>
    public float Extent => BrickSize * BricksPerAxis;

    /// <summary>Half of <see cref="Extent"/>, the distance from the cube's centre to its face.</summary>
    public float HalfExtent => Extent * 0.5f;

    /// <summary>World-space minimum corner of this level's grid cube.</summary>
    public float3 Min { get; }

    /// <summary>World-space centre of this level's grid cube.</summary>
    public float3 Center { get; }

    internal ClipmapLevel(int index, WorldSettings settings, int3 originBrick)
    {
        Index = index;
        BrickSize = settings.BrickSize(index);
        VoxelSize = settings.VoxelSize(index);
        VoxelCount = settings.BrickVoxelCount;
        BricksPerAxis = settings.BricksPerLevelAxis;
        OriginBrick = originBrick;

        Min = ToWorld(originBrick, BrickSize);
        Center = new float3(Min.X + HalfExtent, Min.Y + HalfExtent, Min.Z + HalfExtent);
    }

    /// <summary>
    /// The lattice coordinate of the brick containing a world-space point, in the level's global lattice.
    /// </summary>
    public int3 BrickKeyAt(float3 worldPosition) =>
        new(
            LatticeCoordinate(worldPosition.X, BrickSize),
            LatticeCoordinate(worldPosition.Y, BrickSize),
            LatticeCoordinate(worldPosition.Z, BrickSize)
        );

    /// <summary>World-space minimum corner of a brick, given its lattice coordinate.</summary>
    public float3 BrickOrigin(int3 brickKey) => ToWorld(brickKey, BrickSize);

    /// <summary>
    /// Half a brick, the offset that turns a brick's minimum corner into its centre.
    /// </summary>
    /// <remarks>
    /// The centre rather than the corner is what decides ownership, because a corner sits exactly on the
    /// boundary between bricks and would be resolved inconsistently by the level that excludes its own
    /// minimum face.
    /// </remarks>
    public float3 HalfBrick => new(BrickSize * 0.5f, BrickSize * 0.5f, BrickSize * 0.5f);

    /// <summary>
    /// The slot index of a brick within this level's grid, in row-major order.
    /// </summary>
    /// <remarks>
    /// The slot is derived by subtracting the anchor and masking, not by taking the lattice coordinate
    /// modulo the grid size. The subtraction is what keeps the mapping stable: two bricks a full grid apart
    /// alias onto the same slot, which is the intended behaviour for a level whose resolution cannot
    /// resolve that distance anyway. The aliasing is resolved by the neighbouring levels, which do cover
    /// it at a finer resolution.
    /// </remarks>
    public int SlotIndex(int3 brickKey)
    {
        var mask = BricksPerAxis - 1;

        var x = (brickKey.X - OriginBrick.X) & mask;
        var y = (brickKey.Y - OriginBrick.Y) & mask;
        var z = (brickKey.Z - OriginBrick.Z) & mask;

        return (z * BricksPerAxis + y) * BricksPerAxis + x;
    }

    /// <summary>
    /// The lattice coordinate of a slot, the inverse of <see cref="SlotIndex"/> up to the grid period.
    /// </summary>
    public int3 SlotBrickKey(int slotIndex)
    {
        var axis = BricksPerAxis;
        var plane = axis * axis;

        return new int3(
            OriginBrick.X + slotIndex % axis,
            OriginBrick.Y + slotIndex / axis % axis,
            OriginBrick.Z + slotIndex / plane
        );
    }

    /// <summary>True when a world-space point lies inside this level's grid cube.</summary>
    public bool Contains(float3 worldPosition) =>
        worldPosition.X >= Min.X
        && worldPosition.X < Min.X + Extent
        && worldPosition.Y >= Min.Y
        && worldPosition.Y < Min.Y + Extent
        && worldPosition.Z >= Min.Z
        && worldPosition.Z < Min.Z + Extent;

    /// <summary>
    /// The anchor that puts <paramref name="center"/> at the middle of the grid cube, snapped outwards to
    /// a whole brick.
    /// </summary>
    /// <remarks>
    /// Snapping outwards rather than to the nearest brick means the cube always covers at least the half
    /// extent the camera asked for, instead of oscillating between covering slightly less and slightly more
    /// as the camera drifts across a boundary.
    /// </remarks>
    internal static int3 SnapAnchor(float3 center, WorldSettings settings, int level)
    {
        var brickSize = settings.BrickSize(level);
        var halfBricks = settings.BricksPerLevelAxis / 2;

        return new int3(
            LatticeCoordinate(center.X, brickSize) - halfBricks,
            LatticeCoordinate(center.Y, brickSize) - halfBricks,
            LatticeCoordinate(center.Z, brickSize) - halfBricks
        );
    }

    private static int LatticeCoordinate(float value, float brickSize) =>
        (int)Math.Floor((double)value / brickSize);

    private static float3 ToWorld(int3 brickKey, float brickSize) =>
        new(
            (float)(brickKey.X * (double)brickSize),
            (float)(brickKey.Y * (double)brickSize),
            (float)(brickKey.Z * (double)brickSize)
        );
}
