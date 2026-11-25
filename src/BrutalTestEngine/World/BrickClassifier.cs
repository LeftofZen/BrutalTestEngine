using Brutal.Numerics;

namespace BrutalTestEngine.World;

/// <summary>
/// The CPU reference for the GPU brick-classification pass.
/// </summary>
/// <remarks>
/// <para>
/// The classification pass is the one piece of the world pipeline that runs on the GPU and produces a
/// result nothing else can check, because there is no read-back to inspect. So the same pass exists here in
/// managed code, over the same closed-form terrain, and a startup self-test compares the two per brick.
/// This is the same discipline the compute field step already uses: an abstraction that is verified rather
/// than merely compiled.
/// </para>
/// <para>
/// It is also the honest implementation. Terrain generation belongs on the GPU when it is millions of
/// samples a frame, but a terrain function that cannot be evaluated on the CPU cannot be tested, and an
/// untested terrain function is a world that does not line up with itself.
/// </para>
/// </remarks>
public static class BrickClassifier
{
    /// <summary>
    /// The terrain height the classification shader evaluates, in metres.
    /// </summary>
    /// <remarks>
    /// Three superposed sines in closed form. Every term is a function of world position alone, so a brick's
    /// field depends only on where the brick is and never on when it was evaluated — which is what makes the
    /// GPU result reproducible and comparable to this one.
    /// </remarks>
    public static float HeightAt(float x, float z) =>
        80.0f * MathF.Sin(x * 0.0125f) * MathF.Cos(z * 0.0115f)
        + 30.0f * MathF.Sin(x * 0.0310f) * MathF.Sin(z * 0.0270f)
        + 12.0f * MathF.Cos(x * 0.0700f) * MathF.Sin(z * 0.0650f);

    /// <summary>
    /// Counts the cells of a brick that the terrain surface passes through.
    /// </summary>
    /// <param name="brickLattice">
    /// The brick's lattice coordinate, relative to the level's origin as the shader receives it.
    /// </param>
    /// <param name="settings">The world's shape.</param>
    /// <param name="level">The clipmap level that owns the brick.</param>
    /// <returns>
    /// The number of cells with a crossed edge, or zero when the brick is entirely above or entirely below
    /// the surface.
    /// </returns>
    /// <remarks>
    /// A count rather than a boolean, because a pass that reports "the surface touches every brick" would
    /// still satisfy a boolean check while being useless for keeping empty bricks out of the pool — and the
    /// number of surface bricks is the entire reason the pool is affordable.
    /// </remarks>
    public static int CountCrossedCells(int3 brickLattice, WorldSettings settings, int level)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var voxels = settings.BrickVoxelCount;
        var samples = voxels + 2;
        var brickSize = settings.BrickSize(level);
        var voxelSize = settings.VoxelSize(level);

        // Single precision, in the same order as the shader: lattice times brick size, then the voxel offset,
        // then the level's own origin. The order matters, because that is what keeps the intermediate values
        // small enough to be exact.
        var brickOrigin = new float3(brickLattice.X * brickSize, brickLattice.Y * brickSize, brickLattice.Z * brickSize);

        var field = new float[samples * samples * samples];

        for (var z = 0; z < samples; z++)
        {
            for (var y = 0; y < samples; y++)
            {
                for (var x = 0; x < samples; x++)
                {
                    var worldX = brickOrigin.X + (x - 1) * voxelSize;
                    var worldY = brickOrigin.Y + (y - 1) * voxelSize;
                    var worldZ = brickOrigin.Z + (z - 1) * voxelSize;

                    field[(z * samples + y) * samples + x] = worldY - HeightAt(worldX, worldZ);
                }
            }
        }

        var crossed = 0;

        for (var z = 0; z < voxels; z++)
        {
            for (var y = 0; y < voxels; y++)
            {
                for (var x = 0; x < voxels; x++)
                {
                    if (CellHasCrossing(field, samples, x, y, z))
                        crossed++;
                }
            }
        }

        return crossed;
    }

    private static bool CellHasCrossing(float[] field, int samples, int x, int y, int z)
    {
        foreach (var (a, b) in SdfBrick.CellEdgeCorners)
        {
            var ca = SdfBrick.CellCorners[a];
            var cb = SdfBrick.CellCorners[b];

            var da = field[((z + ca.Z) * samples + y + ca.Y) * samples + x + ca.X];
            var db = field[((z + cb.Z) * samples + y + cb.Y) * samples + x + cb.X];

            if (SdfBrick.HasCrossing(da, db))
                return true;
        }

        return false;
    }
}
