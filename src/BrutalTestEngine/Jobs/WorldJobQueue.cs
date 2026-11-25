namespace BrutalTestEngine.Jobs;

/// <summary>
/// A bounded multi-producer, multi-consumer queue of world-generation jobs.
/// </summary>
/// <remarks>
/// <para>
/// Bounded, and that is the important property. An unbounded job queue turns a slow worker into unbounded
/// memory growth: a camera moving quickly through a large world can identify thousands of bricks that need
/// meshing in a single frame, and if all of them are enqueued the queue becomes a leak that is
/// indistinguishable from a stall. A bounded queue makes the backpressure explicit — <see cref="TryEnqueue"/>
/// returns false and the producer asks again next frame, by which time the camera's request set has been
/// recomputed and many of those bricks may not be wanted at all.
/// </para>
/// <para>
/// The queue is a lock-protected ring rather than a lock-free structure, and that is a deliberate trade.
/// The work behind one job is tens of microseconds of noise evaluation and surface extraction, so the lock
/// is held for a few hundred nanoseconds around a pair of index updates — well under a percent of the work
/// it guards. A lock-free ring would earn its keep if jobs were sub-microsecond; it would also cost far
/// more to get right than the contention it removes, and the correctness of the full/empty conditions and
/// the wake accounting matters more here than a fraction of a percent.
/// </para>
/// </remarks>
public sealed class WorldJobQueue : IDisposable
{
    private readonly WorldJob[] _slots;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly SemaphoreSlim _space;

    private int _head;
    private int _tail;
    private int _count;
    private bool _completed;
    private bool _disposed;
    private long _enqueued;
    private long _rejected;

    /// <summary>Creates a queue able to hold <paramref name="capacity"/> jobs.</summary>
    public WorldJobQueue(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _slots = new WorldJob[capacity];
        _space = new SemaphoreSlim(capacity);
    }

    /// <summary>Maximum number of jobs the queue can hold.</summary>
    public int Capacity => _slots.Length;

    /// <summary>Number of jobs currently waiting to be dequeued.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>True once <see cref="Complete"/> has been called.</summary>
    public bool IsCompleted
    {
        get
        {
            lock (_gate)
                return _completed;
        }
    }

    /// <summary>Total number of jobs ever enqueued.</summary>
    public long EnqueuedCount => Interlocked.Read(ref _enqueued);

    /// <summary>Number of enqueue attempts rejected because the queue was full.</summary>
    public long RejectedCount => Interlocked.Read(ref _rejected);

    /// <summary>
    /// Tries to enqueue a job without blocking. Returns false when the queue is full.
    /// </summary>
    /// <remarks>
    /// Non-blocking on purpose: the producer is the world streamer running inside a frame, and a frame must
    /// never wait for a worker. Rejection is the correct signal — it means the workers are behind, and the
    /// streamer's answer is to ask again later rather than to stall the frame.
    /// </remarks>
    public bool TryEnqueue(in WorldJob job)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_space.Wait(0))
        {
            Interlocked.Increment(ref _rejected);
            return false;
        }

        lock (_gate)
        {
            if (_completed)
            {
                // Give the space token back, so a completed queue does not leak capacity.
                _space.Release();
                return false;
            }

            _slots[_tail] = job;
            _tail = (_tail + 1) % _slots.Length;
            _count++;
        }

        Interlocked.Increment(ref _enqueued);
        _available.Release();
        return true;
    }

    /// <summary>
    /// Blocks until a job is available or <paramref name="timeoutMilliseconds"/> elapses.
    /// </summary>
    /// <remarks>
    /// The wait is re-checked against the queue rather than trusted, because <see cref="Complete"/> releases
    /// the semaphore too; a consumer has to exit promptly on shutdown instead of dequeuing a phantom job.
    /// </remarks>
    public bool WaitDequeue(out WorldJob job, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (true)
        {
            if (TryDequeue(out job))
                return true;

            if (IsCompleted)
            {
                job = default;
                return false;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                job = default;
                return false;
            }

            _available.Wait((int)Math.Min(remaining, int.MaxValue));
        }
    }

    /// <summary>Attempts to dequeue without blocking.</summary>
    public bool TryDequeue(out WorldJob job)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                job = default;
                return false;
            }

            job = _slots[_head];
            _head = (_head + 1) % _slots.Length;
            _count--;
        }

        // Tokens are balanced outside the gate: consuming one available signal and returning one space
        // signal. Draining non-blockingly keeps the counters in step even when a wake was spurious.
        _available.Wait(0);
        _space.Release();
        return true;
    }

    /// <summary>Wakes every waiting consumer and refuses further enqueues.</summary>
    public void Complete()
    {
        lock (_gate)
            _completed = true;

        // One release per possible waiter, so a blocked consumer cannot be left asleep on a queue that will
        // never be signalled again.
        for (var i = 0; i < Environment.ProcessorCount + 1; i++)
            _available.Release();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _available.Dispose();
        _space.Dispose();
    }
}
