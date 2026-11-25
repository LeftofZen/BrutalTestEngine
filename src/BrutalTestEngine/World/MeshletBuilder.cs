using Brutal.Numerics;

namespace BrutalTestEngine.World;

/// <summary>
/// A micro-mesh: a self-contained piece of a brick's surface that one mesh shader workgroup can emit.
/// </summary>
/// <remarks>
/// <para>
/// A meshlet is what makes GPU-driven rendering possible at all. Instead of the CPU deciding what to draw,
/// each meshlet carries its own bounding sphere and its own normal cone, so a task shader can reject it —
/// outside the frustum, behind the camera, occluded, or facing away — before a single vertex is shaded.
/// Rejection is per-meshlet rather than per-object, which is what lets a world of millions of bricks be
/// culled at a granularity fine enough to matter.
/// </para>
/// <para>
/// Vertices live in a shared pool and are addressed by the meshlet's offset into it. The splitter copies
/// vertices rather than sharing them across meshlets, so a meshlet's vertex range is exactly its own: that
/// costs a few duplicated vertices on the seams and buys a task shader that never has to check which
/// meshlet a shared vertex really belongs to.
/// </para>
/// <para>
/// Both deques are ordered so a meshlet can be dispatched on its own: vertices at
/// <c>[VertexOffset, VertexOffset + VertexCount)</c> and indices at
/// <c>[IndexOffset, IndexOffset + IndexCount)</c>, with indices already local to the meshlet's vertex
/// range. A task shader writes those two offsets into a draw-indirect command and a mesh shader reads
/// them, so neither ever has to know how the split was made.
/// </para>
/// </remarks>
public readonly record struct Meshlet
{
    /// <summary>Bytes per quantised vertex: three bytes of position and one of packed normal.</summary>
    public const int BytesPerVertex = 4;

    /// <summary>Index of the brick whose surface this meshlet belongs to.</summary>
    public int BrickId { get; init; }

    /// <summary>Offset, in vertices, of this meshlet's first vertex in the vertex pool.</summary>
    public int VertexOffset { get; init; }

    /// <summary>Offset, in indices, of this meshlet's first index in the index pool.</summary>
    public int IndexOffset { get; init; }

    /// <summary>Number of vertices this meshlet emits.</summary>
    public int VertexCount { get; init; }

    /// <summary>Number of indices this meshlet emits. Always a multiple of three.</summary>
    public int IndexCount { get; init; }

    /// <summary>Centre of the meshlet's bounding sphere, in world space.</summary>
    public float3 BoundsCenter { get; init; }

    /// <summary>Radius of the meshlet's bounding sphere, in world space.</summary>
    public float Radius { get; init; }

    /// <summary>
    /// Direction the meshlet's normals cluster around, in world space. Meaningless when
    /// <see cref="IsConeDegenerate"/> is true.
    /// </summary>
    public float3 ConeAxis { get; init; }

    /// <summary>
    /// Cosine of the angle around <see cref="ConeAxis"/> that contains every normal in the meshlet.
    /// </summary>
    public float ConeCosine { get; init; }

    /// <summary>
    /// True when the meshlet's normals do not cluster usefully and cone culling must not reject it.
    /// </summary>
    /// <remarks>
    /// A closing loop of triangles can have normals pointing almost everywhere; forcing a cone on them would
    /// mean either a wide cone that rejects nothing while costing the same to test, or a narrow cone that
    /// wrongly culls geometry. Signalling degeneracy is both cheaper and correct, and the reject test is
    /// written so that a degenerate cone always fails to reject.
    /// </remarks>
    public bool IsConeDegenerate => ConeCosine < 0.0f;

    /// <summary>Number of triangles this meshlet emits.</summary>
    public int TriangleCount => IndexCount / 3;
}

