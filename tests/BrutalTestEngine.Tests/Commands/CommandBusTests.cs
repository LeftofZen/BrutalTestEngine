using BrutalTestEngine.Commands;
using BrutalTestEngine.Input;

namespace BrutalTestEngine.Tests.Commands;

/// <summary>
/// The bus is the one place every intent in the engine converges on, so its routing and ordering are
/// load-bearing: a command that reaches the wrong thread, or arrives out of order, breaks determinism.
/// </summary>
[TestFixture]
public class CommandBusTests
{
    [Test]
    public void Routes_each_command_to_the_thread_that_owns_its_effect()
    {
        using var bus = new CommandBus();

        bus.Post(new QuitCommand { Source = CommandSource.System });
        bus.Post(
            new KeyCommand
            {
                Source = CommandSource.Input,
                Key = InputKeys.Left,
                Pressed = true,
                Repeat = false,
            }
        );
        bus.Post(new StepSimulationCommand { Source = CommandSource.System, Ticks = 1 });

        // Each queue holds exactly the commands routed to it, and nothing else.
        Assert.That(bus.TryDequeue(CommandTarget.Engine, out var engine), Is.True);
        Assert.That(engine, Is.TypeOf<QuitCommand>());
        Assert.That(bus.TryDequeue(CommandTarget.Engine, out _), Is.False);

        Assert.That(bus.TryDequeue(CommandTarget.Input, out var input), Is.True);
        Assert.That(input, Is.TypeOf<KeyCommand>());
        Assert.That(bus.TryDequeue(CommandTarget.Input, out _), Is.False);

        Assert.That(bus.TryDequeue(CommandTarget.Simulation, out var simulation), Is.True);
        Assert.That(simulation, Is.TypeOf<StepSimulationCommand>());
        Assert.That(bus.TryDequeue(CommandTarget.Simulation, out _), Is.False);
    }

    [Test]
    public void Assigns_strictly_increasing_sequence_numbers()
    {
        using var bus = new CommandBus();

        for (var i = 0; i < 100; i++)
            bus.Post(new QuitCommand { Source = CommandSource.System });

        var previous = 0L;
        while (bus.TryDequeue(CommandTarget.Engine, out var command))
        {
            Assert.That(command.Sequence, Is.EqualTo(previous + 1));
            previous = command.Sequence;
        }

        Assert.That(previous, Is.EqualTo(100));
    }

