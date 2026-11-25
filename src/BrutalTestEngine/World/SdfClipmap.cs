using Brutal.Numerics;
using System.Runtime.InteropServices;

namespace BrutalTestEngine.World;

/// <summary>
/// The SDF brick-clipmap: nested levels of brick grids, re-anchored around a moving point in whole bricks.
/// </summary>
/// <remarks>
/// <para>
/// The clipmap owns three things and no more: the static level geometry derived from
/// <see cref="WorldSettings"/>, the current anchor of each level, and the resident-brick set that the
/// levels' slots are mapped through. It does not own the bricks' voxel contents, the meshes, or any GPU
/// memory — those live behind the indirection table and are owned by whatever is producing and consuming
/// the geometry.
/// </para>
/// <para>
/// <see cref="Recenter"/> is the entry point that matters. It returns only the level indices whose anchor
/// actually moved. A level whose anchor did not move has the same bricks in the same slots as last frame,
/// so its meshes are still valid and its surface data does not need to be re-evaluated. That is what turns
/// a clipmap from "re-generate the world every frame" into "generate only what the camera walked into".
/// </para>
/// </remarks>
public sealed class SdfClipmap
{
    private readonly ClipmapLevel[] _levels;
    private readonly List<int> _recenteredLevels = [];

    /// <summary>Creates a clipmap around <paramref name="center"/> using the given settings.</summary>
    /// <param name="settings">The world shape.</param>
    /// <param name="center">The point to centre the levels on.</param>
    /// <param name="residentBrickBudget">
    /// How many bricks may hold geometry at once, or zero to allow one per slot. This is the consumer's
    /// budget, not the clipmap's: the pool's slot table always covers every slot of every level, but it will
    /// not hand out more ids than this, because the consumer's per-brick arrays are sized from it.
    /// </param>
    public SdfClipmap(WorldSettings settings, float3 center, int residentBrickBudget = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        Settings = settings;
        Center = center;

        _levels = new ClipmapLevel[settings.LevelCount];
        for (var level = 0; level < _levels.Length; level++)
            _levels[level] = BuildLevel(level, center);

        var idCount = residentBrickBudget > 0
            ? Math.Min(residentBrickBudget, settings.MaxAllocatedBricks)
            : settings.MaxAllocatedBricks;

        Pool = new SdfBrickPool(settings.MaxAllocatedBricks, idCount);
    }

    /// <summary>The settings this clipmap was built from.</summary>
    public WorldSettings Settings { get; }

    /// <summary>The point the clipmap is currently centred on.</summary>
    public float3 Center { get; private set; }

    /// <summary>The resident-brick set.</summary>
    public SdfBrickPool Pool { get; }

    /// <summary>The levels, finest first.</summary>
    public ReadOnlySpan<ClipmapLevel> Levels => _levels;

    /// <summary>Number of levels.</summary>
    public int LevelCount => _levels.Length;

    /// <summary>How many times <see cref="Recenter"/> has moved at least one level.</summary>
    public long RecenterCount { get; private set; }

    /// <summary>How many level anchors <see cref="Recenter"/> has moved over the clipmap's lifetime.</summary>
    public long RecenteredLevelCount { get; private set; }

    /// <summary>The level at <paramref name="index"/>.</summary>
    public ClipmapLevel Level(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _levels.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        return _levels[index];
    }

    /// <summary>
    /// The level whose resolution should own a brick at a given point in the world.
    /// </summary>
    /// <remarks>
    /// The finest level whose grid cube contains the point wins. Because each level's cube is larger than
    /// the one inside it and they share a centre, they nest: a point near the centre is inside every cube
    /// and gets the finest level, while a point beyond the smaller cubes is inside only the outer ones and
    /// gets a coarse level.
    /// </remarks>
    public int SelectLevel(float3 worldPosition)
    {
        for (var level = 0; level < _levels.Length; level++)
        {
            if (_levels[level].Contains(worldPosition))
                return level;
        }

        // Outside every cube, which only happens beyond the view distance. The coarsest level is the right
        // answer rather than an error: the world continues, it is just beyond what the clipmap resolves.
        return _levels.Length - 1;
    }

    /// <summary>
    /// Moves the clipmap's centre, re-anchoring only the levels whose anchor crossed a brick boundary.
    /// </summary>
    /// <returns>
    /// The level indices whose anchor moved, valid until the next call. Empty when nothing changed, which is
    /// the common case for a camera at rest or moving within one brick.
    /// </returns>
    public ReadOnlySpan<int> Recenter(float3 center)
    {
        Center = center;
        _recenteredLevels.Clear();

        for (var level = 0; level < _levels.Length; level++)
        {
            var anchor = ClipmapLevel.SnapAnchor(center, Settings, level);
            if (anchor == _levels[level].OriginBrick)
                continue;

            _levels[level] = BuildLevel(level, center);
            _recenteredLevels.Add(level);
        }

        if (_recenteredLevels.Count > 0)
        {
            RecenterCount++;
            RecenteredLevelCount += _recenteredLevels.Count;
        }

        return CollectionsMarshal.AsSpan(_recenteredLevels);
    }

    /// <summary>
    /// The key of the brick that owns a world-space point, and the level that owns it.
    /// </summary>
    public BrickKey BrickAt(float3 worldPosition)
    {
        var level = SelectLevel(worldPosition);

        return new BrickKey(level, _levels[level].BrickKeyAt(worldPosition));
    }

    /// <summary>
    /// The distance from the centre at which a level's exclusive contribution begins, in metres.
    /// </summary>
    /// <remarks>
    /// Exposed because it delimits the region only this level can resolve, and therefore the region its
    /// meshes are responsible for drawing. Everything inside this radius is covered by a finer level at
    /// better resolution and is deliberately not meshed here.
    /// </remarks>
    public float InnerRadius(int level)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, _levels.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        return level == 0 ? 0.0f : _levels[level - 1].HalfExtent;
    }

    /// <summary>The distance from the centre at which this level's contribution ends, in metres.</summary>
    public float OuterRadius(int level)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, _levels.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        return _levels[level].HalfExtent;
    }

    private ClipmapLevel BuildLevel(int level, float3 center) =>
        new(level, Settings, ClipmapLevel.SnapAnchor(center, Settings, level));
}
