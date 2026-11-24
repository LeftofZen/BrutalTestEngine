using BrutalTestEngine.Logging;
using NUnit.Framework;
using Serilog.Events;

namespace BrutalTestEngine.Tests.Logging;

/// <summary>
/// Tests for the debug window's log ring buffer.
/// </summary>
/// <remarks>
/// The buffer is written from the logging pipeline's background thread and read from the main thread every
/// frame, so the ordering and the overwrite behaviour are the parts worth pinning down: a snapshot that
/// returns events out of order, or that reports a count larger than the buffer, would show up as a garbled
/// window rather than as a crash.
/// </remarks>
[TestFixture]
public sealed class LogEventBufferTests
{
    private static LogEntry Entry(string message, LogEventLevel level = LogEventLevel.Information) =>
        new(level, 0.0, "test", "thread", message);

    [Test]
    public void A_new_buffer_is_empty()
    {
        var buffer = new LogEventBuffer(4);

        Assert.Multiple(() =>
        {
            Assert.That(buffer.Count, Is.Zero);
            Assert.That(buffer.OverwrittenCount, Is.Zero);
            Assert.That(buffer.Capacity, Is.EqualTo(4));
        });
    }

    [Test]
    public void Snapshot_returns_events_oldest_first()
    {
        var buffer = new LogEventBuffer(4);
        buffer.Add(Entry("first"));
        buffer.Add(Entry("second"));
        buffer.Add(Entry("third"));

        var destination = new LogEntry[4];
        var count = buffer.Snapshot(destination);

        Assert.That(count, Is.EqualTo(3));
        Assert.That(
            destination.Take(count).Select(e => e.Message),
            Is.EqualTo(new[] { "first", "second", "third" })
        );
    }

    [Test]
    public void A_full_buffer_overwrites_the_oldest_event()
    {
        var buffer = new LogEventBuffer(3);
        buffer.Add(Entry("first"));
        buffer.Add(Entry("second"));
        buffer.Add(Entry("third"));
        buffer.Add(Entry("fourth"));

        var destination = new LogEntry[3];
        var count = buffer.Snapshot(destination);

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(3));
            Assert.That(
                destination.Select(e => e.Message),
                Is.EqualTo(new[] { "second", "third", "fourth" })
            );
            Assert.That(buffer.OverwrittenCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void The_count_never_exceeds_the_capacity()
    {
        var buffer = new LogEventBuffer(2);
        for (var i = 0; i < 100; i++)
            buffer.Add(Entry($"event {i}"));

        Assert.Multiple(() =>
        {
            Assert.That(buffer.Count, Is.EqualTo(2));
            Assert.That(buffer.OverwrittenCount, Is.EqualTo(98));
        });
    }

    [Test]
    public void Snapshot_writes_no_more_than_the_destination_holds()
    {
        var buffer = new LogEventBuffer(8);
        for (var i = 0; i < 8; i++)
            buffer.Add(Entry($"event {i}"));

        var destination = new LogEntry[3];
        var count = buffer.Snapshot(destination);

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(3));
            // The most recent events are the ones worth keeping when the caller's buffer is smaller.
            Assert.That(
                destination.Select(e => e.Message),
                Is.EqualTo(new[] { "event 5", "event 6", "event 7" })
            );
        });
    }

    [Test]
    public void Clear_empties_the_buffer_but_keeps_the_overwrite_count()
    {
        var buffer = new LogEventBuffer(2);
        buffer.Add(Entry("first"));
        buffer.Add(Entry("second"));
        buffer.Add(Entry("third"));

        buffer.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(buffer.Count, Is.Zero);
            Assert.That(buffer.Snapshot(new LogEntry[2]), Is.Zero);
            // The count is a session total, not a measure of what is currently retained, so clearing the
            // window must not make it look like nothing was ever dropped.
            Assert.That(buffer.OverwrittenCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Concurrent_writers_do_not_lose_or_corrupt_events()
    {
        const int writers = 4;
        const int perWriter = 500;
        var buffer = new LogEventBuffer(writers * perWriter);

        var threads = new Thread[writers];
        for (var w = 0; w < writers; w++)
        {
            var id = w;
            threads[w] = new Thread(() =>
            {
                for (var i = 0; i < perWriter; i++)
                    buffer.Add(Entry($"{id}:{i}"));
            });
        }

        foreach (var thread in threads)
            thread.Start();
        foreach (var thread in threads)
            thread.Join();

        var destination = new LogEntry[writers * perWriter];
        var count = buffer.Snapshot(destination);

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(writers * perWriter));
            Assert.That(buffer.OverwrittenCount, Is.Zero);
            // Every event must survive exactly once: a torn write would show up as a duplicate or a gap.
            Assert.That(destination.Take(count).Select(e => e.Message).Distinct().Count(), Is.EqualTo(count));
        });
    }

    [Test]
    public void A_capacity_below_one_is_rejected()
    {
        Assert.That(() => new LogEventBuffer(0), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