    [Test]
    public void Counts_accepted_and_applied_separately_per_target()
    {
        using var bus = new CommandBus();

        bus.Post(new QuitCommand { Source = CommandSource.System });
        bus.Post(new QuitCommand { Source = CommandSource.System });
        bus.Post(new StepSimulationCommand { Source = CommandSource.System, Ticks = 1 });

        Assert.That(bus.AcceptedCount, Is.EqualTo(3));
        Assert.That(bus.AppliedCount(CommandTarget.Engine), Is.Zero);

        Assert.That(bus.TryDequeue(CommandTarget.Engine, out _), Is.True);

        // Applying is counted against the target that consumed it, not the bus as a whole, so the
        // debug UI can attribute work to the thread that did it.
        Assert.Multiple(() =>
        {
            Assert.That(bus.AppliedCount(CommandTarget.Engine), Is.EqualTo(1));
            Assert.That(bus.AppliedCount(CommandTarget.Simulation), Is.Zero);
            Assert.That(bus.AcceptedCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void PendingCount_reflects_what_has_not_been_drained()
    {
        using var bus = new CommandBus();

        bus.Post(new QuitCommand { Source = CommandSource.System });
        bus.Post(
            new KeyCommand
            {
                Source = CommandSource.Input,
                Key = InputKeys.Left,
                Pressed = true,
                Repeat = false,
            }
        );

        Assert.That(bus.PendingCount, Is.EqualTo(2));

        bus.TryDequeue(CommandTarget.Engine, out _);
        Assert.That(bus.PendingCount, Is.EqualTo(1));

        bus.TryDequeue(CommandTarget.Input, out _);
        Assert.That(bus.PendingCount, Is.Zero);
    }

    [Test]
    public void Drain_stops_at_the_cap_so_a_flood_cannot_stall_a_tick()
    {
        using var bus = new CommandBus();
        var handler = new RecordingHandler();

        for (var i = 0; i < 50; i++)
            bus.Post(new QuitCommand { Source = CommandSource.System });

        Assert.That(bus.Drain(CommandTarget.Engine, handler, 10), Is.EqualTo(10));

        // The remainder is still there, in order, for the next call.
        Assert.Multiple(() =>
        {
            Assert.That(handler.Quits, Is.EqualTo(10));
            Assert.That(bus.PendingCount, Is.EqualTo(40));
        });

        Assert.That(bus.Drain(CommandTarget.Engine, handler, 10), Is.EqualTo(10));
        Assert.That(handler.Quits, Is.EqualTo(20));
    }

    [Test]
    public void Drain_dispatches_to_the_handler_overload_matching_the_concrete_type()
    {
        using var bus = new CommandBus();
        var handler = new RecordingHandler();

        bus.Post(new QuitCommand { Source = CommandSource.System });
        bus.Post(new SetTickRateCommand { Source = CommandSource.System, TicksPerSecond = 30 });
        bus.Post(new SetPausedCommand { Source = CommandSource.System, Paused = false });
        bus.Post(new UnhandledCommand { Source = CommandSource.System, Text = "x", Reason = "test" });

        bus.Drain(CommandTarget.Engine, handler, 1);
        bus.Drain(CommandTarget.Engine, handler, 1);
        bus.Drain(CommandTarget.Simulation, handler, 1);
        bus.Drain(CommandTarget.Engine, handler, 1);

        Assert.Multiple(() =>
        {
            Assert.That(handler.Quits, Is.EqualTo(1));
            Assert.That(handler.LastTickRate, Is.EqualTo(30));
            Assert.That(handler.LastPaused, Is.False);
            Assert.That(handler.LastUnhandledText, Is.EqualTo("x"));
        });
    }

    [Test]
    public void Every_target_has_an_independent_queue()
    {
        using var bus = new CommandBus();

        // Interleave posts to all three targets; draining one must not disturb the others.
        bus.Post(new StepSimulationCommand { Source = CommandSource.System, Ticks = 1 });
        bus.Post(new QuitCommand { Source = CommandSource.System });
        bus.Post(
            new KeyCommand
            {
                Source = CommandSource.Input,
                Key = InputKeys.Left,
                Pressed = true,
                Repeat = false,
            }
        );
        bus.Post(new StepSimulationCommand { Source = CommandSource.System, Ticks = 2 });

        Assert.That(bus.TryDequeue(CommandTarget.Simulation, out var first), Is.True);
        Assert.That(((StepSimulationCommand)first).Ticks, Is.EqualTo(1));

        // The engine and input commands are still queued and untouched, along with the second
        // simulation command: three of the four posts remain.
        Assert.That(bus.PendingCount, Is.EqualTo(3));

        Assert.Multiple(() =>
        {
            Assert.That(bus.TryDequeue(CommandTarget.Engine, out _), Is.True);
            Assert.That(bus.TryDequeue(CommandTarget.Input, out _), Is.True);
            Assert.That(bus.TryDequeue(CommandTarget.Simulation, out var second), Is.True);
            Assert.That(((StepSimulationCommand)second).Ticks, Is.EqualTo(2));
        });
    }

    [Test]
    public void Timestamps_are_monotonic()
    {
        using var bus = new CommandBus();

        for (var i = 0; i < 50; i++)
            bus.Post(new QuitCommand { Source = CommandSource.System });

        var previous = -1.0;
        while (bus.TryDequeue(CommandTarget.Engine, out var command))
        {
            Assert.That(command.TimestampSeconds, Is.GreaterThanOrEqualTo(previous));
            previous = command.TimestampSeconds;
        }
    }

    [Test]
    public void Concurrent_producers_all_get_distinct_sequences_and_none_are_lost()
    {
        using var bus = new CommandBus();
        const int producers = 8;
        const int perProducer = 500;

        var threads = new Thread[producers];
        for (var p = 0; p < producers; p++)
        {
            threads[p] = new Thread(() =>
            {
                for (var i = 0; i < perProducer; i++)
                    bus.Post(new QuitCommand { Source = CommandSource.Network });
            });
        }

        foreach (var thread in threads)
            thread.Start();

        foreach (var thread in threads)
            thread.Join();

        Assert.That(bus.AcceptedCount, Is.EqualTo(producers * perProducer));

        var sequences = new HashSet<long>();
        var drained = 0;
        while (bus.TryDequeue(CommandTarget.Engine, out var command))
        {
            Assert.That(sequences.Add(command.Sequence), Is.True, $"duplicate sequence {command.Sequence}");
            drained++;
        }

        // Uniqueness alone is not enough: the sequence must also be a dense range with no gaps, or a
        // consumer could not use it to detect a lost command.
        Assert.Multiple(() =>
        {
            Assert.That(drained, Is.EqualTo(producers * perProducer));
            Assert.That(sequences.Max(), Is.EqualTo(producers * perProducer));
        });
    }

    [Test]
    public void Posting_null_is_a_programming_error_rather_than_a_silent_drop()
    {
        using var bus = new CommandBus();

        Assert.That(() => bus.Post(null!), Throws.ArgumentNullException);
    }

    [Test]
    public void The_log_records_accepted_commands_for_diagnostics()
    {
        using var bus = new CommandBus();

        bus.Post(new QuitCommand { Source = CommandSource.System });

        Assert.That(bus.Log.Count, Is.GreaterThanOrEqualTo(1));
    }

    private sealed class RecordingHandler : ICommandHandler
    {
        public int Quits { get; private set; }

        public double LastTickRate { get; private set; } = double.NaN;

        public bool? LastPaused { get; private set; }

        public string? LastUnhandledText { get; private set; }

        public void Handle(QuitCommand command) => Quits++;

        public void Handle(HelpCommand command) { }

        public void Handle(SetWindowModeCommand command) { }

        public void Handle(ToggleWindowModeCommand command) { }

        public void Handle(SetTickRateCommand command) => LastTickRate = command.TicksPerSecond;

        public void Handle(InjectInputCommand command) { }

        public void Handle(KeyCommand command) { }

        public void Handle(MouseButtonCommand command) { }

        public void Handle(CursorMoveCommand command) { }

        public void Handle(ScrollCommand command) { }

        public void Handle(TextInputCommand command) { }

        public void Handle(FocusCommand command) { }

        public void Handle(StepSimulationCommand command) { }

        public void Handle(SetPausedCommand command) => LastPaused = command.Paused;

        public void Handle(ResetSimulationCommand command) { }

        public void Handle(CaptureFrameCommand command) { }

        public void Handle(SetLogLevelCommand command) { }

        public void Handle(AddBodyCommand command) { }

        public void Handle(SetGravityCommand command) { }

        public void Handle(SetVolumeCommand command) { }

        public void Handle(UnhandledCommand command) => LastUnhandledText = command.Text;
    }
}
