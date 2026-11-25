using System.Diagnostics;
using Brutal.Logging;
using Brutal.Numerics;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Logging;
using BrutalTestEngine.World;
using MSLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace BrutalTestEngine.Jobs;

/// <summary>
/// Tuning for the world job system: how much parallelism it is allowed and how much geometry it may hold.
/// </summary>
/// <param name="WorkerCount">
/// Number of world-generation threads. Zero means one per logical processor, minus one for the render
/// thread: world generation is throughput work and should use the machine, but it must not starve the
/// thread that has a frame deadline.
/// </param>
/// <param name="QueueCapacity">How many brick requests may be outstanding before the streamer is throttled.</param>
/// <param name="MaxResidentBricks">
/// How many bricks may hold geometry at once. This is the bound the brick registry is sized from.
/// </param>
/// <param name="MeshletsPerResidentBrick">
/// How many meshlets a resident brick is assumed to hold, which sizes the shared meshlet arena. This is an
/// average rather than a worst case: the hardware allows far more per brick, but terrain produces a thin
/// shell of surface cells and reserving the worst case for every brick wastes the difference on every brick
/// in the world. The arena allocator reports when it runs out, and the count is raised here.
/// </param>
public sealed record WorldJobSystemOptions(
    int WorkerCount = 0,
    int QueueCapacity = 4096,
    int MaxResidentBricks = 2048,
    int MeshletsPerResidentBrick = 6
)
{
    /// <summary>The options the engine ships with.</summary>
    public static WorldJobSystemOptions Default { get; } = new();

    /// <summary>Resolves <see cref="WorkerCount"/> against the actual machine.</summary>
    public int ResolvedWorkerCount() => WorkerCount > 0 ? WorkerCount : Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Validates the options.</summary>
    public WorldJobSystemOptions Validate()
    {
        if (QueueCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(QueueCapacity), QueueCapacity, "Must be positive.");

        if (MaxResidentBricks < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxResidentBricks),
                MaxResidentBricks,
                "Must be positive."
            );
        }

        if (MeshletsPerResidentBrick < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MeshletsPerResidentBrick),
                MeshletsPerResidentBrick,
                "Must be positive."
            );
        }

        if (WorkerCount < 0)
            throw new ArgumentOutOfRangeException(nameof(WorkerCount), WorkerCount, "Must not be negative.");

        return this;
    }
}

/// <summary>
/// Runs world generation asynchronously across a set of worker threads and hands the finished geometry to
/// the single consumer that owns the GPU-facing pools.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece that makes terrain generation cheap enough to be worth doing at all. Sampling a
/// brick's field is a hundred noise evaluations, extracting its surface is a walk over 729 cells, and
/// splitting the result is another pass over its triangles — tens of microseconds of pure arithmetic over
/// no shared state, which is the ideal shape for a job system. Doing it on the render thread would spend a
/// frame's whole budget on terrain the camera may not even be looking at yet.
/// </para>
/// <para>
/// Each worker owns a private <see cref="SdfBrickStore"/> and therefore a private set of scratch buffers.
/// That is the whole trick: the field is stateless, so the only per-worker state <em>is</em> the scratch,
/// and a worker that owns its scratch needs no lock anywhere in the pipeline. Brick building is then
/// perfectly parallel with the single exception of the queue.
/// </para>
/// <para>
/// Results are handed back through a bounded pool of reusable geometry buffers rather than through
/// per-result allocations. A worker claims a free buffer, fills it, and enqueues a completion identifying
/// it by index; the consumer reads the buffer, copies what it needs into the destination pools, and
/// returns the buffer to the free list. In steady state the pipeline allocates nothing, which matters
/// because a streaming world processes thousands of bricks a second and a per-brick allocation would put
/// the garbage collector directly into the frame path.
/// </para>
/// <para>
/// <b>Residency is not the same as completeness.</b> <see cref="TryCreateRequest"/> marks a brick as
/// requested and reports whether it still needs building; the mark is what stops the streamer from
/// queueing the same brick every frame, and it is released only when the brick is committed or explicitly
/// dropped. A generation counter rather than a flag is used for the mark, so a brick that a recentring has
/// invalidated can be re-requested without first having to be unmarked.
/// </para>
/// </remarks>
public sealed class WorldJobSystem : IDisposable
{
    /// <summary>How long a worker waits for work before re-checking whether it should exit.</summary>
    private const int WaitTimeoutMilliseconds = 100;

