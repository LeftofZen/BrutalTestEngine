using Brutal.Numerics;

namespace BrutalTestEngine.World;

/// <summary>
/// A brick's identity within the clipmap: which level owns it, and where it sits in that level's lattice.
/// </summary>
/// <remarks>
/// <para>
/// This is the key the mesh cache is indexed by, so it has to be a value type with structural equality and
/// cheap hashing — it is looked up once per brick per frame, and a reference type here would mean an
/// allocation and a pointer chase in the middle of the render thread's hot loop.
/// </para>
/// <para>
/// The level is part of the identity on purpose. The same lattice coordinate at two levels is two
/// different bricks with two different voxel resolutions, and they must not collide in the cache.
/// </para>
/// </remarks>
/// <param name="Level">Index of the clipmap level that owns the brick.</param>
/// <param name="Lattice">The brick's coordinate in that level's global lattice, in bricks.</param>
public readonly record struct BrickKey(int Level, int3 Lattice)
{
    /// <summary>World-space minimum corner of this brick.</summary>
    public float3 Origin(WorldSettings settings)
    {
        var brickSize = settings.BrickSize(Level);

        return new float3(
            (float)(Lattice.X * (double)brickSize),
            (float)(Lattice.Y * (double)brickSize),
            (float)(Lattice.Z * (double)brickSize)
        );
    }

    /// <summary>World-space centre of this brick.</summary>
    public float3 Center(WorldSettings settings)
    {
        var half = settings.BrickSize(Level) * 0.5f;
        var origin = Origin(settings);

        return new float3(origin.X + half, origin.Y + half, origin.Z + half);
    }

    public override string ToString() => $"L{Level}({Lattice.X},{Lattice.Y},{Lattice.Z})";
}
