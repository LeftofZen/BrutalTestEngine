using System.Diagnostics;
using Brutal.Numerics;
using BrutalTestEngine.Engine;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Jobs;

/// <summary>
/// What a streamer pass did, for the debug panel and the run summary.
/// </summary>
/// <param name="LevelsRecentered">Number of clipmap levels whose anchor moved this pass.</param>
/// <param name="Candidates">Bricks the pass considered.</param>
/// <param name="SurfaceBricks">Candidates the pass believes contain surface, whether resident or not.</param>
/// <param name="Requested">Bricks the pass asked the job system to build.</param>
/// <param name="Skipped">Candidates skipped because they hold only air or only solid rock.</param>
/// <param name="Evicted">Resident bricks dropped because the camera left them behind.</param>
/// <param name="BudgetExhausted">True when the pass stopped early because the pool or queue was full.</param>
public readonly record struct StreamerStatistics(
    int LevelsRecentered,
    int Candidates,
    int SurfaceBricks,
    int Requested,
    int Skipped,
    int Evicted,
    bool BudgetExhausted
);

/// <summary>
/// Decides which bricks the world needs, and keeps the job system fed with exactly those.
/// </summary>
/// <remarks>
/// <para>
/// This is the only part of world generation that runs on a frame's critical path, so it is written to do as
/// little as possible. A pass walks the clipmap's slots and decides, for each, whether the terrain can pass
/// through that brick at all. A brick holding only air or only solid rock needs no mesh, and on real terrain
/// that is the overwhelming majority: a level's 1728 slots reduce to a few dozen bricks worth building. That
/// single decision is why the streamer can run at frame rate while the meshing it triggers cannot.
/// </para>
/// <para>
/// The test has to be <em>sound</em>, which is a stronger requirement than being conservative. It derives its
/// bounds from the field's own height scale and shortest wavelength, so a brick is skipped only when those
/// numbers say it cannot contain surface. A bound measured by sampling would only cover the samples taken,
/// and a brick rejected on that basis is a hole in the world that no test would find, because the hole looks
/// exactly like terrain that was never there.
/// </para>
/// <para>
/// The pass is bounded. When the pool or the queue runs out of room it stops walking and reports that it did,
/// rather than pressing on and being refused for every remaining slot. The next pass resumes once the workers
/// have drained; because slots are walked in a fixed order, the resumption is deterministic.
/// </para>
/// </remarks>
public sealed class WorldStreamer
{
    /// <summary>
    /// Height samples taken per brick along each horizontal axis when testing for surface.
    /// </summary>
    /// <remarks>
    /// Four samples per axis: the corners, the edge midpoints and the centre. This is the granularity at which
    /// the test stops being free and starts being useful. One sample per brick would miss a spike inside it,
    /// and testing the brick's full 81 columns would cost as much as the meshing the test exists to avoid.
    /// </remarks>
    private const int SamplesPerBrickAxis = 4;

    private readonly WorldJobSystem _jobs;
    private readonly ThreadMetrics _streamMetrics;
    private readonly ThreadMetrics _commitMetrics;

    /// <summary>
    /// The surface verdict for each slot, and the anchor stamp it was reached at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The verdict for a slot is a pure function of the level's anchor and the slot index, and the anchor only
    /// moves when the camera crosses a brick boundary. Without this cache the pass re-evaluates sixteen noise
    /// samples for every slot of every level on every frame — four thousand slots times sixteen samples times
    /// five octaves — to reach an answer that almost never changes. That was the single largest cost on the
    /// frame's critical path.
    /// </para>
    /// <para>
    /// A slot's stamp is compared against its level's, so a level that recentres invalidates its own slots and
    /// no others. The stamps start unequal to force a miss on the first pass, because a zero-initialised
    /// verdict would otherwise be read as a valid "no surface" for every slot in the world.
    /// </para>
    /// </remarks>
    private readonly long[] _levelStamp;
    private readonly long[] _slotStamp;
    private readonly bool[] _slotSurface;
    private long _stamp = 1;

