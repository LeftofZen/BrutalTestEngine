namespace BrutalTestEngine.Threading;

/// <summary>
/// A lock-free single-writer / single-reader handoff that never blocks either side.
/// </summary>
/// <remarks>
/// <para>
/// The writer publishes a new value without waiting for the reader, and the reader always takes the
/// most recently published value without waiting for the writer. Three slots are used so the writer
/// can fill one while the reader reads another and the third stays free to swap in.
/// </para>
/// <para>
/// This is the standard way to decouple a simulation tick from a render loop: the simulation must
/// never stall on the renderer, and the renderer must never stall on the simulation. A plain lock
/// would couple their timings; a single slot would tear.
/// </para>
/// <para>
/// Ownership of the middle slot is published through a single word, so handing a filled slot to the
/// reader and taking a spare back is one indivisible compare-exchange. Swapping pairs of references
/// instead would let the reader observe the exchange half-done, and it would then either miss a
/// published version or take the slot the writer was still filling.
/// </para>
/// <para>
/// The writer must be a single thread and the reader must be a single thread. Different readers or
/// writers require external synchronisation.
/// </para>
/// </remarks>
public sealed class TripleBuffer<T>
    where T : class
{
    // The middle slot is published as "index | DirtyFlag" in one word. Dirty means it holds a value
    // the reader has not taken yet.
    private const int DirtyFlag = 1 << 30;
    private const int IndexMask = DirtyFlag - 1;

    private readonly T[] _slots;

    // Written and read by the writer only.
    private int _back = 0;

    // Written and read by the reader only.
    private int _front = 1;

    // Shared. Mutated only through Interlocked so publication is indivisible.
    private int _middle = 2 | DirtyFlag;

    private long _version;

    public TripleBuffer(Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _slots = [factory(), factory(), factory()];
    }

    /// <summary>
    /// Number of values published so far. The reader can compare this against the version it last
    /// observed to tell whether a new snapshot is available.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Publishes a value. The writer fills its private slot and swaps it in as the latest, so the
    /// reader is never blocked and never sees a partially written value.
    /// </summary>
    public void Publish(Action<T> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        // This thread is the only writer, so filling the back slot needs no synchronisation and no
        // reader can be looking at it.
        write(_slots[_back]);

        // Offer the filled slot as the new middle in one step and take back whatever was there. If the
        // reader had already claimed the previous middle, the flag is clear and the slot we receive is
        // simply a spare to refill; either way we end up owning exactly one slot.
        var previous = Interlocked.Exchange(ref _middle, _back | DirtyFlag);
        _back = previous & IndexMask;

        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Reads the most recently published value. The returned instance stays valid until this thread
    /// calls <see cref="TryRead"/> again, so callers should copy out what they need rather than
    /// holding the reference.
    /// </summary>
    public bool TryRead(out T value, out long version)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _middle);

            // Nothing published since the last read, so the front slot is still the newest value.
            if ((observed & DirtyFlag) == 0)
                break;

            // Claim the published slot and hand our old front slot back as the spare, but only if the
            // word still says what we read: a publish that landed in between must not be discarded.
            if (Interlocked.CompareExchange(ref _middle, _front, observed) == observed)
            {
                _front = observed & IndexMask;
                break;
            }
        }

        value = _slots[_front];
        version = Version;
        return true;
    }
}
