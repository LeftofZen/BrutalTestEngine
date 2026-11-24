namespace BrutalTestEngine.Simulation;

/// <summary>
/// A struct-of-arrays store for the simulation's entities: one flat array per component, and a dense
/// prefix of live entities.
/// </summary>
/// <remarks>
/// <para>
/// This is the engine's data-oriented storage primitive, and it is deliberately not an object graph. An
/// array-of-structs layout would put every entity's position, velocity and radius in one cache line, so a
/// pass that only integrates positions would still pull the radius and the age through the cache. Here each
/// pass touches exactly the arrays it needs, in order, with no pointer chasing and no per-entity allocation.
/// </para>
/// <para>
/// Entities are dense: every live entity occupies an index in <c>[0, <see cref="Count"/>)</c>. Removal is a
/// swap with the last live entity, which keeps the arrays packed and makes iteration a plain counted loop
/// over a contiguous range. The cost is that an index is not stable across a removal, so callers hold
/// indices only within one pass — which is exactly how the tick uses them.
/// </para>
/// <para>
/// Nothing here is virtual and nothing here allocates after construction. The arrays grow geometrically and
/// are reused for the lifetime of the world, so a steady-state tick performs no managed allocations at all.
/// </para>
/// <para>
/// The arrays are exposed as <see cref="Span{T}"/> over the live prefix rather than as the backing arrays, so
/// a caller cannot accidentally read a stale slot past <see cref="Count"/> and cannot resize the store out
/// from under another pass.
/// </para>
/// </remarks>
public sealed class EntityStore
{
    private const int DefaultCapacity = 256;

    private float[] _positionX;
    private float[] _positionY;
    private float[] _positionZ;
    private float[] _velocityX;
    private float[] _velocityY;
    private float[] _velocityZ;
    private float[] _radius;
    private float[] _age;

    private int _count;

    /// <summary>Creates a store with room for <paramref name="capacity"/> entities.</summary>
    public EntityStore(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        capacity = Math.Max(capacity, 1);

        _positionX = new float[capacity];
        _positionY = new float[capacity];
        _positionZ = new float[capacity];
        _velocityX = new float[capacity];
        _velocityY = new float[capacity];
        _velocityZ = new float[capacity];
        _radius = new float[capacity];
        _age = new float[capacity];
    }

    /// <summary>Number of live entities. They occupy indices <c>[0, Count)</c>.</summary>
    public int Count => _count;

    /// <summary>Number of entities the store can hold before it has to grow.</summary>
    public int Capacity => _positionX.Length;

    /// <summary>X positions of the live entities.</summary>
    public Span<float> PositionX => _positionX.AsSpan(0, _count);

    /// <summary>Y positions of the live entities.</summary>
    public Span<float> PositionY => _positionY.AsSpan(0, _count);

    /// <summary>Z positions of the live entities.</summary>
    public Span<float> PositionZ => _positionZ.AsSpan(0, _count);

    /// <summary>X velocities of the live entities.</summary>
    public Span<float> VelocityX => _velocityX.AsSpan(0, _count);

    /// <summary>Y velocities of the live entities.</summary>
    public Span<float> VelocityY => _velocityY.AsSpan(0, _count);

    /// <summary>Z velocities of the live entities.</summary>
    public Span<float> VelocityZ => _velocityZ.AsSpan(0, _count);

    /// <summary>Collision radii of the live entities.</summary>
    public Span<float> Radius => _radius.AsSpan(0, _count);

    /// <summary>Simulated age in seconds of the live entities.</summary>
    public Span<float> Age => _age.AsSpan(0, _count);

    /// <summary>
    /// Appends an entity and returns its index. The index is only valid until the next removal.
    /// </summary>
    public int Add(
        float x,
        float y,
        float z,
        float velocityX = 0.0f,
        float velocityY = 0.0f,
        float velocityZ = 0.0f,
        float radius = 0.5f
    )
    {
        if (_count == _positionX.Length)
            Grow(_count * 2);

        var index = _count++;

        _positionX[index] = x;
        _positionY[index] = y;
        _positionZ[index] = z;
        _velocityX[index] = velocityX;
        _velocityY[index] = velocityY;
        _velocityZ[index] = velocityZ;
        _radius[index] = radius;
        _age[index] = 0.0f;

        return index;
    }

