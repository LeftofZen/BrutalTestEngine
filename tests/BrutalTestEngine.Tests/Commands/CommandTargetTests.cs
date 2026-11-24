using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Input;

namespace BrutalTestEngine.Tests.Commands;

/// <summary>
/// Which thread owns a command's effect is the routing contract of the whole engine. These are the
/// invariants a refactor is most likely to break silently: nothing fails, a command is just applied on
/// the wrong thread, and the resulting race appears somewhere else entirely.
/// </summary>
[TestFixture]
public class CommandTargetTests
{
    private static readonly Command[] AllCommands =
    [
        new QuitCommand { Source = CommandSource.System },
        new HelpCommand { Source = CommandSource.System },
        new SetWindowModeCommand { Source = CommandSource.System, Mode = WindowMode.Windowed },
        new ToggleWindowModeCommand { Source = CommandSource.System },
        new SetTickRateCommand { Source = CommandSource.System, TicksPerSecond = 60 },
        new InjectInputCommand { Source = CommandSource.System, Text = "quit" },
        new UnhandledCommand { Source = CommandSource.System, Text = "?" },
        new KeyCommand
        {
            Source = CommandSource.Input,
            Key = InputKeys.Left,
            Pressed = true,
            Repeat = false,
        },
        new MouseButtonCommand { Source = CommandSource.Input, Button = 0, Pressed = true },
        new CursorMoveCommand { Source = CommandSource.Input, X = 1, Y = 2 },
        new ScrollCommand { Source = CommandSource.Input, X = 0, Y = 1 },
        new TextInputCommand { Source = CommandSource.Input, Text = "a" },
        new FocusCommand { Source = CommandSource.Input, Focused = true },
        new StepSimulationCommand { Source = CommandSource.System, Ticks = 1 },
        new SetPausedCommand { Source = CommandSource.System, Paused = true },
        new ResetSimulationCommand { Source = CommandSource.System },
        new CaptureFrameCommand { Source = CommandSource.System },
        new SetLogLevelCommand { Source = CommandSource.System, Level = "debug" },
        new AddBodyCommand { Source = CommandSource.System },
        new SetGravityCommand { Source = CommandSource.System },
        new SetVolumeCommand { Source = CommandSource.System },
    ];

    /// <summary>
    /// The expected target for each command type. Kept as data so the routing test can be generated
    /// from it rather than hand-written per type.
    /// </summary>
    private static readonly Dictionary<Type, CommandTarget> ExpectedTargets = new()
    {
        [typeof(QuitCommand)] = CommandTarget.Engine,
        [typeof(HelpCommand)] = CommandTarget.Engine,
        [typeof(SetWindowModeCommand)] = CommandTarget.Engine,
        [typeof(ToggleWindowModeCommand)] = CommandTarget.Engine,
        [typeof(SetTickRateCommand)] = CommandTarget.Engine,
        [typeof(InjectInputCommand)] = CommandTarget.Engine,
        [typeof(UnhandledCommand)] = CommandTarget.Engine,
        [typeof(KeyCommand)] = CommandTarget.Input,
        [typeof(MouseButtonCommand)] = CommandTarget.Input,
        [typeof(CursorMoveCommand)] = CommandTarget.Input,
        [typeof(ScrollCommand)] = CommandTarget.Input,
        [typeof(TextInputCommand)] = CommandTarget.Input,
        [typeof(FocusCommand)] = CommandTarget.Input,
        [typeof(StepSimulationCommand)] = CommandTarget.Simulation,
        [typeof(SetPausedCommand)] = CommandTarget.Simulation,
        [typeof(ResetSimulationCommand)] = CommandTarget.Simulation,
        [typeof(CaptureFrameCommand)] = CommandTarget.Engine,
        [typeof(SetLogLevelCommand)] = CommandTarget.Engine,
        [typeof(AddBodyCommand)] = CommandTarget.Physics,
        [typeof(SetGravityCommand)] = CommandTarget.Physics,
        [typeof(SetVolumeCommand)] = CommandTarget.Audio,
    };

    [Test]
    public void Each_command_names_the_thread_that_owns_its_effect()
    {
        Assert.Multiple(() =>
        {
            foreach (var (type, expected) in ExpectedTargets)
            {
                var command = AllCommands.Single(c => c.GetType() == type);
                Assert.That(command.Target, Is.EqualTo(expected), $"{type.Name} routes to the wrong thread");
            }
        });
    }

    [Test]
    public void Every_command_type_is_covered_by_the_routing_table()
    {
        // A new command type that nobody decided a target for would otherwise slip through: the test
        // above only checks the types it lists.
        var declared = typeof(Command)
            .Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(Command)) && !t.IsAbstract && !t.IsGenericTypeDefinition)
            .ToHashSet();

        Assert.That(declared, Is.EquivalentTo(ExpectedTargets.Keys));
    }

    [Test]
    public void Every_command_type_can_be_described()
    {
        // Describe() feeds the command log and the debug console, so an empty or missing description
        // makes an otherwise observable system opaque.
        Assert.Multiple(() =>
        {
            foreach (var command in AllCommands)
                Assert.That(command.Describe(), Is.Not.Null.And.Not.WhiteSpace, $"{command.GetType().Name}");
        });
    }

    [Test]
    public void Every_command_type_has_a_handler_overload()
    {
        // The dispatcher throws on a command it does not know, which would only surface when that
        // command is first posted at runtime.
        var handler = new AcceptingHandler();

        Assert.Multiple(() =>
        {
            foreach (var command in AllCommands)
                Assert.That(() => CommandDispatcher.Dispatch(handler, command), Throws.Nothing);
        });
    }

    /// <summary>Accepts every command type without asserting anything about it.</summary>
    private sealed class AcceptingHandler : ICommandHandler
    {
        public void Handle(QuitCommand command) { }

        public void Handle(HelpCommand command) { }

        public void Handle(SetWindowModeCommand command) { }

        public void Handle(ToggleWindowModeCommand command) { }

        public void Handle(SetTickRateCommand command) { }

        public void Handle(InjectInputCommand command) { }

        public void Handle(KeyCommand command) { }

        public void Handle(MouseButtonCommand command) { }

        public void Handle(CursorMoveCommand command) { }

        public void Handle(ScrollCommand command) { }

        public void Handle(TextInputCommand command) { }

        public void Handle(FocusCommand command) { }

        public void Handle(StepSimulationCommand command) { }

        public void Handle(SetPausedCommand command) { }

        public void Handle(ResetSimulationCommand command) { }

        public void Handle(CaptureFrameCommand command) { }

        public void Handle(SetLogLevelCommand command) { }

        public void Handle(AddBodyCommand command) { }

        public void Handle(SetGravityCommand command) { }

        public void Handle(SetVolumeCommand command) { }

        public void Handle(UnhandledCommand command) { }
    }
}
