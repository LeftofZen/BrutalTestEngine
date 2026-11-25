using BrutalTestEngine.Jobs;
using BrutalTestEngine.World;

namespace BrutalTestEngine.Tests.Jobs;

/// <summary>
/// Tests for the bounded world job queue.
/// </summary>
/// <remarks>
/// The properties that matter are the ones a hand-rolled ring gets wrong: that a full queue refuses rather
/// than corrupts, that the token accounting stays balanced across mixed enqueue and dequeue, and that a
/// waiting consumer wakes for every item. The concurrency test is a real one — several producers and several
/// consumers, with every produced item accounted for exactly once.
/// </remarks>
[TestFixture]
public sealed class WorldJobQueueTests
{
    [Test]
    public void An_empty_queue_has_no_work()
    {
        using var queue = new WorldJobQueue(4);

        Assert.Multiple(
            () =>
            {
                Assert.That(queue.Count, Is.EqualTo(0));
                Assert.That(queue.Capacity, Is.EqualTo(4));
                Assert.That(queue.TryDequeue(out _), Is.False);
                Assert.That(queue.IsCompleted, Is.False);
            }
        );
    }

    [Test]
    public void Enqueue_and_dequeue_preserve_order()
    {
        using var queue = new WorldJobQueue(8);

        for (var i = 0; i < 5; i++)
            Assert.That(queue.TryEnqueue(Job(i)), Is.True);

        Assert.That(queue.Count, Is.EqualTo(5));

        for (var i = 0; i < 5; i++)
        {
            Assert.That(queue.TryDequeue(out var job), Is.True);
            Assert.That(job.BrickId, Is.EqualTo(i));
        }

        Assert.That(queue.TryDequeue(out _), Is.False);
    }

    [Test]
    public void A_full_queue_refuses_and_counts_the_refusal()
    {
        using var queue = new WorldJobQueue(4);

        for (var i = 0; i < 4; i++)
            Assert.That(queue.TryEnqueue(Job(i)), Is.True);

        Assert.Multiple(
            () =>
            {
                Assert.That(queue.TryEnqueue(Job(4)), Is.False);
                Assert.That(queue.Count, Is.EqualTo(4));
                Assert.That(queue.RejectedCount, Is.EqualTo(1));
                Assert.That(queue.EnqueuedCount, Is.EqualTo(4));
            }
        );
    }

    [Test]
    public void A_dequeue_frees_exactly_one_slot()
    {
        using var queue = new WorldJobQueue(2);

        Assert.That(queue.TryEnqueue(Job(0)), Is.True);
        Assert.That(queue.TryEnqueue(Job(1)), Is.True);
        Assert.That(queue.TryEnqueue(Job(2)), Is.False);

        Assert.That(queue.TryDequeue(out _), Is.True);

        // A ring that leaked its space token would still refuse here, and the queue would be permanently
        // unable to fill again after a single refusal.
        Assert.That(queue.TryEnqueue(Job(3)), Is.True);
    }

    [Test]
    public void The_ring_wraps_without_losing_or_reordering_items()
    {
        using var queue = new WorldJobQueue(3);

        var produced = 0;
        var consumed = 0;

        for (var round = 0; round < 20; round++)
        {
            while (queue.TryEnqueue(Job(produced)))
                produced++;

            while (queue.TryDequeue(out var job))
            {
                Assert.That(job.BrickId, Is.EqualTo(consumed));
                consumed++;
            }
        }

        Assert.Multiple(
            () =>
            {
                Assert.That(produced, Is.GreaterThan(20));
                Assert.That(consumed, Is.EqualTo(produced));
                Assert.That(queue.Count, Is.EqualTo(0));
            }
        );
    }

    [Test]
    public void Wait_dequeue_returns_immediately_when_work_is_present()
    {
        using var queue = new WorldJobQueue(4);
        queue.TryEnqueue(Job(7));

        Assert.That(queue.WaitDequeue(out var job, 10), Is.True);
        Assert.That(job.BrickId, Is.EqualTo(7));
    }

    [Test]
    public void Wait_dequeue_times_out_on_an_empty_queue()
    {
        using var queue = new WorldJobQueue(4);

        var startedAt = Environment.TickCount64;
        var taken = queue.WaitDequeue(out _, 50);

        Assert.Multiple(
            () =>
            {
                Assert.That(taken, Is.False);
                Assert.That(Environment.TickCount64 - startedAt, Is.GreaterThanOrEqualTo(40));
            }
        );
    }

