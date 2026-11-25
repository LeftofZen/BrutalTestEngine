namespace BrutalTestEngine.World;

/// <summary>
/// The hardware budgets a meshlet must fit inside, and the geometry bounds they imply for a brick.
/// </summary>
/// <remarks>
/// <para>
/// The 64-vertex and 124-triangle limits are what a mesh shader workgroup advertises on the Turing/Ada and
/// RDNA families; a meshlet that exceeds them cannot be dispatched. They constrain how a brick's geometry
/// has to be cut, not how big a brick can be: an 8³ brick has up to 729 candidate cells, so one brick is
/// <em>several</em> meshlets. The premise that holds is that a brick maps to a small, bounded number of
/// meshlets, and the split is mechanical.
/// </para>
/// <para>
/// The budgets are stated below the hardware ceiling rather than at it. A meshlet that exactly fills the
/// vertex buffer is legal but fragile — the closing vertex of the last triangle routinely needs the slot a
/// previous one just freed — so the splitter plans against a little headroom.
/// </para>
/// </remarks>
public static class BrickShape
{
    /// <summary>Maximum vertices a single meshlet may emit, per the hardware.</summary>
    public const int MaxMeshletVertices = 64;

    /// <summary>Maximum primitives a single meshlet may emit, per the hardware.</summary>
    public const int MaxMeshletTriangles = 124;

    /// <summary>The vertex budget the splitter plans against, below the hardware ceiling.</summary>
    public const int MeshletVertexBudget = MaxMeshletVertices - 4;

    /// <summary>The triangle budget the splitter plans against, below the hardware ceiling.</summary>
    public const int MeshletTriangleBudget = MaxMeshletTriangles - 4;

    /// <summary>
    /// The most vertices a brick's extraction can emit, including the neighbour ring.
    /// </summary>
    /// <remarks>
    /// One vertex per cell in <c>[-1, n-1]³</c>, which is the cell range the brick must be able to place a
    /// vertex in. It has to reach one cell below its own minimum so its negative faces can be meshed by the
    /// brick that owns them rather than by both bricks at once: a shared face is emitted exactly once, by
    /// the brick on its negative side, and never duplicated. The positive faces are the neighbour's
    /// problem, and the neighbour has the ring data to handle them.
    /// </remarks>
    public static int MaxVerticesForBrick(int voxelCount)
    {
        var cells = voxelCount + 1;

        return cells * cells * cells;
    }

    /// <summary>
    /// The most triangles a brick's extraction can emit: two per grid edge the surface may cross.
    /// </summary>
    /// <remarks>
    /// A brick has <c>n(n+1)²</c> grid edges per axis and three axes, and each crossed edge becomes a quad.
    /// This is a real worst case rather than a guess, which is what lets the mesh's buffers be sized once
    /// instead of grown.
    /// </remarks>
    public static int MaxTrianglesForBrick(int voxelCount) =>
        6 * voxelCount * (voxelCount + 1) * (voxelCount + 1);

    /// <summary>
    /// The largest number of meshlets one brick can be split into, given the vertex and triangle budgets.
    /// </summary>
    /// <remarks>
    /// This is the number the GPU's meshlet buffer is sized from. It is an upper bound rather than an
    /// expectation: a brick containing only a flat piece of ground produces far fewer, and a brick
    /// containing nothing at all produces none.
    /// </remarks>
    public static int MaxMeshletsForBrick(int voxelCount)
    {
        var byVertices = DivideRoundUp(MaxVerticesForBrick(voxelCount), MeshletVertexBudget);
        var byTriangles = DivideRoundUp(MaxTrianglesForBrick(voxelCount), MeshletTriangleBudget);

        return Math.Max(byVertices, byTriangles);
    }

    /// <summary>True when a mesh of the given size is within the hardware budgets.</summary>
    public static bool Allows(int vertexCount, int triangleCount) =>
        vertexCount is >= 0 and <= MaxMeshletVertices
        && triangleCount is >= 0 and <= MaxMeshletTriangles;

    /// <summary>The number of groups needed to cover <paramref name="count"/> at <paramref name="groupSize"/>.</summary>
    public static int DivideRoundUp(int count, int groupSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(groupSize, 1);

        return (count + groupSize - 1) / groupSize;
    }
}
