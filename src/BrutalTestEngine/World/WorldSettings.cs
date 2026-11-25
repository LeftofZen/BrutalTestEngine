namespace BrutalTestEngine.World;

/// <summary>
/// The static shape of the world: how finely the terrain is resolved near the camera and how far that
/// resolution reaches.
/// </summary>
/// <remarks>
/// <para>
/// A clipmap is a set of nested, concentric grids. Level 0 is the finest and covers the smallest area;
/// each level out halves in resolution and doubles in extent, so the outer levels cover a vast distance at
/// a resolution that is only good enough to be recognisable. The whole structure is what makes a seamless
/// open world affordable: memory is spent where the player is looking, and a hierarchy of bounding volumes
/// lets the far levels stay resident without being drawn in detail.
/// </para>
/// <para>
/// The alternative — one grid at the finest resolution all the way out — is not a trade-off, it is
/// geometrically impossible. Every rung out costs a factor of eight in voxels, so a modest 8 km view at
/// 0.5 m voxels is roughly eight billion voxels. The clipmap is the standard answer and the numbers here
/// are the reason it exists.
/// </para>
/// </remarks>
public sealed record WorldSettings
{
    /// <summary>The settings the engine ships with.</summary>
    public static WorldSettings Default { get; } = new();

    /// <summary>How many nested levels the clipmap has.</summary>
    /// <remarks>
    /// Nine levels at a base brick of eight metres reaches 4096 m. The level count and the base extent
    /// together decide the view distance, so they are stated together rather than as independent knobs.
    /// </remarks>
    public int LevelCount { get; init; } = 9;

    /// <summary>Number of bricks along each axis of a level's grid.</summary>
    /// <remarks>
    /// A power of two, because the level's grid is addressed by wrapping a world position into it, and that
    /// wrap is a mask rather than a modulo. Eight bricks per axis keeps a level's working set to 8³ = 512
    /// bricks, which is what makes a streamer pass over every level cheap enough to run every frame: the
    /// pass walks every slot of every level, so this number cubed times the level count is the pass's
    /// candidate count, and it is the dominant term in the streamer's cost.
    /// </remarks>
    public int BricksPerLevelAxis { get; init; } = 8;

    /// <summary>Number of voxels along each axis of a brick.</summary>
    /// <remarks>
    /// Eight voxels. The brick size is chosen by the meshlet budget, not by memory: a brick's extracted
    /// surface has to fit inside a single meshlet's vertex and triangle budget, and 8³ cells is the largest
    /// that reliably does. A larger brick would need two meshlets per brick and lose the direct index from
    /// brick to meshlet.
    /// </remarks>
    public int BrickVoxelCount { get; init; } = 8;

    /// <summary>The world-space size of the finest brick in the finest level, in metres.</summary>
    public float BaseBrickSize { get; init; } = 8.0f;

    /// <summary>
    /// The world-space size of one voxel in the finest level, in metres.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived from <see cref="BaseBrickSize"/> and <see cref="BrickVoxelCount"/> rather than stored, because
    /// a brick's voxels have to tile it exactly. Bricks sit on a lattice spaced by the brick size while their
    /// geometry only spans the voxel grid, so a voxel size that does not fill the brick leaves a gap between
    /// every pair of neighbours: the world draws as isolated islands of geometry instead of a surface.
    /// </para>
    /// <para>
    /// That failure is invisible to every test that inspects one brick at a time and invisible to every
    /// counter the engine reports — brick count, meshlet count, vertex count all look healthy. It shows up
    /// only on screen. Storing the two sizes independently made the bug reachable by a configuration change;
    /// deriving one from the other makes it unrepresentable.
    /// </para>
    /// </remarks>
    public float BaseVoxelSize => BaseBrickSize / BrickVoxelCount;

    /// <summary>
    /// The seed every deterministic piece of world generation is derived from.
    /// </summary>
    /// <remarks>
    /// Part of the settings rather than a parameter of the generator, because the world has to be
    /// reproducible across a server boundary: two machines meshing the same brick must produce the same
    /// bytes, or a player crossing a shard seam sees the terrain change shape. Every worker derives its
    /// noise from this one value, so a worker count cannot influence the world.
    /// </remarks>
    public int Seed { get; init; } = 1337;

    /// <summary>World-space size of a single brick at <paramref name="level"/>.</summary>
    public float BrickSize(int level) => BaseBrickSize * (1 << level);

    /// <summary>World-space size of one voxel at <paramref name="level"/>.</summary>
    public float VoxelSize(int level) => BaseVoxelSize * (1 << level);

    /// <summary>
    /// The world-space distance one level's grid covers along one axis, in metres.
    /// </summary>
    public float LevelExtent(int level) => BrickSize(level) * BricksPerLevelAxis;

    /// <summary>
    /// The distance from the camera at which the terrain defaults to the coarsest level, in metres.
    /// </summary>
    public float ViewDistance => LevelExtent(LevelCount - 1);

    /// <summary>Number of voxels in a single brick.</summary>
    public int VoxelsPerBrick => BrickVoxelCount * BrickVoxelCount * BrickVoxelCount;

    /// <summary>Number of bricks a level holds when every one of them intersects a surface.</summary>
    public int BricksPerLevel => BricksPerLevelAxis * BricksPerLevelAxis * BricksPerLevelAxis;

    /// <summary>
    /// The worst-case total brick count across every level, if every brick is allocated at once.
    /// </summary>
    /// <remarks>
    /// This is the number the GPU pool is sized from. It is a real bound rather than a guess: no level can
    /// hold more bricks than its grid has cells, so the sum is the exact ceiling on how many surface bricks
    /// the hierarchy can ever ask for.
    /// </remarks>
    public int MaxAllocatedBricks => BricksPerLevel * LevelCount;

    /// <summary>Worst-case voxel count across the whole clipmap, if fully allocated and non-empty.</summary>
    public long MaxAllocatedVoxels => (long)MaxAllocatedBricks * VoxelsPerBrick;

    /// <summary>Validates the settings, throwing on anything the clipmap's arithmetic cannot survive.</summary>
    public void Validate()
    {
        if (LevelCount < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LevelCount),
                LevelCount,
                "The clipmap needs at least a fine level and a coarse level."
            );
        }

        if (BricksPerLevelAxis < 2 || !int.IsPow2(BricksPerLevelAxis))
        {
            throw new ArgumentOutOfRangeException(
                nameof(BricksPerLevelAxis),
                BricksPerLevelAxis,
                "A level's brick count must be a power of two and at least two, because the wrap that "
                    + "maps a world position into a level is a mask."
            );
        }

        if (BrickVoxelCount is < 2 or > 16 || !int.IsPow2(BrickVoxelCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(BrickVoxelCount),
                BrickVoxelCount,
                "A brick's voxel count must be a power of two between 2 and 16. Beyond 16 the extracted "
                    + "surface no longer fits in one meshlet."
            );
        }

        if (BaseBrickSize <= 0.0f)
            throw new ArgumentOutOfRangeException(nameof(BaseBrickSize), BaseBrickSize, "Must be positive.");
    }
}
