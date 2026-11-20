using BrutalTestEngine.Threading;

namespace BrutalTestEngine.Tests.Threading;

/// <summary>
/// The triple buffer is what decouples the simulation from the renderer: neither side may ever block on
/// the other, and the reader must never see a half-written snapshot.
/// </summary>
[TestFixture]
public class TripleBufferTests
{
    [Test]
    public void Starts_with_whatever_the_factory_produced()
    {
        var buffer = new TripleBuffer<Snapshot>(() => new Snapshot());

        Assert.That(buffer.TryRead(out var snapshot, out var version), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Value, Is.Zero);
            Assert.That(version, Is.Zero);
        });
    }

    [Test]
    public void Publishes_so_a_reader_sees_the_new_value_and_version()
    {
        var buffer = new TripleBuffer<Snapshot>(() => new Snapshot());

        buffer.Publish(s => s.Value = 42);

        Assert.That(buffer.TryRead(out var snapshot, out var version), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Value, Is.EqualTo(42));
            Assert.That(version, Is.EqualTo(1));
        });
    }

    [Test]
    public void The_reader_always_sees_the_most_recent_publish_not_a_queue_of_them()
    {
        var buffer = new TripleBuffer<Snapshot>(() => new Snapshot());

        // A slow frame skips intermediate simulation ticks; that is the whole point of this type. The
        // reader must get the latest state, never a backlog.
        buffer.Publish(s => s.Value = 1);
        buffer.Publish(s => s.Value = 2);
        buffer.Publish(s => s.Value = 3);

        Assert.That(buffer.TryRead(out var snapshot, out _), Is.True);
        Assert.That(snapshot.Value, Is.EqualTo(3));
    }

    [Test]
    public void Reading_twice_without_a_publish_yields_the_same_value()
    {
        var buffer = new TripleBuffer<Snapshot>(() => new Snapshot());

        buffer.Publish(s => s.Value = 9);

        buffer.TryRead(out var first, out var firstVersion);
        buffer.TryRead(out var second, out var secondVersion);

        Assert.Multiple(() =>
        {
            Assert.That(first.Value, Is.EqualTo(9));
            Assert.That(second.Value, Is.EqualTo(9));
            Assert.That(secondVersion, Is.EqualTo(firstVersion));
        });
    }

    [Test]
    public void The_writer_does_not_block_on_a_reader_that_never_reads()
    {
        var buffer = new TripleBuffer<Snapshot>(() => new Snapshot());

        // A stalled renderer must not be able to hold up the simulation.
        for (var i = 0; i < 10_000; i++)
            buffer.Publish(s => s.Value = i);

        Assert.That(buffer.Version, Is.EqualTo(10_000));

        Assert.That(buffer.TryRead(out var snapshot, out _), Is.True);
        Assert.That(snapshot.Value, Is.EqualTo(9_999));
    }

    [Test]
    public void A_writer_thread_publishing_concurrently_never_produces_a_torn_read()
    {
        var buffer = new TripleBuffer<Snapshot>(() => new Snapshot());
        const int writes = 20_000;

        var writer = new Thread(() =>
        {
            // Every field of the snapshot is written from the same value, so any tearing shows up as an
            // inconsistent pair rather than as a lucky-but-wrong read.
            for (var i = 1; i <= writes; i++)
            {
                var value = i;
                buffer.Publish(s =>
                {
                    s.Value = value;
                    s.Doubled = value * 2;
                });
            }
        });

        writer.Start();

        var reads = 0;
        while (writer.IsAlive && reads < 100_000)
        {
            buffer.TryRead(out var snapshot, out _);
            Assert.That(snapshot.Doubled, Is.EqualTo(snapshot.Value * 2));
            reads++;
        }

        writer.Join();
        Assert.That(buffer.Version, Is.EqualTo(writes));
    }

    private sealed class Snapshot
    {
        public int Value;
        public int Doubled;
    }
}