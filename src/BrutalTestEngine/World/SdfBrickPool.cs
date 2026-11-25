namespace BrutalTestEngine.World;

/// <summary>
/// The clipmap's resident brick set: the mapping from a resident brick's slot to a physical brick id, and
/// the pool of free ids.
/// </summary>
/// <remarks>
/// <para>
/// This is the indirection table the whole design rests on. A brick that intersects a surface is given an
/// id in <c>[0, Capacity)</c>; a brick that does not intersect one is never allocated and costs nothing at
/// all. In a world that is mostly air and mostly solid interior — which every terrain is — the difference
/// between "one slot per grid cell" and "one slot per surface cell" is one to two orders of magnitude of
/// both memory and meshing work.
/// </para>
/// <para>
/// Ids are handed out from a free list and are stable for as long as the brick is resident, so a mesh built
/// last frame is still addressed by the same id this frame. Releasing an id and reusing it means the mesh
/// at that id is stale, which is why <see cref="Release"/> reports the id it freed and the caller is
/// expected to invalidate that mesh.
/// </para>
/// <para>
/// The table itself is a flat array indexed by slot, which is what lets the GPU read it directly: a task
/// shader walks the levels' slots and reads the id for each, with no hashing and no pointer chase.
/// </para>
/// </remarks>
public sealed class SdfBrickPool
{
    /// <summary>
    /// The reserved id for "this slot holds no surface brick".
    /// </summary>
    /// <remarks>
    /// Zero would be a valid id, so the sentinel has to be something the allocator can never hand out. It is
    /// also what an uninitialised buffer full of zeroes must not accidentally decode as, which is why the
    /// table is explicitly filled with this value at construction rather than left at zero.
    /// </remarks>
    public const int Empty = -1;

    private readonly int[] _idsBySlot;
    private readonly int[] _free;
    private readonly int[] _slotById;
    private int _freeCount;
    private int _allocated;

    /// <summary>Creates a pool covering <paramref name="slotCount"/> slots, with one id per slot.</summary>
    public SdfBrickPool(int slotCount)
        : this(slotCount, slotCount) { }

    /// <summary>
    /// Creates a pool covering <paramref name="slotCount"/> slots but handing out at most
    /// <paramref name="idCount"/> ids.
    /// </summary>
    /// <remarks>
    /// The two counts are separate because they are owned by different things. The slot count is a property
    /// of the clipmap's shape — every slot of every level needs a table entry whether or not it is ever
    /// allocated. The id count is a budget owned by whoever consumes the ids, and it is what bounds the
    /// consumer's per-brick arrays. Sizing the id space from the slot count would let the pool hand out an id
    /// that the consumer has no storage for, so the budget is passed in explicitly and the pool reports
    /// itself full once it is spent.
    /// </remarks>
    public SdfBrickPool(int slotCount, int idCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(idCount, 1);

        _idsBySlot = new int[slotCount];
        _slotById = new int[idCount];
        _free = new int[idCount];

        _idsBySlot.AsSpan().Fill(Empty);
        _slotById.AsSpan().Fill(Empty);

        // Filled in descending order so ids are handed out ascending, which keeps a fresh world's brick
        // ids in roughly spatial order and makes the vertex pool's early regions hot.
        for (var i = 0; i < idCount; i++)
            _free[i] = idCount - 1 - i;

        _freeCount = idCount;
    }

    /// <summary>Total number of slots the pool can allocate.</summary>
    public int Capacity => _idsBySlot.Length;

    /// <summary>Total number of brick ids the pool can hand out at once.</summary>
    public int IdCapacity => _slotById.Length;

    /// <summary>Number of slots currently holding a surface brick.</summary>
    public int AllocatedCount => _allocated;

    /// <summary>True when no slot is free.</summary>
    public bool IsFull => _freeCount == 0;

    /// <summary>
    /// The physical brick id for a slot, or <see cref="Empty"/> when the slot holds no surface brick.
    /// </summary>
    public int IdOf(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, _idsBySlot.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        return _idsBySlot[slot];
    }

    /// <summary>The slot a physical brick id currently belongs to, or <see cref="Empty"/> if it is free.</summary>
    public int SlotOf(int id)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, _slotById.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(id);

        return _slotById[id];
    }

    /// <summary>
    /// Assigns a physical brick id to a slot, returning it, or <see cref="Empty"/> and changing nothing when
    /// the pool is full.
    /// </summary>
    /// <remarks>
    /// Returning a sentinel rather than throwing is deliberate: the pool running out is a normal condition —
    /// it means the clipmap tried to keep more surface bricks resident than the budget allows — and the
    /// caller's correct response is to drop the allocation and keep rendering, not to abort a frame.
    /// </remarks>
    public int Allocate(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, _idsBySlot.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        if (_idsBySlot[slot] != Empty)
            return _idsBySlot[slot];

        if (_freeCount == 0)
            return Empty;

        var id = _free[--_freeCount];

        _idsBySlot[slot] = id;
        _slotById[id] = slot;
        _allocated++;

        return id;
    }

    /// <summary>
    /// Frees a slot's brick id, returning the id that was released, or <see cref="Empty"/> when the slot
    /// held nothing.
    /// </summary>
    public int Release(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, _idsBySlot.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);

        var id = _idsBySlot[slot];
        if (id == Empty)
            return Empty;

        _idsBySlot[slot] = Empty;
        _slotById[id] = Empty;
        _free[_freeCount++] = id;
        _allocated--;

        return id;
    }

    /// <summary>Frees every slot.</summary>
    public void Clear()
    {
        _idsBySlot.AsSpan().Fill(Empty);
        _slotById.AsSpan().Fill(Empty);

        for (var i = 0; i < _free.Length; i++)
            _free[i] = _free.Length - 1 - i;

        _freeCount = _free.Length;
        _allocated = 0;
    }

    /// <summary>
    /// Copies the slot-to-id table into <paramref name="destination"/> for upload to the GPU.
    /// </summary>
    /// <returns>The number of entries written.</returns>
    public int SnapshotTable(Span<int> destination)
    {
        var count = Math.Min(destination.Length, _idsBySlot.Length);
        _idsBySlot.AsSpan(0, count).CopyTo(destination);

        return count;
    }
}
