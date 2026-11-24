using System.Collections.Concurrent;

namespace BrutalTestEngine.Threading;

/// <summary>
/// A multi-producer, single-consumer queue. Any number of threads may enqueue; exactly one consumer
/// thread dequeues. The consumer may block waiting for work.
/// </summary>
/// <remarks>
/// <para>
/// This is the handoff used for work that originates on more than one thread (for example IO
/// requests posted from the simulation and from the main thread) but must be executed serially on a
/// dedicated worker.
/// </para>
/// <para>
/// The wake signal is a count of items enqueued and not yet taken, so every successful dequeue consumes
/// one. That accounting is what makes mixing the two entry points safe: a consumer that drains with
/// <see cref="TryDequeue"/> leaves no growing surplus of signals behind to make a later
/// <see cref="WaitDequeue"/> return immediately instead of blocking.
/// </para>
/// <para>
/// A signal can still arrive a moment after the item it belongs to was already taken, because the
/// producer releases it after the item becomes visible. That leaves a transient surplus, which is why
/// <see cref="WaitDequeue"/> re-checks the queue after every wake rather than trusting one wake to mean
/// one item. The surplus is bounded by the number of enqueues in flight, so it cannot accumulate.
/// </para>
/// </remarks>
public sealed class MpscQueue<T> : IDisposable
{
    private readonly ConcurrentQueue<T> _queue = new();
    private readonly SemaphoreSlim _available = new(0);
    private volatile bool _completed;
    private bool _disposed;

    /// <summary>Number of items currently waiting to be dequeued.</summary>
    public int Count => _queue.Count;

    /// <summary>True once <see cref="Complete"/> has been called.</summary>
    public bool IsCompleted => _completed;

    /// <summary>Enqueues an item. Safe to call from any thread.</summary>
    public void Enqueue(T item)
    {
        _queue.Enqueue(item);

        // Released after the item is visible, so a consumer woken by the signal always finds work. The
        // reverse order would wake a waiter that then finds an empty queue and has to wait again.
        _available.Release();
    }

    /// <summary>Attempts to dequeue without blocking.</summary>
    public bool TryDequeue(out T item)
    {
        if (!_queue.TryDequeue(out item!))
        {
            item = default!;
            return false;
        }

        // Consume the matching wake signal. The producer may not have released it yet, in which case this
        // takes a signal belonging to another item; the count stays balanced either way, and WaitDequeue
        // tolerates the surplus by re-checking the queue after each wake.
        _available.Wait(0);
        return true;
    }

    /// <summary>
    /// Blocks until an item is available or the timeout elapses. Returns false on timeout, or when the
    /// queue has been completed and drained.
    /// </summary>
    /// <remarks>
    /// The wait is a loop rather than a single wait-then-take, because a wake signal can be spurious: it
    /// may belong to an item another call already took, or to a completion. Re-checking the queue after
    /// each wake is what makes the timeout a bound on total time rather than on one wait.
    /// </remarks>
    public bool WaitDequeue(out T item, int timeoutMilliseconds)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;

        while (true)
        {
            if (_queue.TryDequeue(out item!))
            {
                _available.Wait(0);
                return true;
            }

            if (_completed)
            {
                item = default!;
                return false;
            }

            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                item = default!;
                return false;
            }

            _available.Wait((int)Math.Min(remaining, int.MaxValue));
        }
    }

    /// <summary>
    /// Signals that no further items will be enqueued. The consumer should drain and exit.
    /// </summary>
    public void Complete()
    {
        _completed = true;
        _available.Release();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _available.Dispose();
    }
}