    /// <summary>
    /// Marks a brick as unrequested. Any epoch at or below zero is a valid "nothing wanted" value, so the
    /// sentinel is chosen to be unreachable rather than merely unlikely.
    /// </summary>
    private const long NoRequest = long.MinValue;

    private readonly WorldJobSystemOptions _options;
    private readonly WorldSettings _settings;
    private readonly WorldJobQueue _jobs;
    private readonly Thread[] _threads;
    private readonly ThreadTimerRegistry _timers;

    // The result side: a bounded set of reusable geometry buffers, a stack of the free ones, and a queue of
    // completions waiting to be committed.
    private readonly BrickGeometry[] _geometry;
    private readonly int[] _freeBuffers;
    private readonly Lock _bufferGate = new();
    private int _freeBufferCount;
    private readonly Queue<WorldJobCompletion> _completions = new();
    private readonly Lock _completionGate = new();

    // The destination pools: one shared arena of meshlet slots, handed out in contiguous runs so a brick's
    // meshlets are always adjacent and a draw can name them with one offset and one count.
    private readonly MeshletArena _arena;
    private readonly byte[] _vertexPool;
    private readonly uint[] _indexPool;
    private readonly Meshlet[] _meshlets;
    private readonly BrickRecord[] _bricks;

    /// <summary>Per-brick request marks, indexed by brick id.</summary>
    private readonly long[] _requested;

    private readonly ThreadMetrics _buildMetrics;
    private readonly ThreadMetrics _waitMetrics;

    // The draw list the renderer reads. It is rebuilt from the brick records on demand rather than
    // maintained incrementally, because a brick's record is written by the frame thread and read by the
    // render thread, and a rebuild is a single linear pass over a few thousand entries.
    private readonly BrickDraw[] _draws;

    // The range of the pools that has changed since the last upload, in meshlet slots. A commit can only
    // touch the run it just allocated, so the dirty range is the union of the runs committed since the
    // renderer last looked. Tracking it is what keeps an upload proportional to what moved rather than to
    // the size of the pools.
    private int _dirtyFirst = int.MaxValue;
    private int _dirtyLast = -1;

    private readonly LogCategory _log = EngineLog.Categories.World;

    private long _epoch = 1;
    private long _submitted;
    private long _committed;
    private long _empty;
    private long _stale;
    private long _outOfPool;
    private long _noBuffer;
    private long _overflow;
    private int _overflowReported;
    private volatile bool _disposed;
    private bool _started;