    [Test]
    public void Completion_wakes_a_blocked_consumer()
    {
        using var queue = new WorldJobQueue(4);

        var tookJob = false;
        var consumer = new Thread(() =>
        {
            // Long timeout: the point is that Complete wakes it, not that the timeout expires.
            tookJob = queue.WaitDequeue(out _, 5000);
        });

        consumer.Start();
        Thread.Sleep(50);

        queue.Complete();
        consumer.Join(TimeSpan.FromSeconds(2));

        Assert.Multiple(
            () =>
            {
                Assert.That(consumer.IsAlive, Is.False);
                Assert.That(tookJob, Is.False);
                Assert.That(queue.IsCompleted, Is.True);
            }
        );
    }

    [Test]
    public void A_completed_queue_refuses_further_work()
    {
        using var queue = new WorldJobQueue(4);
        queue.Complete();

        Assert.Multiple(
            () =>
            {
                Assert.That(queue.TryEnqueue(Job(0)), Is.False);
                Assert.That(queue.EnqueuedCount, Is.EqualTo(0));
            }
        );
    }

    [Test]
    public void Several_producers_and_consumers_account_for_every_item_exactly_once()
    {
        const int ProducerCount = 4;
        const int ConsumerCount = 4;
        const int PerProducer = 500;

        using var queue = new WorldJobQueue(64);

        // Every brick id is unique and carries its producer, so a duplicated or lost item is detectable
        // rather than merely a wrong count.
        var seen = new System.Collections.Concurrent.ConcurrentBag<int>();
        var producersDone = 0;

        var producers = new Thread[ProducerCount];
        for (var p = 0; p < ProducerCount; p++)
        {
            var producer = p;
            producers[p] = new Thread(() =>
            {
                for (var i = 0; i < PerProducer; i++)
                {
                    var id = (producer * PerProducer) + i;

                    // Spin rather than drop, so the test asserts on delivery and not on backpressure.
                    while (!queue.TryEnqueue(Job(id, producer)))
                        Thread.Yield();
                }

                Interlocked.Increment(ref producersDone);
            });
        }

        var consumers = new Thread[ConsumerCount];
        for (var c = 0; c < ConsumerCount; c++)
        {
            consumers[c] = new Thread(() =>
            {
                while (true)
                {
                    if (!queue.WaitDequeue(out var job, 20))
                    {
                        if (Volatile.Read(ref producersDone) == ProducerCount && queue.Count == 0)
                            return;

                        continue;
                    }

                    seen.Add(job.BrickId);
                }
            });
        }

        foreach (var thread in producers)
            thread.Start();

        foreach (var thread in consumers)
            thread.Start();

        foreach (var thread in producers)
            Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, "a producer did not finish");

        foreach (var thread in consumers)
            Assert.That(thread.Join(TimeSpan.FromSeconds(10)), Is.True, "a consumer did not finish");

        var ordered = seen.OrderBy(x => x).ToArray();

        Assert.Multiple(
            () =>
            {
                Assert.That(ordered.Length, Is.EqualTo(ProducerCount * PerProducer));
                Assert.That(ordered.Distinct().Count(), Is.EqualTo(ordered.Length), "an item was delivered twice");
                Assert.That(ordered[0], Is.EqualTo(0));
                Assert.That(ordered[^1], Is.EqualTo((ProducerCount * PerProducer) - 1));
            }
        );
    }

    [Test]
    public void Wait_dequeue_does_not_leave_a_surplus_of_wake_signals()
    {
        using var queue = new WorldJobQueue(4);

        queue.TryEnqueue(Job(0));
        Assert.That(queue.WaitDequeue(out _, 10), Is.True);

        // A queue that left its signal behind would return immediately here instead of blocking for the full
        // timeout, which is the classic broken-wakeup bug in a semaphore-counted ring.
        var startedAt = Environment.TickCount64;
        Assert.That(queue.WaitDequeue(out _, 60), Is.False);

        Assert.That(Environment.TickCount64 - startedAt, Is.GreaterThanOrEqualTo(50));
    }

    private static WorldJob Job(int id, int level = 0) =>
        new(WorldJobKind.BuildBrick, 1, new BrickKey(level, new Brutal.Numerics.int3(id, 0, 0)), id);
}
