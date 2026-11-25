using Brutal.Numerics;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Jobs;

/// <summary>
/// The kinds of world-generation work the job system can be asked to perform.
/// </summary>
/// <remarks>
/// An enum rather than a delegate or a class hierarchy, for the same reason commands are a flat struct:
/// a job is data that crosses a thread boundary, and data that crosses a thread boundary has to be
/// copyable, comparable and inspectable. A closure would carry an allocation and a captured environment
/// per job, which is exactly the cost a streaming world cannot afford when it enqueues thousands of jobs
/// to fill in a valley.
/// </remarks>
public enum WorldJobKind
{
    /// <summary>Sample a brick's field, extract its surface and split it into meshlets.</summary>
    BuildBrick = 0,
}

/// <summary>
/// One unit of world-generation work: a request to build a specific brick.
/// </summary>
/// <remarks>
/// <para>
/// Identified by its key rather than by a brick id, because the id does not exist yet when the job is
/// queued. The pool allocates the id when the request is made, so the job carries the reservation rather
/// than taking one: a worker must not allocate, because allocation mutates shared state and would put a
/// lock in front of work that is otherwise embarrassingly parallel.
/// </para>
/// <para>
/// <see cref="Epoch"/> is the clipmap's recentring generation. It is how a result the camera has already
/// made obsolete is detected: a brick requested in epoch 7 must not be committed into epoch 8's slot,
/// because that slot now describes different terrain, and nothing downstream could tell that the geometry
/// no longer belongs there.
/// </para>
/// </remarks>
/// <param name="Kind">What to do.</param>
/// <param name="Epoch">The clipmap generation this brick was requested for.</param>
/// <param name="Key">The brick to build.</param>
/// <param name="BrickId">The physical brick id reserved for this brick.</param>
public readonly record struct WorldJob(WorldJobKind Kind, long Epoch, BrickKey Key, int BrickId);

/// <summary>
/// A finished brick waiting to be committed, identified by which reusable buffer holds its geometry.
/// </summary>
/// <param name="Job">The job that produced the geometry.</param>
/// <param name="BufferIndex">Index of the reusable buffer holding the geometry.</param>
/// <param name="MeshletCount">Number of meshlets produced. Zero means the brick held no surface.</param>
/// <param name="ElapsedTicks">Time the worker spent on the job, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</param>
public readonly record struct WorldJobCompletion(
    WorldJob Job,
    int BufferIndex,
    int MeshletCount,
    long ElapsedTicks
);

/// <summary>
/// A brick's resident record: where its geometry lives and what transform decodes it.
/// </summary>
/// <remarks>
/// Every field except the meshlet range is a pure function of the brick's key, which is what lets the
/// renderer derive a draw command without asking the job system anything except the range.
/// </remarks>
/// <param name="Key">The brick this record describes.</param>
/// <param name="Origin">World-space minimum corner of the brick.</param>
/// <param name="VoxelSize">World-space size of one voxel at the brick's level.</param>
/// <param name="MeshletOffset">Offset of the brick's first meshlet in the committed meshlet arena, or -1.</param>
/// <param name="MeshletCount">Number of meshlets the brick produced, zero when it held no surface.</param>
/// <param name="Epoch">The request generation this geometry was built for.</param>
public readonly record struct BrickRecord(
    BrickKey Key,
    float3 Origin,
    float VoxelSize,
    int MeshletOffset,
    int MeshletCount,
    long Epoch
)
{
    /// <summary>True when this record describes geometry that has been committed and can be drawn.</summary>
    public bool HasGeometry => MeshletCount > 0 && MeshletOffset >= 0;
}

/// <summary>
/// One brick's draw command, in the form the renderer consumes it.
/// </summary>
/// <remarks>
/// <para>
/// This is the boundary between the world and the renderer, and it is deliberately a flat value type with
/// no references. The renderer must not walk the job system's arrays while workers are mutating them, so
/// the job system publishes a snapshot of exactly the fields a draw needs, and the renderer reads only
/// that. Nothing here points back at the world, which is what makes the snapshot safe to hold across a
/// frame boundary.
/// </para>
/// <para>
/// The meshlet range is in arena coordinates, so the draw is one <c>DrawIndexed</c> per meshlet with a
/// first-index offset and no indirection. <see cref="DecodeScale"/> folds the voxel size, the brick's
/// expanded unit span and the byte range into the single number the vertex shader multiplies a packed
/// byte by, so the shader needs no knowledge of the brick's shape.
/// </para>
/// </remarks>
/// <param name="Origin">World-space minimum corner of the brick.</param>
/// <param name="DecodeScale">Metres per unit of packed vertex byte.</param>
/// <param name="MeshletOffset">Offset of the brick's first meshlet in the arena.</param>
/// <param name="MeshletCount">Number of meshlets the brick produced.</param>
public readonly record struct BrickDraw(
    float3 Origin,
    float DecodeScale,
    int MeshletOffset,
    int MeshletCount
);