    /// <summary>Creates a job system over the given world settings.</summary>
    public WorldJobSystem(WorldSettings settings, WorldJobSystemOptions options, ThreadTimerRegistry timers)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timers);

        settings.Validate();
        options.Validate();

        _settings = settings;
        _options = options;
        _timers = timers;

        _jobs = new WorldJobQueue(options.QueueCapacity);

        _buildMetrics = timers.Get(ThreadTimerRegistry.Keys.WorldBuild);
        _waitMetrics = timers.Get(ThreadTimerRegistry.Keys.WorldWait);

        // One reusable buffer per queue slot, so a completion can always be published without blocking a
        // worker. Sizing this from the queue rather than from the worker count is what makes that true even
        // when every worker finishes at the same instant.
        _geometry = new BrickGeometry[options.QueueCapacity];
        for (var i = 0; i < _geometry.Length; i++)
            _geometry[i] = new BrickGeometry(settings);

        _freeBuffers = new int[_geometry.Length];
        for (var i = 0; i < _freeBuffers.Length; i++)
            _freeBuffers[i] = i;
        _freeBufferCount = _freeBuffers.Length;

        var meshletsPerBrick = BrickShape.MaxMeshletsForBrick(settings.BrickVoxelCount);

        // The arena is sized in meshlet slots from a resident-brick budget expressed in the worst case the
        // hardware allows, then divided down by the realistic case. Sizing it at the theoretical worst case
        // for every brick would reserve thirty-three meshlets per brick where terrain produces one to four,
        // which is tens of megabytes of pools that are almost entirely untouched.
        _arena = new MeshletArena(options.MaxResidentBricks * options.MeshletsPerResidentBrick);

        _vertexPool = new byte[_arena.Capacity * MeshletVertexPoolStride];

        // The destination index pool is 32-bit, unlike the worker's. A worker's indices are local to its own
        // brick and fit in 16 bits because one brick is at most 729 vertices; the destination's have to
        // address the whole arena, which is far past 65535. Rebasing them here is what lets the GPU draw a
        // brick's geometry with a single DrawIndexed and a first-index offset, with no per-brick indirection
        // to resolve which meshlet owns which vertex.
        _indexPool = new uint[_arena.Capacity * MeshletBuilder.IndexStride];
        _meshlets = new Meshlet[_arena.Capacity];
        _bricks = new BrickRecord[options.MaxResidentBricks];
        _requested = new long[options.MaxResidentBricks];
        _draws = new BrickDraw[options.MaxResidentBricks];

        _bricks.AsSpan().Fill(new BrickRecord(default, default, 0.0f, -1, 0, 0));
        _requested.AsSpan().Fill(NoRequest);

        var workerCount = options.ResolvedWorkerCount();

        Workers = new SdfBrickStore[workerCount];
        _threads = new Thread[workerCount];

        for (var i = 0; i < workerCount; i++)
        {
            // The field is stateless, so sharing it is safe and each worker's store is independently
            // scratch-buffered. This is the allocation that makes the pipeline lock-free.
            Workers[i] = new SdfBrickStore(new TerrainField(settings.Seed), settings);

            var slot = i;
            _threads[i] = new Thread(() => RunWorker(slot))
            {
                Name = $"WorldGen{slot}",
                IsBackground = true,

                // Below normal: world generation is throughput work with no deadline of its own, and it must
                // never make a frame or a simulation tick wait on a core.
                Priority = ThreadPriority.BelowNormal,
            };
        }
    }

    /// <summary>Number of meshlets one brick may hold, the stride of the destination pools.</summary>
    public int MeshletsPerBrick => BrickShape.MaxMeshletsForBrick(_settings.BrickVoxelCount);

    /// <summary>Bytes one meshlet occupies in the vertex pool.</summary>
    public int MeshletVertexPoolStride => MeshletBuilder.VertexStride * Meshlet.BytesPerVertex;

    /// <summary>Indices one meshlet occupies in the index pool.</summary>
    public int MeshletIndexPoolStride => MeshletBuilder.IndexStride;

    /// <summary>Number of meshlet slots the arena manages.</summary>
    public int MeshletCapacity => _arena.Capacity;

    /// <summary>The shared meshlet arena, for diagnostics and tests.</summary>
    public MeshletArena Arena => _arena;

    /// <summary>The worker stores, one per world-generation thread. Exposed for tests and diagnostics.</summary>
    public SdfBrickStore[] Workers { get; }

    /// <summary>The settings the system was built for.</summary>
    public WorldSettings Settings => _settings;

    /// <summary>How many bricks may hold geometry at once.</summary>
    public int MaxResidentBricks => _options.MaxResidentBricks;

    /// <summary>Number of worker threads.</summary>
    public int WorkerCount => Workers.Length;

    /// <summary>The committed vertex pool, in the layout the mesh shader expects.</summary>
    public byte[] VertexPool => _vertexPool;

    /// <summary>The committed index pool, holding arena-global indices.</summary>
    public uint[] IndexPool => _indexPool;

    /// <summary>The committed meshlets.</summary>
    public Meshlet[] Meshlets => _meshlets;

    /// <summary>Per-brick resident records, indexed by brick id.</summary>
    public BrickRecord[] Bricks => _bricks;

    /// <summary>The current clipmap generation. A result from an older generation is discarded.</summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>Number of brick requests submitted to the queue.</summary>
    public long SubmittedCount => Interlocked.Read(ref _submitted);

    /// <summary>Number of results taken off the completion queue.</summary>
    public long CompletedCount => Interlocked.Read(ref _committed);

    /// <summary>Number of committed bricks that turned out to hold no surface.</summary>
    public long EmptyCount => Interlocked.Read(ref _empty);

    /// <summary>Number of results discarded because the clipmap moved while they were being built.</summary>
    public long StaleResultCount => Interlocked.Read(ref _stale);

    /// <summary>Number of requests refused because the brick pool was full.</summary>
    public long OutOfPoolCount => Interlocked.Read(ref _outOfPool);

    /// <summary>Number of results dropped because no geometry buffer was free.</summary>
    public long NoBufferCount => Interlocked.Read(ref _noBuffer);

    /// <summary>Number of commits refused because the destination meshlet pool was full.</summary>
    public long OverflowCount => Interlocked.Read(ref _overflow);

    /// <summary>Requests queued or in flight.</summary>
    public long InFlightCount => SubmittedCount - CompletedCount;

    /// <summary>Number of completions waiting to be taken.</summary>
    public int CompletionsWaiting
    {
        get
        {
            lock (_completionGate)
                return _completions.Count;
        }
    }

    /// <summary>Number of geometry buffers currently free.</summary>
    public int FreeBufferCount
    {
        get
        {
            lock (_bufferGate)
                return _freeBufferCount;
        }
    }

    /// <summary>Number of bricks the registry believes are resident with geometry.</summary>
    public long ResidentBrickCount
    {
        get
        {
            long count = 0;
            foreach (var brick in _bricks)
            {
                if (brick.HasGeometry)
                    count++;
            }

            return count;
        }
    }

    /// <summary>Total meshlets the workers have built, across every brick and every worker.</summary>
    public long MeshletsBuilt
    {
        get
        {
            long total = 0;
            foreach (var worker in Workers)
                total += worker.MeshletsProduced;

            return total;
        }
    }

    /// <summary>Starts the worker threads. Safe to call more than once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
            return;

        _started = true;

        for (var i = 0; i < _threads.Length; i++)
            _threads[i].Start();
    }

    /// <summary>
    /// Marks a brick as wanted and, when it still needs building, queues the job for it.
    /// </summary>
    /// <param name="key">The brick to build.</param>
    /// <param name="slot">The brick's slot in its level's grid, which is the pool's slot space.</param>
    /// <param name="pool">The clipmap's brick pool, which owns the id space.</param>
    /// <returns>
    /// True when the brick is now either queued or already resident for the current generation. False when
    /// the caller should stop asking for bricks this frame — the pool or the queue is backed up.
    /// </returns>
    /// <remarks>
    /// The mark is set before the job is queued, so a request that the queue rejects is still marked and
    /// does not have to be re-derived; it simply will not be resident, and the streamer's next pass will
    /// re-request it. That ordering matters because the streamer's pass is driven by what is <em>not</em>
    /// resident, and a rejected request that left no mark would be re-derived and re-rejected in a tight
    /// loop every frame.
    /// </remarks>
    public bool TryCreateRequest(BrickKey key, int slot, SdfBrickPool pool)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pool);

        var epoch = Interlocked.Read(ref _epoch);
        var id = pool.IdOf(slot);

        if (id != SdfBrickPool.Empty && _requested[id] == epoch)
        {
            // Already wanted in this generation, which is the overwhelmingly common case: most of a frame's
            // candidate bricks were requested in an earlier frame and are still resident or still building.
            return true;
        }

        if (id == SdfBrickPool.Empty)
        {
            id = pool.Allocate(slot);
            if (id == SdfBrickPool.Empty)
            {
                Interlocked.Increment(ref _outOfPool);
                return false;
            }
        }

        _requested[id] = epoch;
        _bricks[id] = new BrickRecord(
            key,
            key.Origin(_settings),
            _settings.VoxelSize(key.Level),
            -1,
            0,
            epoch
        );

        var job = new WorldJob(WorldJobKind.BuildBrick, epoch, key, id);

        if (!_jobs.TryEnqueue(job))
        {
            // Left marked on purpose. The streamer's next pass will see the mark and re-request, so a
            // backed-up queue produces one retry per pass rather than a spin.
            return false;
        }

        Interlocked.Increment(ref _submitted);
        return true;
    }

    /// <summary>
    /// Drops every mark from an earlier generation, so the streamer re-derives what the world now needs.
    /// </summary>
    /// <returns>The new generation.</returns>
    /// <remarks>
    /// Bumping the generation rather than walking the request array is what makes a recentring cheap: the
    /// marks from the previous generation simply stop matching, and a brick that is still wanted is
    /// re-requested only if it is genuinely absent. The consequence is that a recentring does not
    /// invalidate geometry the workers have already built for a still-correct brick, which is the difference
    /// between a clipmap that can afford to move and one that re-meshes the world on every brick crossing.
    /// </remarks>
    public long BeginEpoch()
    {
        var next = Interlocked.Increment(ref _epoch);

        if (_log.IsEnabled(MSLogLevel.Debug))
            _log.Debug($"world generation epoch {next}");

        return next;
    }

    /// <summary>
    /// Takes the next completion without blocking.
    /// </summary>
    /// <remarks>
    /// The caller must release <paramref name="completion"/> by calling <see cref="Commit"/> or
    /// <see cref="Release"/>. Leaking one costs a buffer from a bounded set, and leaking all of them stalls
    /// the pipeline, so the contract is strict.
    /// </remarks>
    public bool TryTakeCompletion(out WorldJobCompletion completion)
    {
        lock (_completionGate)
        {
            if (_completions.Count == 0)
            {
                completion = default;
                return false;
            }

            completion = _completions.Dequeue();
        }

        Interlocked.Increment(ref _committed);
        return true;
    }

    /// <summary>The meshlets of an uncommitted completion. Valid until the completion is released.</summary>
    public ReadOnlySpan<Meshlet> PeekMeshlets(in WorldJobCompletion completion) =>
        _geometry[completion.BufferIndex].Meshlets.AsSpan(0, completion.MeshletCount);

    /// <summary>
    /// Copies a completion's geometry into the destination pools and marks the brick as resident.
    /// </summary>
    /// <remarks>
    /// The copy rewrites each meshlet's pool offsets from the worker's coordinates into the destination's.
    /// That rewrite is why the destination needs no shared allocator: a brick's region is
    /// <c>brickId * MeshletsPerBrick</c>, so the offset is a pure function of the id the reservation
    /// already carries.
    /// </remarks>
    /// <returns>The number of meshlets committed.</returns>
    public int Commit(in WorldJobCompletion completion)
    {
        var job = completion.Job;
        var brickId = job.BrickId;

        try
        {
            if (brickId < 0 || brickId >= _bricks.Length)
                return 0;

            // A result built for an older generation is discarded rather than committed. The camera moved
            // and the slot now describes different terrain, so applying this geometry would put a valley
            // where a hill belongs, and no later stage could tell that it was wrong.
            if (_requested[brickId] != job.Epoch)
            {
                Interlocked.Increment(ref _stale);
                return 0;
            }

            if (completion.MeshletCount == 0)
            {
                Interlocked.Increment(ref _empty);
                _bricks[brickId] = _bricks[brickId] with { MeshletCount = 0 };
                return 0;
            }

            var geometry = _geometry[completion.BufferIndex];
            var destinationMeshlet = _arena.Allocate(completion.MeshletCount);

            if (destinationMeshlet < 0)
            {
                ReportOverflow(completion.MeshletCount);
                _bricks[brickId] = _bricks[brickId] with { MeshletCount = 0, MeshletOffset = -1 };
                return 0;
            }

            var vertexByteBase = destinationMeshlet * MeshletVertexPoolStride;
            var indexBase = destinationMeshlet * MeshletIndexPoolStride;

            geometry.UsedVertexBytes(completion.MeshletCount).CopyTo(_vertexPool.AsSpan(vertexByteBase));

            // The index rebase is the one piece of per-element work in the commit. A worker's indices are
            // local to its own brick and reference vertices from zero, so each has to be widened to 32 bits
            // and moved to the arena's numbering. Doing both in one pass is why there is no intermediate
            // 16-bit copy of the indices at all.
            var source = geometry.Meshlets.AsSpan(0, completion.MeshletCount);
            var destination = _meshlets.AsSpan(destinationMeshlet, completion.MeshletCount);

            // A meshlet's vertex offset is in vertices, so the byte base is converted back rather than
            // stored: keeping the two pool strides independent is what lets either change on its own.
            var destinationVertex = vertexByteBase / Meshlet.BytesPerVertex;

            for (var i = 0; i < completion.MeshletCount; i++)
            {
                var meshlet = source[i];

                destination[i] = meshlet with
                {
                    VertexOffset = destinationVertex + meshlet.VertexOffset,
                    IndexOffset = indexBase + meshlet.IndexOffset,
                };
            }

            CopyAndRebaseIndices(geometry, source, destinationMeshlet, indexBase);

            // A brick being rebuilt must release the run it held first, or every roll of the world leaks a
            // run into the arena until it fragments into unusable slivers.
            var previous = _bricks[brickId];
            if (previous.HasGeometry && previous.MeshletOffset != destinationMeshlet)
                _arena.Release(previous.MeshletOffset, previous.MeshletCount);

            _bricks[brickId] = previous with
            {
                MeshletOffset = destinationMeshlet,
                MeshletCount = completion.MeshletCount,
            };

            MarkDirty(destinationMeshlet, completion.MeshletCount);

            return completion.MeshletCount;
        }
        finally
        {
            Discard(completion);
        }
    }

    /// <summary>Abandons a completion without committing it, returning its geometry buffer.</summary>
    public void Discard(in WorldJobCompletion completion)
    {
        lock (_bufferGate)
            _freeBuffers[_freeBufferCount++] = completion.BufferIndex;
    }

    /// <summary>
    /// Releases a brick: its meshlet run returns to the arena and its id returns to the pool.
    /// </summary>
    /// <remarks>
    /// Both halves are required. Returning the run is what keeps the arena from fragmenting, and returning
    /// the id is what lets the slot be reused. Clearing the request mark as well is what makes the brick
    /// requestable again; leaving it set would make the streamer believe the brick is already in hand.
    /// </remarks>
    public void Evict(int brickId, SdfBrickPool pool, int slot)
    {
        ArgumentNullException.ThrowIfNull(pool);

        if (brickId < 0 || brickId >= _bricks.Length)
            return;

        var record = _bricks[brickId];

        if (record.HasGeometry)
        {
            // The released run's slots are marked dirty too. The renderer's draw list is rebuilt from the
            // records, so a released run is never drawn again; marking it keeps the dirty range honest for
            // any consumer that reads the pools directly.
            MarkDirty(record.MeshletOffset, record.MeshletCount);
            _arena.Release(record.MeshletOffset, record.MeshletCount);
        }

        _requested[brickId] = NoRequest;
        _bricks[brickId] = record with { MeshletCount = 0, MeshletOffset = -1, Epoch = 0 };

        if (pool.SlotOf(brickId) == slot)
            pool.Release(slot);
    }

    /// <summary>
    /// Whether a brick is resident with geometry, for the streamer to test.
    /// </summary>
    public bool IsResident(int slot, SdfBrickPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var id = pool.IdOf(slot);
        if (id == SdfBrickPool.Empty)
            return false;

        return _bricks[id].HasGeometry && _requested[id] == Interlocked.Read(ref _epoch);
    }

    /// <summary>
    /// Whether a brick id carries a request mark in the current generation, whether or not its geometry has
    /// arrived yet.
    /// </summary>
    /// <remarks>
    /// This is the test for "is this brick still wanted", as opposed to <see cref="IsResident"/>'s "does this
    /// brick have something to draw". The difference is the entire time a brick spends in flight, and getting
    /// it wrong makes every in-flight brick look abandoned: the streamer would evict each request before its
    /// worker could finish, so no brick would ever become resident and the world would render as scattered
    /// fragments of whatever happened to commit between two evictions.
    /// </remarks>
    public bool IsWanted(int brickId)
    {
        if (brickId < 0 || brickId >= _bricks.Length)
            return false;

        return _requested[brickId] == Interlocked.Read(ref _epoch);
    }

    /// <summary>
    /// Builds the draw list from the resident brick records and returns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called on the frame thread, never on the render thread: the records are written by the frame thread
    /// when it commits, so reading them anywhere else would be a data race. The returned span aliases an
    /// internal array that the next call overwrites, which is the contract the renderer's upload path
    /// relies on — it copies what it needs before returning.
    /// </para>
    /// <para>
    /// A brick whose geometry is empty is skipped rather than emitted with a zero count, because a draw
    /// with no indices is not free: it still costs a command and a pipeline state check on the GPU.
    /// </para>
    /// </remarks>
    public ReadOnlySpan<BrickDraw> BuildDrawList()
    {
        var count = 0;

        for (var i = 0; i < _bricks.Length; i++)
        {
            var record = _bricks[i];

            if (!record.HasGeometry)
                continue;

            // The packing maps the brick's expanded local range [-1, VoxelCount] onto the byte range 0..255,
            // so a byte decodes to the local coordinate `byte / 255 * (VoxelCount + 1) - 1` and then to
            // metres by multiplying by the voxel size. The shader evaluates `origin + byte / 255 * scale`,
            // which is the same expression with the two constants folded together: the scale is the whole
            // expanded span in metres, and the origin is shifted down by one voxel to absorb the -1.
            //
            // Getting this wrong is not a subtle error. Dropping the span factor makes the scale 255 times
            // too small, which collapses every brick to a sub-metre speck at its own corner — the geometry is
            // all there and all drawn, and the world is invisible.
            var localUnitCount = _settings.BrickVoxelCount + 1;
            var voxelSize = record.VoxelSize;

            _draws[count++] = new BrickDraw(
                record.Origin - new float3(voxelSize, voxelSize, voxelSize),
                voxelSize * localUnitCount,
                record.MeshletOffset,
                record.MeshletCount
            );
        }

        return _draws.AsSpan(0, count);
    }

    /// <summary>
    /// Takes the range of the pools that has changed since the last call, in meshlet slots.
    /// </summary>
    /// <remarks>
    /// The range is a union rather than a list, so a frame that commits bricks scattered across the arena
    /// uploads everything between the first and the last. That is the right trade for a streamer: a
    /// recentring commits a contiguous band of new bricks, and the alternative — a per-run list — costs an
    /// allocation and a sort to save a memcpy of a few hundred kilobytes.
    /// </remarks>
    /// <returns>False when nothing has changed since the last call.</returns>
    public bool TryTakeDirtyRange(out int firstMeshlet, out int meshletCount)
    {
        if (_dirtyLast < _dirtyFirst)
        {
            firstMeshlet = 0;
            meshletCount = 0;
            return false;
        }

        firstMeshlet = _dirtyFirst;
        meshletCount = _dirtyLast - _dirtyFirst + 1;

        _dirtyFirst = int.MaxValue;
        _dirtyLast = -1;

        return true;
    }

    /// <summary>Widens the dirty range to cover a run of meshlet slots.</summary>
    private void MarkDirty(int meshletOffset, int meshletCount)
    {
        if (meshletCount <= 0 || meshletOffset < 0)
            return;

        if (meshletOffset < _dirtyFirst)
            _dirtyFirst = meshletOffset;

        var last = meshletOffset + meshletCount - 1;
        if (last > _dirtyLast)
            _dirtyLast = last;
    }

    /// <summary>
    /// Blocks until every queued job has been taken off the completion queue, or the timeout elapses.
    /// </summary>
    /// <remarks>
    /// For tests and shutdown, never for the frame loop. A frame must not wait for world generation: the
    /// whole point of the job system is that the frame proceeds with whatever geometry is ready.
    /// </remarks>
    public bool WaitForIdle(int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (_jobs.Count == 0 && CompletionsWaiting == 0 && InFlightCount <= 0)
                return true;

            Thread.Sleep(1);
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _jobs.Complete();

        foreach (var thread in _threads)
        {
            if (thread.IsAlive)
                thread.Join(TimeSpan.FromSeconds(5));
        }

        foreach (var worker in Workers)
            worker.Dispose();

        foreach (var geometry in _geometry)
            geometry.Clear();

        _jobs.Dispose();
    }

    private void RunWorker(int slot)
    {
        var store = Workers[slot];

        while (true)
        {
            var waitedAt = Stopwatch.GetTimestamp();

            if (!_jobs.WaitDequeue(out var job, WaitTimeoutMilliseconds))
            {
                if (_jobs.IsCompleted || _disposed)
                    return;

                _waitMetrics.RecordSince(waitedAt);
                continue;
            }

            _waitMetrics.RecordSince(waitedAt);

            var startedAt = Stopwatch.GetTimestamp();

            try
            {
                var meshlets = store.Build(job.Key, job.BrickId);
                var elapsed = Stopwatch.GetTimestamp() - startedAt;

                _buildMetrics.RecordSince(startedAt);

                Publish(job, store, meshlets, elapsed);
            }
            catch (Exception exception)
            {
                // A worker must not take the process down over one bad brick. The exception is logged on the
                // worker's own thread and the loop continues, so a single degenerate brick cannot stop the
                // world from filling in.
                if (_log.IsEnabled(MSLogLevel.Error))
                    _log.Error(exception, $"world generation failed for brick {job.Key}: {exception.Message}");
            }
        }
    }

    private void Publish(WorldJob job, SdfBrickStore store, ReadOnlySpan<Meshlet> meshlets, long elapsedTicks)
    {
        int buffer;

        lock (_bufferGate)
        {
            if (_freeBufferCount == 0)
            {
                // The consumer is behind. Dropping the result is the right call: the brick stays marked and
                // non-resident, so the streamer will notice and ask for it again. Blocking a worker here
                // would deadlock against a consumer that is itself the only thread that frees buffers.
                Interlocked.Increment(ref _noBuffer);
                return;
            }

            buffer = _freeBuffers[--_freeBufferCount];
        }

        _geometry[buffer].Capture(store, meshlets);

        lock (_completionGate)
            _completions.Enqueue(new WorldJobCompletion(job, buffer, meshlets.Length, elapsedTicks));
    }

    /// <summary>
    /// Copies one brick's indices into the destination pool, widening them and moving them into the arena's
    /// vertex numbering in a single pass.
    /// </summary>
    /// <remarks>
    /// One pass rather than copy-then-rebase, because the source is 16-bit brick-local and the destination is
    /// 32-bit arena-global: there is no moment at which a straight copy would be meaningful, so the widening
    /// and the rebase are naturally the same operation. The work is a few hundred 32-bit adds per brick,
    /// which is small next to the vertex copy beside it.
    /// </remarks>
    private void CopyAndRebaseIndices(
        BrickGeometry geometry,
        ReadOnlySpan<Meshlet> meshlets,
        int destinationMeshlet,
        int indexBase
    )
    {
        var source = geometry.UsedIndexValues(meshlets.Length);

        for (var i = 0; i < meshlets.Length; i++)
        {
            var meshlet = meshlets[i];

            // The meshlet's own vertex range, converted from the worker's slot numbering into the arena's.
            var firstVertex = (destinationMeshlet + i) * MeshletBuilder.VertexStride;
            var start = indexBase + (i * MeshletBuilder.IndexStride);
            var destination = _indexPool.AsSpan(start, meshlet.IndexCount);

            var offset = i * MeshletBuilder.IndexStride;

            for (var index = 0; index < destination.Length; index++)
                destination[index] = (uint)(source[offset + index] + firstVertex);
        }
    }

    private void ReportOverflow(int meshletCount)
    {
        Interlocked.Increment(ref _overflow);

        if (Interlocked.Exchange(ref _overflowReported, 1) != 0)
            return;

        if (_log.IsEnabled(MSLogLevel.Error))
        {
            _log.Error(
                $"the committed meshlet pool is full; a brick needing {meshletCount} meshlet(s) was dropped. "
                    + $"Raise {nameof(WorldJobSystemOptions.MaxResidentBricks)} or lower the brick voxel count."
            );
        }
    }
}

