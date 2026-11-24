using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Input;

namespace BrutalTestEngine.Tests.Commands;

/// <summary>
/// The parser is the single grammar every text transport shares, so its behaviour is worth pinning:
/// if it drifts, the console, rcon, network, web and database sources all drift with it.
/// </summary>
[TestFixture]
public class CommandParserTests
{
    [Test]
    public void Parses_each_verb_into_its_command_type()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandParser.Parse("help", CommandSource.Remote), Is.TypeOf<HelpCommand>());
            Assert.That(CommandParser.Parse("quit", CommandSource.Remote), Is.TypeOf<QuitCommand>());
            Assert.That(
                CommandParser.Parse("window-mode toggle", CommandSource.Remote),
                Is.TypeOf<ToggleWindowModeCommand>()
            );
            Assert.That(CommandParser.Parse("tick-rate 120", CommandSource.Remote), Is.TypeOf<SetTickRateCommand>());
            Assert.That(CommandParser.Parse("key down Left", CommandSource.Remote), Is.TypeOf<KeyCommand>());
            Assert.That(CommandParser.Parse("mouse up 0", CommandSource.Remote), Is.TypeOf<MouseButtonCommand>());
            Assert.That(CommandParser.Parse("cursor 1.5 2.5", CommandSource.Remote), Is.TypeOf<CursorMoveCommand>());
            Assert.That(CommandParser.Parse("scroll 0 -1", CommandSource.Remote), Is.TypeOf<ScrollCommand>());
            Assert.That(CommandParser.Parse("text hello", CommandSource.Remote), Is.TypeOf<TextInputCommand>());
            Assert.That(CommandParser.Parse("focus lost", CommandSource.Remote), Is.TypeOf<FocusCommand>());
            Assert.That(CommandParser.Parse("inject quit", CommandSource.Remote), Is.TypeOf<InjectInputCommand>());
            Assert.That(CommandParser.Parse("step 7", CommandSource.Remote), Is.TypeOf<StepSimulationCommand>());
            Assert.That(CommandParser.Parse("pause", CommandSource.Remote), Is.TypeOf<SetPausedCommand>());
            Assert.That(CommandParser.Parse("resume", CommandSource.Remote), Is.TypeOf<SetPausedCommand>());
            Assert.That(
                CommandParser.Parse("reset-simulation", CommandSource.Remote),
                Is.TypeOf<ResetSimulationCommand>()
            );
            Assert.That(CommandParser.Parse("capture", CommandSource.Remote), Is.TypeOf<CaptureFrameCommand>());
            Assert.That(
                CommandParser.Parse("log-level debug", CommandSource.Remote),
                Is.TypeOf<SetLogLevelCommand>()
            );
        });
    }

    [Test]
    public void Is_case_and_whitespace_insensitive_for_the_verb()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CommandParser.Parse("  QUIT  ", CommandSource.Remote), Is.TypeOf<QuitCommand>());
            Assert.That(CommandParser.Parse("\twindOW-mode", CommandSource.Remote), Is.TypeOf<ToggleWindowModeCommand>());
        });
    }

    [Test]
    public void A_bare_key_means_press()
    {
        var command = CommandParser.Parse("key Left", CommandSource.Remote) as KeyCommand;

        Assert.That(command, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(command!.Pressed, Is.True);
            Assert.That(command.Repeat, Is.False);
        });
    }

    [Test]
    public void Repeat_is_a_press_that_is_flagged_as_an_auto_repeat()
    {
        var command = CommandParser.Parse("key repeat Left", CommandSource.Remote) as KeyCommand;

        Assert.That(command, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(command!.Pressed, Is.True);
            Assert.That(command.Repeat, Is.True);
        });
    }

    [Test]
    public void A_bare_window_mode_is_a_toggle() =>
        Assert.That(CommandParser.Parse("window-mode", CommandSource.Remote), Is.TypeOf<ToggleWindowModeCommand>());

    [TestCase("window-mode fullscreen", WindowMode.BorderlessFullscreen)]
    [TestCase("window-mode borderless", WindowMode.BorderlessFullscreen)]
    [TestCase("window-mode windowed", WindowMode.Windowed)]
    public void Parses_the_window_modes_that_exist(string line, WindowMode expected)
    {
        var command = CommandParser.Parse(line, CommandSource.Remote) as SetWindowModeCommand;

        Assert.That(command, Is.Not.Null);
        Assert.That(command!.Mode, Is.EqualTo(expected));
    }

    [Test]
    public void The_source_is_recorded_verbatim()
    {
        foreach (var source in Enum.GetValues<CommandSource>())
            Assert.That(CommandParser.Parse("quit", source).Source, Is.EqualTo(source));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("# a comment")]
    [TestCase("tick-rate 0")]
    [TestCase("tick-rate -5")]
    [TestCase("tick-rate fast")]
    [TestCase("tick-rate NaN")]
    [TestCase("step 0")]
    [TestCase("step many")]
    [TestCase("key sideways Left")]
    [TestCase("key down NotAKey")]
    [TestCase("key down extra words")]
    [TestCase("mouse down")]
    [TestCase("mouse sideways 0")]
    [TestCase("mouse down zero")]
    [TestCase("cursor 1")]
    [TestCase("cursor a b")]
    [TestCase("scroll 1")]
    [TestCase("focus maybe")]
    [TestCase("window-mode hologram")]
    [TestCase("nonsense verb")]
    public void Anything_unusable_becomes_an_unhandled_command_with_a_reason(string line)
    {
        var command = CommandParser.Parse(line, CommandSource.Remote) as UnhandledCommand;

        Assert.That(command, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(command!.Reason, Is.Not.Null.And.Not.WhiteSpace);
            Assert.That(command.Source, Is.EqualTo(CommandSource.Remote));
        });
    }

    [Test]
    public void ParseMany_gives_one_command_per_non_blank_line()
    {
        var commands = CommandParser.ParseMany("quit\n\n  \ntick-rate 90\r\nstep 3\n", CommandSource.Network);

        Assert.That(commands, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(commands[0], Is.TypeOf<QuitCommand>());
            Assert.That(commands[1], Is.TypeOf<SetTickRateCommand>());
            Assert.That(commands[2], Is.TypeOf<StepSimulationCommand>());
            Assert.That(commands.Select(c => c.Source), Is.All.EqualTo(CommandSource.Network));
        });
    }

    [Test]
    public void Parsed_values_keep_their_precision()
    {
        var tickRate = CommandParser.Parse("tick-rate 144.5", CommandSource.Remote) as SetTickRateCommand;
        var cursor = CommandParser.Parse("cursor -12.25 0.5", CommandSource.Remote) as CursorMoveCommand;
        var text = CommandParser.Parse("text hello world", CommandSource.Remote) as TextInputCommand;

        Assert.Multiple(() =>
        {
            Assert.That(tickRate, Is.Not.Null);
            Assert.That(tickRate!.TicksPerSecond, Is.EqualTo(144.5));
            Assert.That(cursor, Is.Not.Null);
            Assert.That(cursor!.X, Is.EqualTo(-12.25));
            Assert.That(cursor.Y, Is.EqualTo(0.5));
            Assert.That(text, Is.Not.Null);
            Assert.That(text!.Text, Is.EqualTo("hello world"));
        });
    }

    [Test]
    public void Formatting_then_parsing_reproduces_the_same_command()
    {
        // This round trip is how a command reaches a remote client, so it has to be lossless for every
        // command the engine can emit.
        Command[] commands =
        [
            new HelpCommand { Source = CommandSource.Remote },
            new QuitCommand { Source = CommandSource.Remote },
            new SetWindowModeCommand { Source = CommandSource.Remote, Mode = WindowMode.Windowed },
            new SetWindowModeCommand { Source = CommandSource.Remote, Mode = WindowMode.BorderlessFullscreen },
            new ToggleWindowModeCommand { Source = CommandSource.Remote },
            new SetTickRateCommand { Source = CommandSource.Remote, TicksPerSecond = 144.0 },
            new KeyCommand
            {
                Source = CommandSource.Remote,
                Key = InputKeys.Left,
                Pressed = true,
                Repeat = false,
            },
            new KeyCommand
            {
                Source = CommandSource.Remote,
                Key = InputKeys.Escape,
                Pressed = false,
                Repeat = false,
            },
            new MouseButtonCommand { Source = CommandSource.Remote, Button = 2, Pressed = true },
            new CursorMoveCommand { Source = CommandSource.Remote, X = -3.5, Y = 42.25 },
            new ScrollCommand { Source = CommandSource.Remote, X = 0.0, Y = -2.0 },
            new TextInputCommand { Source = CommandSource.Remote, Text = "hello" },
            new FocusCommand { Source = CommandSource.Remote, Focused = false },
            new InjectInputCommand { Source = CommandSource.Remote, Text = "quit" },
            new StepSimulationCommand { Source = CommandSource.Remote, Ticks = 9 },
            new SetPausedCommand { Source = CommandSource.Remote, Paused = true },
            new SetPausedCommand { Source = CommandSource.Remote, Paused = false },
            new ResetSimulationCommand { Source = CommandSource.Remote },
        ];

        Assert.Multiple(() =>
        {
            foreach (var original in commands)
            {
                Assert.That(
                    CommandParser.TryFormat(original, out var line),
                    Is.True,
                    $"{original.Describe()} did not format"
                );

                // Records compare by value, so this is a full structural equality check, including the
                // strongly typed members the line had to encode and decode.
                Assert.That(CommandParser.Parse(line, CommandSource.Remote), Is.EqualTo(original));
            }
        });
    }

    [Test]
    public void An_unhandled_command_cannot_be_formatted()
    {
        var formatted = CommandParser.TryFormat(
            new UnhandledCommand { Source = CommandSource.Remote, Text = "x" },
            out var line
        );

        Assert.Multiple(() =>
        {
            Assert.That(formatted, Is.False);
            Assert.That(line, Is.Empty);
        });
    }

    [Test]
    public void HelpText_lists_every_verb_the_parser_accepts()
    {
        // A verb the parser accepts but help omits is a verb nobody can discover.
        string[] verbs =
        [
            "help",
            "quit",
            "window-mode",
            "tick-rate",
            "key",
            "mouse",
            "cursor",
            "scroll",
            "text",
            "focus",
            "inject",
            "step",
            "pause",
            "resume",
            "reset-simulation",
        ];

        Assert.Multiple(() =>
        {
            foreach (var verb in verbs)
                Assert.That(CommandParser.HelpText, Does.Contain(verb), $"help output is missing '{verb}'");
        });
    }

    [Test]
    public void Every_documented_log_level_spelling_parses()
    {
        // A level that is silently rejected looks identical to a level that changed and had no
        // effect, so every spelling a developer might type has to reach the same command.
        Assert.Multiple(() =>
        {
            foreach (var spelling in new[] { "verbose", "trace", "debug", "info", "information", "warn", "warning", "error", "fatal", "critical" })
            {
                Assert.That(
                    CommandParser.Parse($"log-level {spelling}", CommandSource.Remote),
                    Is.TypeOf<SetLogLevelCommand>(),
                    $"'{spelling}' should parse"
                );
            }

            Assert.That(
                CommandParser.Parse("log-level nonsense", CommandSource.Remote),
                Is.TypeOf<UnhandledCommand>()
            );
        });
    }

    [Test]
    public void The_new_verbs_round_trip_through_the_wire_format()
    {
        // A command that can be sent must be encodable, or a remote source could issue something
        // it could never have expressed.
        Assert.Multiple(() =>
        {
            Assert.That(
                CommandParser.TryFormat(
                    new CaptureFrameCommand { Source = CommandSource.Remote },
                    out var capture
                ),
                Is.True
            );
            Assert.That(capture, Is.EqualTo("capture"));
            Assert.That(CommandParser.Parse(capture, CommandSource.Remote), Is.TypeOf<CaptureFrameCommand>());

            Assert.That(
                CommandParser.TryFormat(
                    new SetLogLevelCommand { Source = CommandSource.Remote, Level = "debug" },
                    out var logLevel
                ),
                Is.True
            );
            Assert.That(logLevel, Is.EqualTo("log-level debug"));
            Assert.That(CommandParser.Parse(logLevel, CommandSource.Remote), Is.TypeOf<SetLogLevelCommand>());
        });
    }
}