/// <summary>
/// Cuts a <see cref="BrickMesh"/> into meshlets that each fit a mesh shader workgroup, appending the
/// result to the shared vertex and index pools.
/// </summary>
/// <remarks>
/// <para>
/// The split is by triangle order, greedily, keeping a running vertex count. A triangle goes into the
/// current meshlet when the meshlet still fits once that triangle's not-yet-present vertices are counted;
/// otherwise the meshlet is closed and the triangle starts the next one. Two properties make that exact
/// rather than approximate.
/// </para>
/// <para>
/// First, a vertex is local to the meshlet that first used it and is copied into any later meshlet that
/// needs it. So the only question is whether the current meshlet has room, and that is decidable before the
/// triangle is written. Vertices are never shared between meshlets, which means a meshlet's vertex range is
/// self-contained and a task shader needs no lookup to find them.
/// </para>
/// <para>
/// Second, a triangle requires at most three vertices, so a fresh meshlet always has room for it and the
/// loop always progresses. Without that, the greedy split would need a fallback for a triangle larger than
/// the budget.
/// </para>
/// <para>
/// The result is a pure function of the mesh's triangle order, so the same brick always splits the same
/// way. That is what makes the split cacheable and, more importantly, what makes the GPU version of it
/// testable against this one.
/// </para>
/// </remarks>
public static class MeshletBuilder
{
    /// <summary>
    /// The number of vertices one meshlet can hold, used to derive a meshlet's indices from its ordinal.
    /// </summary>
    /// <remarks>
    /// Meshlets are packed at a fixed stride rather than densely, so a meshlet's pool offsets are a pure
    /// function of its ordinal and the split needs no running offset bookkeeping. The cost is a little
    /// slack in the pools — a meshlet that uses 40 of its 60 vertex slots leaves 20 unused — which is
    /// nothing next to the cost of a second indirection when reading a meshlet's data on the GPU.
    /// </remarks>
    public const int VertexStride = BrickShape.MeshletVertexBudget;

    /// <summary>The number of indices one meshlet can hold, its stride in the index pool.</summary>
    public const int IndexStride = BrickShape.MeshletTriangleBudget * 3;

    /// <summary>
    /// Splits <paramref name="mesh"/> into meshlets, writing its vertices and indices into the pools.
    /// </summary>
    /// <param name="mesh">The extracted brick mesh.</param>
    /// <param name="meshlets">The list to append the produced meshlets to.</param>
    /// <param name="vertexPool">The shared vertex pool, three bytes per vertex plus one of normal.</param>
    /// <param name="indexPool">The shared index pool, holding meshlet-local indices.</param>
    /// <param name="brickId">The physical brick id to record on each meshlet.</param>
    /// <param name="brickOrigin">World-space minimum corner of the brick.</param>
    /// <param name="voxelSize">World-space size of one voxel, used to lift local positions into the world.</param>
    /// <returns>The number of meshlets appended.</returns>
    public static int Build(
        BrickMesh mesh,
        List<Meshlet> meshlets,
        Span<byte> vertexPool,
        Span<ushort> indexPool,
        int brickId,
        float3 brickOrigin,
        float voxelSize
    )
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(meshlets);

        if (mesh.IsEmpty)
            return 0;

        var indices = mesh.Indices;
        var firstMeshlet = meshlets.Count;
        var built = firstMeshlet;
        var triangle = 0;
        var triangleCount = mesh.TriangleCount;

        // Reused across meshlets: the meshlet-local index of each of the mesh's vertices, or -1 when the
        // current meshlet has not used it yet.
        var localOfVertex = new int[mesh.VertexCount];

