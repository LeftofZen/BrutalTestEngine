namespace BrutalTestEngine.World;

/// <summary>
/// The result of extracting a brick's surface: the whole surface as one vertex list and one index list,
/// before it is cut into meshlets.
/// </summary>
/// <remarks>
/// <para>
/// A brick's surface is one mesh, not one meshlet. It is cut into meshlets afterwards by
/// <see cref="MeshletBuilder"/>, and the reason for two stages rather than one is that they answer
/// different questions. The extractor answers "where is the surface inside this brick"; the splitter
/// answers "how does that fit in a 64-vertex workgroup". Merging them would mean extraction had to be
/// budget-aware at every step, which makes it far harder to reason about and to test.
/// </para>
/// <para>
/// Vertex positions are stored as 8 bits per axis across an <em>expanded</em> local range of
/// <c>[-1, n]</c> — one voxel of ring on the negative side, and the brick's own far samples on the positive
/// side. The ring is why the range is a voxel wider than the brick: a boundary quad's corners include a
/// cell one step outside the brick, and that vertex has to be representable. The encoding is still three
/// bytes per vertex, and the mesh shader decodes it with one multiply-add against the brick's origin.
/// </para>
/// <para>
/// The normal is packed into a single byte as an octahedral coordinate. That format survives interpolation
/// well, decodes to a unit vector in a handful of instructions, and costs a quarter of what three floats
/// would. An SDF's gradient is its normal, so the normal is available analytically and there is no
/// normal-averaging pass and no smoothing group to respect.
/// </para>
/// </remarks>
public sealed class BrickMesh
{
    private readonly byte[] _positions;
    private readonly byte[] _normals;
    private readonly ushort[] _indices;

    /// <summary>Creates an empty mesh sized for a brick of <paramref name="voxelCount"/> voxels.</summary>
    public BrickMesh(int voxelCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(voxelCount, 1);

        VoxelCount = voxelCount;
        MaxVertices = BrickShape.MaxVerticesForBrick(voxelCount);
        MaxTriangles = BrickShape.MaxTrianglesForBrick(voxelCount);

        _positions = new byte[MaxVertices * 3];
        _normals = new byte[MaxVertices];
        _indices = new ushort[MaxTriangles * 3];
    }

    /// <summary>The brick's voxel count along each axis.</summary>
    public int VoxelCount { get; }

    /// <summary>
    /// The number of local units a quantised coordinate spans: one more than the voxel count, because of
    /// the ring voxel.
    /// </summary>
    public int LocalUnitCount => VoxelCount + 1;

    /// <summary>The greatest number of vertices this mesh can hold.</summary>
    public int MaxVertices { get; }

    /// <summary>The greatest number of triangles this mesh can hold.</summary>
    public int MaxTriangles { get; }

    /// <summary>Number of vertices emitted.</summary>
    public int VertexCount { get; private set; }

    /// <summary>Number of indices emitted. Always a multiple of three.</summary>
    public int IndexCount { get; private set; }

    /// <summary>Number of triangles emitted.</summary>
    public int TriangleCount => IndexCount / 3;

    /// <summary>True when the extraction produced no geometry.</summary>
    public bool IsEmpty => IndexCount == 0;

    /// <summary>Read-only view of the packed vertex positions, three bytes per vertex.</summary>
    public ReadOnlySpan<byte> Positions => _positions.AsSpan(0, VertexCount * 3);

    /// <summary>Read-only view of the packed octahedral normals, one byte per vertex.</summary>
    public ReadOnlySpan<byte> Normals => _normals.AsSpan(0, VertexCount);

    /// <summary>Read-only view of the indices into this mesh's own vertex list.</summary>
    public ReadOnlySpan<ushort> Indices => _indices.AsSpan(0, IndexCount);

    /// <summary>The packed position of a single vertex, as three bytes.</summary>
    public (byte X, byte Y, byte Z) PositionAt(int vertex) =>
        (_positions[vertex * 3], _positions[vertex * 3 + 1], _positions[vertex * 3 + 2]);

    /// <summary>The packed normal of a single vertex.</summary>
    public byte NormalAt(int vertex) => _normals[vertex];

    /// <summary>Removes every vertex and index, keeping the buffers.</summary>
    public void Clear()
    {
        VertexCount = 0;
        IndexCount = 0;
    }