    private float3 _camera;
    private StreamerStatistics _statistics;
    private long _passCount;
    private long _totalRequested;
    private long _totalEvicted;

    /// <summary>Creates a streamer over a job system.</summary>
    public WorldStreamer(WorldJobSystem jobs, ThreadTimerRegistry timers)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(timers);

        _jobs = jobs;
        _streamMetrics = timers.Get(ThreadTimerRegistry.Keys.WorldStream);
        _commitMetrics = timers.Get(ThreadTimerRegistry.Keys.WorldCommit);

        var settings = jobs.Settings;
        var slots = settings.LevelCount * settings.BricksPerLevel;

        _levelStamp = new long[settings.LevelCount];
        _slotStamp = new long[slots];
        _slotSurface = new bool[slots];

        _levelStamp.AsSpan().Fill(_stamp);
    }

    /// <summary>The most recent pass's statistics.</summary>
    public StreamerStatistics Statistics => _statistics;

    /// <summary>Number of passes run.</summary>
    public long PassCount => _passCount;

    /// <summary>Total bricks requested across every pass.</summary>
    public long TotalRequested => _totalRequested;

    /// <summary>Total resident bricks evicted across every pass.</summary>
    public long TotalEvicted => _totalEvicted;

    /// <summary>The camera position the last pass streamed around.</summary>
    public float3 Camera => _camera;

    /// <summary>
    /// Advances streaming for a camera position.
    /// </summary>
    /// <remarks>
    /// The three phases have to be in this order. First the clipmap is recentred, because the level anchors
    /// decide which slot each brick lives in and the epoch decides which in-flight results are still wanted.
    /// Then bricks the camera has left behind are evicted, because evicting returns arena runs that the
    /// requests in the same pass may need. Only then are new bricks requested.
    /// </remarks>
    public StreamerStatistics Update(float3 camera, SdfClipmap clipmap, TerrainField field)
    {
        ArgumentNullException.ThrowIfNull(clipmap);
        ArgumentNullException.ThrowIfNull(field);

        var startedAt = Stopwatch.GetTimestamp();

        _camera = camera;

        // Recentring returns only the levels whose anchor actually moved, so a camera drifting within one
        // brick costs nothing at all. When a level did move, the previous generation's request marks no
        // longer apply and the hierarchy is re-derived against the new anchors.
        var recentered = clipmap.Recenter(camera);
        if (recentered.Length > 0)
        {
            _jobs.BeginEpoch();

            // A moved level's slots now hold different bricks, so its cached verdicts are meaningless. Only
            // the levels that moved are invalidated; the rest keep theirs and are skipped entirely.
            _stamp++;

            foreach (var level in recentered)
                _levelStamp[level] = _stamp;
        }

        var evicted = EvictStale(clipmap);
        var (candidates, surface, skipped, requested, exhausted) = RequestMissing(clipmap, field);

        _passCount++;
        _totalRequested += requested;
        _totalEvicted += evicted;

        _statistics = new StreamerStatistics(
            recentered.Length,
            candidates,
            surface,
            requested,
            skipped,
            evicted,
            exhausted
        );

        _streamMetrics.RecordSince(startedAt);
        return _statistics;
    }

    /// <summary>
    /// Commits the finished bricks that are ready, copying their geometry into the destination pools.
    /// </summary>
    /// <param name="maxCommits">
    /// A cap on how many commits one frame performs. The copy is memcpy work on the frame's critical path, so
    /// an unbounded burst of completions after the workers warm up would surface as a hitch.
    /// </param>
    /// <returns>The number of meshlets committed.</returns>
    public int CommitReady(int maxCommits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCommits, 0);

        var startedAt = Stopwatch.GetTimestamp();
        var meshlets = 0;
        var commits = 0;

        while (commits < maxCommits && _jobs.TryTakeCompletion(out var completion))
        {
            meshlets += _jobs.Commit(completion);
            commits++;
        }

        if (commits > 0)
            _commitMetrics.RecordSince(startedAt);

        return meshlets;
    }

    /// <summary>
    /// Evicts resident bricks the clipmap no longer owns, returning their arena runs and pool ids.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A brick is evicted when the hierarchy no longer resolves its centre to the level the geometry was
    /// built for. That single test covers both cases that matter: a brick that has fallen outside its level's
    /// grid, and a brick the level no longer owns because a finer level now covers it after the anchor moved.
    /// </para>
    /// <para>
    /// A brick that is still being built is <em>not</em> evicted, and that distinction is the whole point of
    /// this method's shape. A freshly requested brick has no geometry yet — meshlet count zero is its state
    /// for the entire time its job is in flight — so testing <see cref="BrickRecord.HasGeometry"/> alone
    /// treats every in-flight brick as stale and evicts it on the next pass, before its worker can finish.
    /// The queue then never drains, every frame re-requests the same bricks, and the world oscillates between
    /// "not built yet" and "thrown away". Only a brick with a request mark in an older generation is genuinely
    /// abandoned, which is what <see cref="WorldJobSystem.IsWanted"/> answers.
    /// </para>
    /// </remarks>
    private int EvictStale(SdfClipmap clipmap)
    {
        var bricks = _jobs.Bricks;
        var pool = clipmap.Pool;
        var evicted = 0;

        for (var id = 0; id < bricks.Length; id++)
        {
            var record = bricks[id];

            if (record.Epoch == 0)
                continue;

            var slot = pool.SlotOf(id);
            if (slot == SdfBrickPool.Empty)
                continue;

            // Still owned and still wanted: leave it alone whether or not its geometry has arrived. A brick
            // whose geometry is empty because the terrain does not reach it is resident in the sense that
            // matters — it will never produce a draw, so rebuilding it would be pure waste.
            if (IsStillOwned(clipmap, record) && _jobs.IsWanted(id))
                continue;

            _jobs.Evict(id, pool, slot);
            evicted++;
        }

        return evicted;
    }

    private static bool IsStillOwned(SdfClipmap clipmap, BrickRecord record)
    {
        if (record.Key.Level < 0 || record.Key.Level >= clipmap.LevelCount)
            return false;

        return clipmap.SelectLevel(record.Key.Center(clipmap.Settings)) == record.Key.Level;
    }

    /// <summary>
    /// Walks the clipmap's slots and requests every brick that contains surface and is not already in hand.
    /// </summary>
    private (int Candidates, int Surface, int Skipped, int Requested, bool Exhausted) RequestMissing(
        SdfClipmap clipmap,
        TerrainField field
    )
    {
        var settings = clipmap.Settings;

        var candidates = 0;
        var surface = 0;
        var skipped = 0;
        var requested = 0;
        var exhausted = false;

        for (var levelIndex = 0; levelIndex < clipmap.LevelCount && !exhausted; levelIndex++)
        {
            var level = clipmap.Level(levelIndex);
            var slotCount = settings.BricksPerLevel;
            var levelStamp = _levelStamp[levelIndex];
            var slotBase = levelIndex * slotCount;

            for (var slot = 0; slot < slotCount; slot++)
            {
                candidates++;

                // Slots are global across the hierarchy, not per level. The pool's table has one entry per
                // slot of every level, so passing a per-level index would make all nine levels share the
                // first level's 512 entries: a brick on level 3 and an unrelated brick on level 0 would
                // collide on the same slot, and the world could never hold more than one level's worth of
                // geometry.
                var globalSlot = slotBase + slot;

                if (_jobs.IsResident(globalSlot, clipmap.Pool))
                {
                    surface++;
                    continue;
                }

                var index = globalSlot;
                var lattice = level.SlotBrickKey(slot);

                // Only bricks this level actually owns are requested. The hierarchy resolves every world
                // point to exactly one level, and the eviction pass enforces that rule; requesting a brick a
                // different level owns would build geometry that is thrown away on the next pass and then
                // rebuilt, forever. Both passes have to agree about ownership or the world churns instead of
                // filling in.
                if (clipmap.SelectLevel(level.BrickOrigin(lattice) + level.HalfBrick) != levelIndex)
                {
                    skipped++;
                    continue;
                }

                // The verdict is only recomputed when this level's anchor has moved since it was last asked.
                // A resident brick is checked first because residency is the cheaper test and it is the one
                // that changes without the anchor moving.
                if (_slotStamp[index] != levelStamp)
                {
                    _slotSurface[index] = ContainsSurface(field, level.BrickOrigin(lattice), level.BrickSize);
                    _slotStamp[index] = levelStamp;
                }

                if (!_slotSurface[index])
                {
                    skipped++;
                    continue;
                }

                surface++;

                if (_jobs.TryCreateRequest(new BrickKey(levelIndex, lattice), globalSlot, clipmap.Pool))
                {
                    requested++;
                    continue;
                }

                // The pool or the queue is full. Stop walking rather than being refused for every remaining
                // slot: slots are walked in a fixed order, so the next pass resumes exactly here.
                exhausted = true;
                break;
            }
        }

        return (candidates, surface, skipped, requested, exhausted);
    }

    /// <summary>
    /// True when the terrain can pass through a brick, judged from a grid of height samples over its footprint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question is whether any column of the brick's footprint has a terrain height inside the brick's
    /// vertical span. Only finitely many columns can be sampled, so the test reasons about the ones between
    /// the samples from the field's own properties rather than from a fudge factor: the height changes by at
    /// most <see cref="TerrainField.HeightRange"/> over one full wavelength, so the span the samples cover can
    /// be widened by the fraction of the range that the sample spacing represents.
    /// </para>
    /// <para>
    /// Widening rather than testing the samples alone is what keeps the test sound at every brick size. On the
    /// coarsest levels a brick is kilometres across and the samples are far apart, so the slack is nearly the
    /// entire range and almost every brick in the level passes — which is correct, because at that spacing
    /// almost any brick could contain a mountain. On the finest level the bricks are eight metres and the
    /// slack is small, so the many bricks holding only air or only rock are rejected, which is where nearly
    /// all of a level's 1728 slots actually are.
    /// </para>
    /// </remarks>
    private static bool ContainsSurface(TerrainField field, float3 origin, float brickSize)
    {
        var bottom = origin.Y;
        var top = origin.Y + brickSize;

        // A brick entirely below the terrain's lowest possible height is solid ground through and through,
        // and one entirely above the highest possible height is open air. Neither contains a surface. This
        // test is exact rather than conservative, because the bounds are derived from the height scale.
        if (top < field.MinimumHeight || bottom > field.MaximumHeight)
            return false;

        var spacing = brickSize / (SamplesPerBrickAxis - 1);

        // The proportion of the field's range the terrain can change over one sample spacing. A spacing
        // longer than the shortest wavelength means it could change by anything in between, so the slack
        // saturates rather than growing past the range the field can produce.
        var reach = MathF.Min(1.0f, spacing / field.ShortestWavelength) * field.HeightRange;

        var lowest = float.MaxValue;
        var highest = float.MinValue;

        for (var z = 0; z < SamplesPerBrickAxis; z++)
        {
            var sampleZ = origin.Z + (z * spacing);

            for (var x = 0; x < SamplesPerBrickAxis; x++)
            {
                var height = field.HeightAt(origin.X + (x * spacing), sampleZ);

                if (height < lowest)
                    lowest = height;

                if (height > highest)
                    highest = height;
            }
        }

        // The brick contains surface when the span the terrain could occupy over its footprint overlaps the
        // brick's own vertical span. The reach is halved because it is shared between the two sides: a sample
        // at the lowest observed height could have neighbours half a reach below it, and one at the highest
        // could have neighbours half a reach above.
        var slack = reach * 0.5f;

        return lowest - slack <= top && highest + slack >= bottom;
    }
}
