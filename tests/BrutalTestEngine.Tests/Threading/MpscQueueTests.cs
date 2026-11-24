using BrutalTestEngine.Threading;

namespace BrutalTestEngine.Tests.Threading;

/// <summary>
/// The queue is the handoff between a producing thread and the one thread that owns the work, so its
/// ordering and its completion semantics are what keep thread shutdown deterministic.
/// </summary>
[TestFixture]
public class MpscQueueTests
{
    [Test]
    public void Preserves_enqueue_order_from_a_single_producer()
    {
        using var queue = new MpscQueue<int>();

        for (var i = 0; i < 1000; i++)
            queue.Enqueue(i);

        Assert.That(queue.Count, Is.EqualTo(1000));

        for (var i = 0; i < 1000; i++)
        {
            Assert.That(queue.TryDequeue(out var value), Is.True);
            Assert.That(value, Is.EqualTo(i));
        }

        Assert.That(queue.TryDequeue(out _), Is.False);
    }

    [Test]
    public void TryDequeue_on_an_empty_queue_reports_failure_rather_than_blocking()
    {
        using var queue = new MpscQueue<int>();

        Assert.Multiple(() =>
        {
            Assert.That(queue.TryDequeue(out var value), Is.False);
            Assert.That(value, Is.Zero);
        });
    }

    [Test]
    public void WaitDequeue_returns_an_item_that_is_already_waiting()
    {
        using var queue = new MpscQueue<int>();
        queue.Enqueue(7);

        Assert.That(queue.WaitDequeue(out var value, 1000), Is.True);
        Assert.That(value, Is.EqualTo(7));
    }

    [Test]
    public void WaitDequeue_times_out_when_nothing_is_produced()
    {
        using var queue = new MpscQueue<int>();

        var started = Environment.TickCount64;
        var dequeued = queue.WaitDequeue(out _, 50);
        var elapsed = Environment.TickCount64 - started;

        Assert.Multiple(() =>
        {
            Assert.That(dequeued, Is.False);

            // It must actually wait, not spin-and-return, or a consumer would busy-loop.
            Assert.That(elapsed, Is.GreaterThanOrEqualTo(25), $"returned after only {elapsed}ms");
        });
    }

    [Test]
    public void WaitDequeue_is_woken_by_a_producer_on_another_thread()
    {
        using var queue = new MpscQueue<string>();

        var producer = new Thread(() =>
        {
            Thread.Sleep(50);
            queue.Enqueue("late");
        });
        producer.Start();

        Assert.That(queue.WaitDequeue(out var value, 5000), Is.True);
        Assert.That(value, Is.EqualTo("late"));

        producer.Join();
    }

    [Test]
    public void Completing_the_queue_unblocks_a_waiting_consumer()
    {
        using var queue = new MpscQueue<int>();

        var consumer = new Thread(() => Assert.That(queue.WaitDequeue(out _, 5000), Is.False));
        consumer.Start();

        Thread.Sleep(50);
        queue.Complete();

        Assert.That(consumer.Join(TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test]
    public void A_completed_queue_still_drains_what_was_already_enqueued()
    {
        using var queue = new MpscQueue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);

        queue.Complete();

        Assert.Multiple(() =>
        {
            Assert.That(queue.IsCompleted, Is.True);
            Assert.That(queue.WaitDequeue(out var first, 100), Is.True);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(queue.WaitDequeue(out var second, 100), Is.True);
            Assert.That(second, Is.EqualTo(2));
        });
    }

    [Test]
    public void Draining_with_TryDequeue_does_not_leave_a_signal_that_breaks_a_later_wait()
    {
        // The bus drains with TryDequeue only. If a successful dequeue did not consume its wake signal,
        // every posted command would leave one behind and a later WaitDequeue would return immediately
        // instead of blocking. This is the regression test for that accounting.
        using var queue = new MpscQueue<int>();

        for (var i = 0; i < 100; i++)
            queue.Enqueue(i);

        var drained = 0;
        while (queue.TryDequeue(out _))
            drained++;

        Assert.That(drained, Is.EqualTo(100));

        var started = Environment.TickCount64;
        var dequeued = queue.WaitDequeue(out _, 60);
        var elapsed = Environment.TickCount64 - started;

        Assert.Multiple(() =>
        {
            Assert.That(dequeued, Is.False);
            Assert.That(elapsed, Is.GreaterThanOrEqualTo(25), $"returned after only {elapsed}ms");
        });
    }

    [Test]
    public void A_wait_after_a_drain_still_wakes_for_a_new_item()
    {
        using var queue = new MpscQueue<int>();

        queue.Enqueue(1);
        Assert.That(queue.TryDequeue(out var first), Is.True);
        Assert.That(first, Is.EqualTo(1));

        var producer = new Thread(() =>
        {
            Thread.Sleep(40);
            queue.Enqueue(2);
        });
        producer.Start();

        Assert.That(queue.WaitDequeue(out var second, 5000), Is.True);
        Assert.That(second, Is.EqualTo(2));

        producer.Join();
    }

    [Test]
    public void Multiple_producers_are_all_drained_without_loss()
    {
        using var queue = new MpscQueue<int>();
        const int producers = 8;
        const int perProducer = 1000;

        var threads = new Thread[producers];
        for (var p = 0; p < producers; p++)
        {
            var id = p;
            threads[p] = new Thread(() =>
            {
                for (var i = 0; i < perProducer; i++)
                    queue.Enqueue(id * perProducer + i);
            });
        }

        foreach (var thread in threads)
            thread.Start();

        foreach (var thread in threads)
            thread.Join();

        // Per-producer order is guaranteed; interleaving between producers is not. So check the set,
        // not the sequence.
        var seen = new HashSet<int>();
        while (queue.TryDequeue(out var value))
            Assert.That(seen.Add(value), Is.True, $"duplicate {value}");

        Assert.That(seen, Has.Count.EqualTo(producers * perProducer));
    }
}
