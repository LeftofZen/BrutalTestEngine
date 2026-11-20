using BrutalTestEngine.Engine;
using BrutalTestEngine.Threading;

namespace BrutalTestEngine.Io;

/// <summary>
/// A unit of file, database or other blocking IO work, executed on the IO thread.
/// </summary>
/// <param name="Name">A short label used in diagnostics.</param>
/// <param name="Work">The blocking work to run.</param>
/// <param name="OnCompleted">
/// Invoked on the IO thread once <paramref name="Work"/> returns. Exceptions are captured and passed
/// to <see cref="IoThread.Faulted"/> instead of tearing the thread down.
/// </param>
public sealed record IoJob(string Name, Action Work, Action<Exception?>? OnCompleted = null);

/// <summary>
/// Owns the IO thread. File, database and other blocking IO is queued here so that neither the
/// simulation tick nor the render loop ever blocks on a disk or a database.
/// </summary>
/// <remarks>
/// <para>
/// A single dedicated thread (rather than the thread pool) is deliberate: IO ordering matters for
/// save games and database writes, and a dedicated thread keeps the engine's thread count and
/// priorities predictable. Callers that need parallelism can add more instances.
/// </para>
/// <para>
/// Jobs are executed in submission order. Completion callbacks run on the IO thread, so they must not
/// block; anything that needs to touch the simulation should post back to it instead.
/// </para>
/// </remarks>
public sealed class IoThread : IDisposable
{
    private const int PollTimeoutMilliseconds = 16;

    private readonly MpscQueue<IoJob> _jobs = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cancellation = new();
        private readonly ThreadMetrics _jobMetrics;
    private long _completed;
    private long _failed;
    private volatile bool _disposed;

        /// <param name="timers">Where this thread records how long each job took.</param>
        public IoThread(ThreadTimerRegistry timers)
    {
            ArgumentNullException.ThrowIfNull(timers);

            _jobMetrics = timers.Get(ThreadTimerRegistry.Keys.IoJob);

        _thread = new Thread(Run)
        {
            Name = "IO",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    /// <summary>Raised on the IO thread when a job throws.</summary>
    public event Action<IoJob, Exception>? Faulted;

    /// <summary>Number of jobs completed successfully.</summary>
    public long CompletedCount => Interlocked.Read(ref _completed);

    /// <summary>Number of jobs that threw.</summary>
    public long FailedCount => Interlocked.Read(ref _failed);

    /// <summary>Queues a job. Safe to call from any thread; never blocks.</summary>
    public void Post(IoJob job)
    {
        if (_disposed)
            return;

        _jobs.Enqueue(job);
    }

    /// <summary>Queues a job with a name and an optional completion callback.</summary>
    public void Post(string name, Action work, Action<Exception?>? onCompleted = null) =>
        Post(new IoJob(name, work, onCompleted));

    private void Run()
    {
        var token = _cancellation.Token;

        while (!token.IsCancellationRequested)
        {
            if (!_jobs.WaitDequeue(out var job, PollTimeoutMilliseconds))
            {
                if (_jobs.IsCompleted)
                    break;

                continue;
            }

            Exception? error = null;
                        var jobStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                job.Work();
                Interlocked.Increment(ref _completed);
            }
            catch (Exception exception)
            {
                error = exception;
                Interlocked.Increment(ref _failed);
                Faulted?.Invoke(job, exception);
            }
                        finally
                        {
                            // Measured over the work only, not the completion callback: the callback is bookkeeping the
                            // caller supplied, and folding it in would blame the IO thread for the caller's code.
                            _jobMetrics.RecordSince(jobStartedAt);
                        }

            try
            {
                job.OnCompleted?.Invoke(error);
            }
            catch (Exception callbackException)
            {
                Faulted?.Invoke(job, callbackException);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cancellation.Cancel();
        _jobs.Complete();
        _thread.Join(TimeSpan.FromSeconds(2));
        _cancellation.Dispose();
        _jobs.Dispose();
    }
}