    /// <summary>
    /// Appends a vertex in the brick's expanded local space, quantising it to a byte per axis.
    /// </summary>
    /// <param name="x">Local X in <c>[-1, VoxelCount]</c>.</param>
    /// <param name="y">Local Y in <c>[-1, VoxelCount]</c>.</param>
    /// <param name="z">Local Z in <c>[-1, VoxelCount]</c>.</param>
    /// <param name="normalX">X component of the surface normal at this vertex.</param>
    /// <param name="normalY">Y component of the surface normal at this vertex.</param>
    /// <param name="normalZ">Z component of the surface normal at this vertex.</param>
    /// <returns>The index of the appended vertex.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the mesh would exceed its vertex bound.</exception>
    public int AddVertex(
        float x,
        float y,
        float z,
        float normalX,
        float normalY,
        float normalZ
    )
    {
        if (VertexCount >= MaxVertices)
        {
            throw new InvalidOperationException(
                $"A brick of {VoxelCount} voxels may not exceed {MaxVertices} vertices."
            );
        }

        var index = VertexCount++;

        // The local range is [-1, VoxelCount] and spans LocalUnitCount units, so a coordinate is shifted by
        // one to make it non-negative before it is scaled into a byte.
        var scale = 255.0f / LocalUnitCount;

        _positions[index * 3] = Quantise((x + 1.0f) * scale);
        _positions[index * 3 + 1] = Quantise((y + 1.0f) * scale);
        _positions[index * 3 + 2] = Quantise((z + 1.0f) * scale);
        _normals[index] = Octahedral.Encode(normalX, normalY, normalZ);

        return index;
    }

    /// <summary>Appends a triangle from three vertex indices.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the mesh would exceed its triangle bound.</exception>
    public void AddTriangle(int a, int b, int c)
    {
        if (IndexCount + 3 > _indices.Length)
        {
            throw new InvalidOperationException(
                $"A brick of {VoxelCount} voxels may not exceed {MaxTriangles} triangles."
            );
        }

        _indices[IndexCount++] = (ushort)a;
        _indices[IndexCount++] = (ushort)b;
        _indices[IndexCount++] = (ushort)c;
    }

    /// <summary>
    /// Decodes a packed position back into the brick's expanded local space.
    /// </summary>
    /// <remarks>
    /// The same decode the mesh shader performs. Kept in managed code as well so the splitter can compute a
    /// meshlet's bounding volume and normal cone without a round trip through the GPU.
    /// </remarks>
    public (float X, float Y, float Z) DecodePosition(int vertex)
    {
        var (x, y, z) = PositionAt(vertex);
        var scale = LocalUnitCount / 255.0f;

        return (x * scale - 1.0f, y * scale - 1.0f, z * scale - 1.0f);
    }

    private static byte Quantise(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
}

/// <summary>
/// Packs a unit normal into one byte using the octahedral encoding.
/// </summary>
/// <remarks>
/// The octahedron mapping folds the sphere onto a square, which makes encode and decode both branch-free
/// arithmetic rather than a lookup. That matters here because the decode runs in the mesh shader, once per
/// vertex, and the encode runs once per extracted vertex.
/// </remarks>
public static class Octahedral
{
    private const int BitsPerAxis = 4;
    private const int AxisMask = (1 << BitsPerAxis) - 1;
    private const float AxisMax = AxisMask;

    /// <summary>Packs a normal into a byte, four bits per octahedral axis.</summary>
    public static byte Encode(float x, float y, float z)
    {
        var length = MathF.Sqrt(x * x + y * y + z * z);
        if (length > 0.0f)
        {
            x /= length;
            y /= length;
            z /= length;
        }

        var denominator = MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z);
        if (denominator <= 0.0f)
            return 0;

        var u = x / denominator;
        var v = y / denominator;

        if (z < 0.0f)
        {
            var signU = u >= 0.0f ? 1.0f : -1.0f;
            var signV = v >= 0.0f ? 1.0f : -1.0f;

            var foldedU = (1.0f - MathF.Abs(v)) * signU;
            var foldedV = (1.0f - MathF.Abs(u)) * signV;

            u = foldedU;
            v = foldedV;
        }

        var packedU = (int)MathF.Round((u * 0.5f + 0.5f) * AxisMax);
        var packedV = (int)MathF.Round((v * 0.5f + 0.5f) * AxisMax);

        return (byte)((Math.Clamp(packedU, 0, AxisMask) << BitsPerAxis) | Math.Clamp(packedV, 0, AxisMask));
    }

    /// <summary>Unpacks a byte back into a unit normal.</summary>
    public static (float X, float Y, float Z) Decode(byte packed)
    {
        var u = ((packed >> BitsPerAxis) & AxisMask) / AxisMax * 2.0f - 1.0f;
        var v = (packed & AxisMask) / AxisMax * 2.0f - 1.0f;

        var x = u;
        var y = v;
        var z = 1.0f - MathF.Abs(u) - MathF.Abs(v);

        if (z < 0.0f)
        {
            var signU = x >= 0.0f ? 1.0f : -1.0f;
            var signV = y >= 0.0f ? 1.0f : -1.0f;

            var unfoldedX = (1.0f - MathF.Abs(y)) * signU;
            var unfoldedY = (1.0f - MathF.Abs(x)) * signV;

            x = unfoldedX;
            y = unfoldedY;
        }

        var length = MathF.Sqrt(x * x + y * y + z * z);
        if (length <= 0.0f)
            return (0.0f, 0.0f, 0.0f);

        return (x / length, y / length, z / length);
    }
}