        while (triangle < triangleCount)
        {
            Array.Fill(localOfVertex, -1);

            var vertexBase = built * VertexStride;
            var indexBase = built * IndexStride;

            var vertexCount = 0;
            var indexCount = 0;

            var min = new float3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new float3(float.MinValue, float.MinValue, float.MinValue);

            // The cone is seeded from an actual normal rather than from an arbitrary axis, so it starts as
            // tight as the geometry allows instead of arbitrarily wide.
            var coneAxis = NormalAt(mesh, indices[triangle * 3]);
            var minDot = 1.0f;

            while (triangle < triangleCount)
            {
                var a = indices[triangle * 3];
                var b = indices[triangle * 3 + 1];
                var c = indices[triangle * 3 + 2];

                var growth = (localOfVertex[a] < 0 ? 1 : 0)
                    + (localOfVertex[b] < 0 ? 1 : 0)
                    + (localOfVertex[c] < 0 ? 1 : 0);

                if (vertexCount + growth > VertexStride || indexCount + 3 > IndexStride)
                    break;

                Write(mesh, vertexPool, localOfVertex, vertexBase, ref vertexCount, a, brickOrigin, voxelSize, ref min, ref max);
                Write(mesh, vertexPool, localOfVertex, vertexBase, ref vertexCount, b, brickOrigin, voxelSize, ref min, ref max);
                Write(mesh, vertexPool, localOfVertex, vertexBase, ref vertexCount, c, brickOrigin, voxelSize, ref min, ref max);

                minDot = MathF.Min(minDot, Dot(coneAxis, NormalAt(mesh, a)));
                minDot = MathF.Min(minDot, Dot(coneAxis, NormalAt(mesh, b)));
                minDot = MathF.Min(minDot, Dot(coneAxis, NormalAt(mesh, c)));

                indexPool[indexBase + indexCount] = (ushort)localOfVertex[a];
                indexPool[indexBase + indexCount + 1] = (ushort)localOfVertex[b];
                indexPool[indexBase + indexCount + 2] = (ushort)localOfVertex[c];

                indexCount += 3;
                triangle++;
            }

            meshlets.Add(new Meshlet
            {
                BrickId = brickId,
                VertexOffset = vertexBase,
                IndexOffset = indexBase,
                VertexCount = vertexCount,
                IndexCount = indexCount,
                BoundsCenter = new float3(
                    (min.X + max.X) * 0.5f,
                    (min.Y + max.Y) * 0.5f,
                    (min.Z + max.Z) * 0.5f
                ),
                Radius = Radius(min, max),
                ConeAxis = coneAxis,

                // A cone reaching past a right angle rejects nothing, so it is reported as degenerate
                // rather than carried around and tested against vertex after vertex for no benefit.
                ConeCosine = minDot > 0.0f ? minDot : -1.0f,
            });

            built++;
        }

        return built - firstMeshlet;
    }

    private static void Write(
        BrickMesh mesh,
        Span<byte> vertexPool,
        int[] localOfVertex,
        int vertexBase,
        ref int vertexCount,
        int vertex,
        float3 brickOrigin,
        float voxelSize,
        ref float3 min,
        ref float3 max
    )
    {
        var world = World(mesh, vertex, brickOrigin, voxelSize);

        min.X = MathF.Min(min.X, world.X);
        min.Y = MathF.Min(min.Y, world.Y);
        min.Z = MathF.Min(min.Z, world.Z);
        max.X = MathF.Max(max.X, world.X);
        max.Y = MathF.Max(max.Y, world.Y);
        max.Z = MathF.Max(max.Z, world.Z);

        if (localOfVertex[vertex] >= 0)
            return;

        var local = vertexCount++;
        localOfVertex[vertex] = local;

        var (x, y, z) = mesh.PositionAt(vertex);
        var destination = (vertexBase + local) * Meshlet.BytesPerVertex;

        vertexPool[destination] = x;
        vertexPool[destination + 1] = y;
        vertexPool[destination + 2] = z;
        vertexPool[destination + 3] = mesh.NormalAt(vertex);
    }

    private static float3 World(BrickMesh mesh, int vertex, float3 brickOrigin, float voxelSize)
    {
        var (x, y, z) = mesh.DecodePosition(vertex);

        return new float3(
            brickOrigin.X + x * voxelSize,
            brickOrigin.Y + y * voxelSize,
            brickOrigin.Z + z * voxelSize
        );
    }

    private static float3 NormalAt(BrickMesh mesh, int vertex)
    {
        var (x, y, z) = Octahedral.Decode(mesh.NormalAt(vertex));

        return new float3(x, y, z);
    }

    private static float Radius(float3 min, float3 max)
    {
        var halfX = (max.X - min.X) * 0.5f;
        var halfY = (max.Y - min.Y) * 0.5f;
        var halfZ = (max.Z - min.Z) * 0.5f;

        return MathF.Sqrt(halfX * halfX + halfY * halfY + halfZ * halfZ);
    }

    private static float Dot(float3 a, float3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