    /// <summary>
    /// Removes the entity at <paramref name="index"/> by moving the last live entity into its slot.
    /// </summary>
    /// <remarks>
    /// Swap-remove rather than a shift, because a shift is O(n) per removal and the tick removes entities in
    /// bulk. The consequence — that the last entity's index changes — is why indices are treated as
    /// pass-local rather than as handles.
    /// </remarks>
    public void RemoveAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var last = --_count;
        if (index == last)
            return;

        _positionX[index] = _positionX[last];
        _positionY[index] = _positionY[last];
        _positionZ[index] = _positionZ[last];
        _velocityX[index] = _velocityX[last];
        _velocityY[index] = _velocityY[last];
        _velocityZ[index] = _velocityZ[last];
        _radius[index] = _radius[last];
        _age[index] = _age[last];
    }

    /// <summary>Removes every entity, keeping the allocated arrays.</summary>
    public void Clear() => _count = 0;

    /// <summary>
    /// Advances every entity by one step: position integrates velocity, velocity integrates gravity, and age
    /// advances.
    /// </summary>
    /// <remarks>
    /// One pass per array rather than one pass per entity. Each loop walks a single contiguous array, so the
    /// hardware prefetcher sees a pure sequential stream and the compiler can vectorise the arithmetic. This
    /// is the whole point of the layout: the same work, in the same order, touching a third of the memory.
    /// </remarks>
    public void Integrate(float deltaSeconds, float gravityY)
    {
        if (_count == 0)
            return;

        var velocityY = _velocityY.AsSpan(0, _count);
        for (var i = 0; i < velocityY.Length; i++)
            velocityY[i] += gravityY * deltaSeconds;

        var positionX = _positionX.AsSpan(0, _count);
        var velocityX = _velocityX.AsSpan(0, _count);
        for (var i = 0; i < positionX.Length; i++)
            positionX[i] += velocityX[i] * deltaSeconds;

        var positionY = _positionY.AsSpan(0, _count);
        for (var i = 0; i < positionY.Length; i++)
            positionY[i] += velocityY[i] * deltaSeconds;

        var positionZ = _positionZ.AsSpan(0, _count);
        var velocityZ = _velocityZ.AsSpan(0, _count);
        for (var i = 0; i < positionZ.Length; i++)
            positionZ[i] += velocityZ[i] * deltaSeconds;

        var age = _age.AsSpan(0, _count);
        for (var i = 0; i < age.Length; i++)
            age[i] += deltaSeconds;
    }

    /// <summary>
    /// Reflects any entity that has fallen below <paramref name="floorY"/> and damps its vertical velocity.
    /// </summary>
    /// <remarks>
    /// A single pass over two arrays, with the branch taken only by the entities that are actually below the
    /// floor. In a real world this is where a broadphase would hand over a candidate list; the shape of the
    /// loop — flat arrays, no allocation, no virtual call — is the part that carries over.
    /// </remarks>
    public void ResolveFloor(float floorY, float restitution = 0.5f)
    {
        if (_count == 0)
            return;

        var positionY = _positionY.AsSpan(0, _count);
        var velocityY = _velocityY.AsSpan(0, _count);

        for (var i = 0; i < positionY.Length; i++)
        {
            if (positionY[i] >= floorY)
                continue;

            positionY[i] = floorY;
            velocityY[i] = -velocityY[i] * restitution;
        }
    }

    /// <summary>
    /// Removes every entity older than <paramref name="maxAgeSeconds"/>.
    /// </summary>
    /// <remarks>
    /// Iterates backwards so a swap-remove cannot move an unvisited entity into a slot that has already been
    /// passed. Iterating forwards would silently skip the entity that was swapped in.
    /// </remarks>
    public int RemoveExpired(float maxAgeSeconds)
    {
        var removed = 0;

        for (var i = _count - 1; i >= 0; i--)
        {
            if (_age[i] < maxAgeSeconds)
                continue;

            RemoveAt(i);
            removed++;
        }

        return removed;
    }

    private void Grow(int minimumCapacity)
    {
        var capacity = Math.Max(minimumCapacity, _positionX.Length * 2);

        Array.Resize(ref _positionX, capacity);
        Array.Resize(ref _positionY, capacity);
        Array.Resize(ref _positionZ, capacity);
        Array.Resize(ref _velocityX, capacity);
        Array.Resize(ref _velocityY, capacity);
        Array.Resize(ref _velocityZ, capacity);
        Array.Resize(ref _radius, capacity);
        Array.Resize(ref _age, capacity);
    }
}