/// <summary>
/// A reusable geometry buffer owned by the job system and filled by a worker.
/// </summary>
/// <remarks>
/// Sized once for the largest brick the settings allow, so a worker's publish is a pair of copies and never
/// a resize. Allocated only as many times as the queue can hold, which bounds the pipeline's transient
/// memory by configuration rather than by frame rate.
/// </remarks>
internal sealed class BrickGeometry(WorldSettings settings)
{
    private readonly byte[] _vertexBytes =
        new byte[BrickShape.MaxMeshletsForBrick(settings.BrickVoxelCount) * MeshletBuilder.VertexStride
            * Meshlet.BytesPerVertex];

    private readonly ushort[] _indexValues =
        new ushort[BrickShape.MaxMeshletsForBrick(settings.BrickVoxelCount) * MeshletBuilder.IndexStride];

    /// <summary>The meshlet headers, as the worker produced them.</summary>
    public Meshlet[] Meshlets { get; } =
        new Meshlet[BrickShape.MaxMeshletsForBrick(settings.BrickVoxelCount)];

    /// <summary>
    /// The vertex bytes the given meshlet count actually spans.
    /// </summary>
    /// <remarks>
    /// Only the used region is exposed, because copying the uninitialised remainder would move several
    /// times as many bytes as the brick needs. The destination region is fixed-stride, so the tail is
    /// simply left as it was.
    /// </remarks>
    public ReadOnlySpan<byte> UsedVertexBytes(int meshletCount) =>
        _vertexBytes.AsSpan(0, meshletCount * MeshletBuilder.VertexStride * Meshlet.BytesPerVertex);

