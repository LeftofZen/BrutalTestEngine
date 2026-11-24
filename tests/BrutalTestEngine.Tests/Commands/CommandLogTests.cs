using BrutalTestEngine.Commands;

namespace BrutalTestEngine.Tests.Commands;

/// <summary>
/// The log is the engine's window into its own command stream. Its bound is the important part: a
/// diagnostic aid that grows without limit becomes a leak.
/// </summary>
[TestFixture]
public class CommandLogTests
{
    /// <summary>A command whose text identifies it, so ordering can be asserted without touching internals.</summary>
    private static TextInputCommand Command(int id) =>
        new() { Source = CommandSource.System, Text = id.ToString() };

    private static int[] Texts(Command[] commands) =>
        [.. commands.Select(c => int.Parse(((TextInputCommand)c).Text))];

    [Test]
    public void Returns_entries_oldest_first()
    {
        var log = new CommandLog(8);

        for (var i = 0; i < 3; i++)
            log.Add(Command(i));

        var destination = new Command[3];

        Assert.That(log.Snapshot(destination), Is.EqualTo(3));
        Assert.That(Texts(destination), Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void Overwrites_the_oldest_entry_once_full()
    {
        var log = new CommandLog(4);

        for (var i = 0; i < 10; i++)
            log.Add(Command(i));

        Assert.Multiple(() =>
        {
            Assert.That(log.Count, Is.EqualTo(4));
            Assert.That(log.OverwrittenCount, Is.EqualTo(6));
        });

        // The four most recent commands are retained, still in order.
        var destination = new Command[4];
        Assert.That(log.Snapshot(destination), Is.EqualTo(4));
        Assert.That(Texts(destination), Is.EqualTo(new[] { 6, 7, 8, 9 }));
    }

    [Test]
    public void Snapshot_returns_the_newest_entries_when_the_destination_is_short()
    {
        var log = new CommandLog(16);

        for (var i = 0; i < 10; i++)
            log.Add(Command(i));

        var destination = new Command[3];

        Assert.That(log.Snapshot(destination), Is.EqualTo(3));

        // A caller showing "the last few commands" wants the newest ones.
        Assert.That(Texts(destination), Is.EqualTo(new[] { 7, 8, 9 }));
    }

    [Test]
    public void Clear_drops_everything()
    {
        var log = new CommandLog(4);
        log.Add(Command(1));

        log.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(log.Count, Is.Zero);
            Assert.That(log.Snapshot(new Command[4]), Is.Zero);
        });
    }

    [Test]
    public void A_capacity_below_one_is_rejected() =>
        Assert.That(() => new CommandLog(0), Throws.InstanceOf<ArgumentOutOfRangeException>());

    [Test]
    public void Concurrent_writers_do_not_corrupt_the_ring()
    {
        const int capacity = 64;
        var log = new CommandLog(capacity);
        const int writers = 8;
        const int perWriter = 500;

        var threads = new Thread[writers];
        for (var w = 0; w < writers; w++)
        {
            threads[w] = new Thread(() =>
            {
                for (var i = 0; i < perWriter; i++)
                    log.Add(Command(i));
            });
        }

        foreach (var thread in threads)
            thread.Start();

        foreach (var thread in threads)
            thread.Join();

        var destination = new Command[capacity];

        Assert.That(log.Count, Is.EqualTo(capacity));

        // Whatever survived must be intact: a torn write would leave a null slot behind.
        Assert.That(log.Snapshot(destination), Is.EqualTo(capacity));
        Assert.That(destination, Is.All.Not.Null);

        // Every command added is accounted for as either retained or overwritten.
        Assert.That(log.Count + log.OverwrittenCount, Is.EqualTo(writers * perWriter));
    }
}
