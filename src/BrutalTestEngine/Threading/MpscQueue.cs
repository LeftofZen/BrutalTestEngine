using System.Collections.Concurrent;

namespace BrutalTestEngine.Threading;

/// <summary>
/// A multi-producer, single-consumer queue. Any number of threads may enqueue; exactly one consumer
/// thread dequeues. The consumer may block waiting for work.
/// </summary>
/// <remarks>
/// This is the handoff used for work that originates on more than one thread (for example IO
/// requests posted from the simulation and from the main thread) but must be executed serially on a
/// dedicated worker.
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
        _available.Release();
    }

    /// <summary>Attempts to dequeue without blocking.</summary>
    public bool TryDequeue(out T item)
    {
        if (_queue.TryDequeue(out item!))
            return true;

        item = default!;
        return false;
    }

    /// <summary>
    /// Blocks until an item is available or the timeout elapses. Returns false on timeout, or when the
    /// queue has been completed and drained.
    /// </summary>
    public bool WaitDequeue(out T item, int timeoutMilliseconds)
    {
        if (_queue.TryDequeue(out item!))
            return true;

        if (_completed)
        {
            item = default!;
            return false;
        }

        if (!_available.Wait(timeoutMilliseconds))
        {
            item = default!;
            return false;
        }

        if (_queue.TryDequeue(out item!))
            return true;

        item = default!;
        return false;
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
