using Brutal.Numerics;
using System.Runtime.InteropServices;

namespace BrutalTestEngine.World;

/// <summary>
/// The set of bricks whose surface data is out of date and which must be re-evaluated on the GPU.
/// </summary>
/// <remarks>
/// <para>
/// Dirty tracking is what makes deformation affordable. When terrain is carved or a dynamic object moves,
/// only the bricks the change touches have any reason to change, and the number of those is tiny next to
/// the number of bricks in view. Everything else keeps the mesh it was given and is never touched.
/// </para>
/// <para>
/// A set rather than a list, because the same brick is routinely marked dirty by several overlapping edits
/// in one tick — a carving tool brushing across a boundary marks the same brick from two sides. Marking
/// twice must cost one rebuild, not two, so membership is checked on insert.
/// </para>
/// <para>
/// Iteration is insertion-ordered, which keeps a rebuild pass deterministic and makes a replay of the same
/// edit sequence produce the same upload order.
/// </para>
/// </remarks>
public sealed class DirtyBrickSet
{
    private readonly HashSet<BrickKey> _set = [];
    private readonly List<BrickKey> _order = [];

    /// <summary>Number of bricks waiting to be rebuilt.</summary>
    public int Count => _order.Count;

    /// <summary>True when no brick needs rebuilding.</summary>
    public bool IsEmpty => _order.Count == 0;

    /// <summary>The dirty bricks, in the order they were first marked.</summary>
    public ReadOnlySpan<BrickKey> Bricks => CollectionsMarshal.AsSpan(_order);

    /// <summary>Marks a brick dirty, returning true when it was not already dirty.</summary>
    public bool Mark(BrickKey brick)
    {
        if (!_set.Add(brick))
            return false;

        _order.Add(brick);

        return true;
    }

    /// <summary>
    /// Marks a brick dirty only if it is resident in the given clipmap.
    /// </summary>
    /// <remarks>
    /// Edits arrive from gameplay, which knows a world position and not which bricks the clipmap currently
    /// has resident. Filtering here means an edit outside the clipmap, or in a level that is not carrying
    /// the brick, is dropped rather than queued forever.
    /// </remarks>
    public bool MarkAt(SdfClipmap clipmap, BrickKey brick)
    {
        ArgumentNullException.ThrowIfNull(clipmap);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(brick.Level, clipmap.LevelCount);
        ArgumentOutOfRangeException.ThrowIfNegative(brick.Level);

        var level = clipmap.Level(brick.Level);
        if (!level.Contains(brick.Center(clipmap.Settings)))
            return false;

        // A brick is only worth rebuilding if this level is the one that owns it; a finer level covering the
        // same ground will mesh it at better resolution, so marking the coarse copy is wasted work.
        if (clipmap.SelectLevel(brick.Center(clipmap.Settings)) != brick.Level)
            return false;

        return Mark(brick);
    }

    /// <summary>
    /// Marks every brick whose volume a world-space sphere overlaps.
    /// </summary>
    /// <returns>The number of bricks newly marked.</returns>
    /// <remarks>
    /// Edits in this engine are spheres — a carving tool, an explosion, a digging brush — so the overlap
    /// query is the only shape that is needed. The traversal is over the affected brick lattice range rather
    /// than over the clipmap's whole brick set, which is what keeps an edit O(edit size) instead of O(world
    /// size).
    /// </remarks>
    public int MarkSphere(SdfClipmap clipmap, float3 center, float radius, int level)
    {
        ArgumentNullException.ThrowIfNull(clipmap);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(level, clipmap.LevelCount);
        ArgumentOutOfRangeException.ThrowIfNegative(level);

        if (radius < 0.0f)
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "Must not be negative.");

        var brickSize = clipmap.Settings.BrickSize(level);

        // A sphere that sits entirely inside one brick still has to mark it, so the minimum and maximum are
        // taken over the sphere's own box rather than over its centre.
        var min = new int3(
            Floor((center.X - radius) / brickSize),
            Floor((center.Y - radius) / brickSize),
            Floor((center.Z - radius) / brickSize)
        );
        var max = new int3(
            Floor((center.X + radius) / brickSize),
            Floor((center.Y + radius) / brickSize),
            Floor((center.Z + radius) / brickSize)
        );

        var marked = 0;

        for (var z = min.Z; z <= max.Z; z++)
        {
            for (var y = min.Y; y <= max.Y; y++)
            {
                for (var x = min.X; x <= max.X; x++)
                {
                    if (MarkAt(clipmap, new BrickKey(level, new int3(x, y, z))))
                        marked++;
                }
            }
        }

        return marked;
    }

    /// <summary>Marks a brick clean, removing it from the set.</summary>
    /// <remarks>
    /// Removal is not swap-with-last like the entity store's, because after a rebuild the caller usually
    /// wants to rebuild the rest of the batch in order, and swapping the last element into the slot being
    /// removed would skip it.
    /// </remarks>
    public bool Clear(BrickKey brick)
    {
        if (!_set.Remove(brick))
            return false;

        _order.Remove(brick);

        return true;
    }

    /// <summary>Marks every brick clean, keeping the allocated capacity.</summary>
    public void Clear()
    {
        _set.Clear();
        _order.Clear();
    }

    /// <summary>True when the set holds <paramref name="brick"/>.</summary>
    public bool Contains(BrickKey brick) => _set.Contains(brick);

    /// <summary>
    /// Copies the dirty bricks into <paramref name="destination"/> for upload to the GPU.
    /// </summary>
    /// <returns>The number of keys written.</returns>
    public int Snapshot(Span<BrickKey> destination)
    {
        var count = Math.Min(destination.Length, _order.Count);
        CollectionsMarshal.AsSpan(_order).Slice(0, count).CopyTo(destination);

        return count;
    }

    private static int Floor(float value) => (int)MathF.Floor(value);
}