    /// <summary>The index values the given meshlet count actually spans.</summary>
    public ReadOnlySpan<ushort> UsedIndexValues(int meshletCount) =>
        _indexValues.AsSpan(0, meshletCount * MeshletBuilder.IndexStride);

    /// <summary>
    /// Copies a worker's freshly built geometry into this buffer.
    /// </summary>
    /// <remarks>
    /// The worker's pools are fixed-stride and start at zero, so the meshlet offsets the builder recorded
    /// are already valid here and are copied through unchanged. Only the meshlets the brick actually
    /// produced are copied; the rest of the buffer's capacity is left as it was.
    /// </remarks>
    public void Capture(SdfBrickStore store, ReadOnlySpan<Meshlet> meshlets)
    {
        if (meshlets.Length == 0)
            return;

        var vertexBytes = meshlets.Length * MeshletBuilder.VertexStride * Meshlet.BytesPerVertex;
        var indexValues = meshlets.Length * MeshletBuilder.IndexStride;

        meshlets.CopyTo(Meshlets);
        store.VertexPool.AsSpan(0, vertexBytes).CopyTo(_vertexBytes);
        store.IndexPool.AsSpan(0, indexValues).CopyTo(_indexValues);
    }

    /// <summary>Drops the buffer's geometry. The arrays are kept so the buffer stays reusable.</summary>
    public void Clear()
    {
        Array.Clear(Meshlets);
        Array.Clear(_vertexBytes);
        Array.Clear(_indexValues);
    }
}
