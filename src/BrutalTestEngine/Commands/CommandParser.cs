using System.Globalization;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Input;

namespace BrutalTestEngine.Commands;

/// <summary>
/// Turns a single line of text into a <see cref="Command"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the shared wire format for every text transport. A remote console line, a datagram, a web
/// request body and a database row all reduce to the same thing — a verb followed by arguments — so they
/// are parsed in exactly one place. Adding a transport therefore never adds a second, subtly different
/// grammar.
/// </para>
/// <para>
/// The verbs are deliberately transport-agnostic. Only the <see cref="CommandSource"/> distinguishes a
/// line typed at the console from the same line arriving over the network, which is what lets the engine
/// apply per-source policy later without touching the grammar.
/// </para>
/// </remarks>
public static class CommandParser
{
    private const int MaxTextLength = 256;

    /// <summary>A readable usage summary, shown by the <c>help</c> verb.</summary>
    public static string HelpText =>
        "verbs: help | quit | window-mode [windowed|fullscreen|toggle] | tick-rate <hz> | "
        + "key [down|up|repeat] <key> | mouse <down|up> <button> | cursor <x> <y> | "
        + "scroll <x> <y> | text <string> | focus <gained|lost> | inject <command...> | "
        + "step <ticks> | pause | resume | reset-simulation | capture | log-level <level> | "
        + "add-body [static] [r<radius>] [<x> <y> <z>] | gravity <x> <y> <z> | volume <0..1>";

    /// <summary>
    /// Parses one line. Anything not understood comes back as an <see cref="UnhandledCommand"/> so the
    /// engine still sees and can log the intent rather than the transport silently discarding it.
    /// </summary>
    public static Command Parse(string line, CommandSource source)
    {
        var trimmed = line.Trim();

        // Blank lines and comments are not instructions at all, so nothing is reported for them.
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return new UnhandledCommand { Source = source, Text = trimmed, Reason = "empty" };

        var space = trimmed.IndexOf(' ');
        var verb = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        var rest = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

        return verb switch
        {
            "help" => new HelpCommand { Source = source },
            "quit" => new QuitCommand { Source = source },
            "window-mode" => ParseWindowMode(rest, source, trimmed),
            "tick-rate" => ParseTickRate(rest, source, trimmed),
            "key" => ParseKey(rest, source, trimmed),
            "mouse" => ParseMouse(rest, source, trimmed),
            "cursor" => ParseCursor(rest, source, trimmed),
            "scroll" => ParseScroll(rest, source, trimmed),
            "text" => new TextInputCommand { Source = source, Text = Clamp(rest) },
            "focus" => ParseFocus(rest, source, trimmed),
            "inject" => new InjectInputCommand { Source = source, Text = Clamp(rest) },
            "step" => ParseStep(rest, source, trimmed),
            "pause" => new SetPausedCommand { Source = source, Paused = true },
            "resume" => new SetPausedCommand { Source = source, Paused = false },
            "reset-simulation" => new ResetSimulationCommand { Source = source },
            "capture" => new CaptureFrameCommand { Source = source },
            "log-level" => ParseLogLevel(rest, source, trimmed),
            "add-body" => ParseAddBody(rest, source, trimmed),
            "gravity" => ParseGravity(rest, source, trimmed),
            "volume" => ParseVolume(rest, source, trimmed),
            _ => new UnhandledCommand
            {
                Source = source,
                Text = Clamp(trimmed),
                Reason = $"unknown verb '{verb}'",
            },
        };
    }

    /// <summary>
    /// Parses a whole payload into commands. Every line is parsed separately, so one datagram or request
    /// can carry a batch of intents.
    /// </summary>
    public static List<Command> ParseMany(ReadOnlySpan<char> text, CommandSource source)
    {
        var commands = new List<Command>();
        var start = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n')
                continue;

            var line = text[start..i].TrimEnd('\r').ToString();
            start = i + 1;

            // A trailing newline must not produce a spurious "empty" report.
            if (line.Trim().Length == 0)
                continue;

            commands.Add(Parse(line, source));
        }

        return commands;
    }

    /// <summary>
    /// Encodes a command back into the line format, so it can be sent to a remote source.
    /// </summary>
    /// <remarks>
    /// This is what makes the transports testable end to end: a command can be produced locally, encoded,
    /// pushed through a real socket and parsed back, which exercises the same round trip a remote client
    /// performs.
    /// </remarks>
    public static bool TryFormat(Command command, out string line)
    {
        switch (command)
        {
            case HelpCommand:
                line = "help";
                return true;
            case QuitCommand:
                line = "quit";
                return true;
            case SetWindowModeCommand setMode:
                line =
                    $"window-mode {(setMode.Mode == WindowMode.BorderlessFullscreen ? "fullscreen" : "windowed")}";
                return true;
            case ToggleWindowModeCommand:
                line = "window-mode toggle";
                return true;
            case SetTickRateCommand tickRate:
                line = $"tick-rate {tickRate.TicksPerSecond.ToString(CultureInfo.InvariantCulture)}";
                return true;
            case KeyCommand key:
                line =
                    $"key {(key.Pressed ? (key.Repeat ? "repeat" : "down") : "up")} {KeyNames.Describe(key.Key)}";
                return true;
            case MouseButtonCommand button:
                line = $"mouse {(button.Pressed ? "down" : "up")} {button.Button}";
                return true;
            case CursorMoveCommand cursor:
                line =
                    $"cursor {cursor.X.ToString(CultureInfo.InvariantCulture)} {cursor.Y.ToString(CultureInfo.InvariantCulture)}";
                return true;
            case ScrollCommand scroll:
                line =
                    $"scroll {scroll.X.ToString(CultureInfo.InvariantCulture)} {scroll.Y.ToString(CultureInfo.InvariantCulture)}";
                return true;
            case TextInputCommand text:
                line = $"text {text.Text}";
                return true;
            case FocusCommand focus:
                line = $"focus {(focus.Focused ? "gained" : "lost")}";
                return true;
            case InjectInputCommand inject:
                line = $"inject {inject.Text}";
                return true;
            case StepSimulationCommand step:
                line = $"step {step.Ticks}";
                return true;
            case SetPausedCommand paused:
                line = paused.Paused ? "pause" : "resume";
                return true;
            case ResetSimulationCommand:
                line = "reset-simulation";
                return true;
            case CaptureFrameCommand:
                line = "capture";
                return true;
            case SetLogLevelCommand logLevel:
                line = $"log-level {logLevel.Level}";
                return true;
            case AddBodyCommand addBody:
                line =
                    $"add-body{(addBody.IsStatic ? " static" : string.Empty)} "
                    + $"r{Number(addBody.Radius)} {Number(addBody.X)} {Number(addBody.Y)} {Number(addBody.Z)}";
                return true;
            case SetGravityCommand gravity:
                line = $"gravity {Number(gravity.X)} {Number(gravity.Y)} {Number(gravity.Z)}";
                return true;
            case SetVolumeCommand volume:
                line = $"volume {Number(volume.Volume)}";
                return true;
            default:
                line = string.Empty;
                return false;
        }
    }

    private static Command ParseWindowMode(string rest, CommandSource source, string original) =>
        rest.ToLowerInvariant() switch
        {
            "windowed" => new SetWindowModeCommand { Source = source, Mode = WindowMode.Windowed },
            "fullscreen" or "borderless" => new SetWindowModeCommand
            {
                Source = source,
                Mode = WindowMode.BorderlessFullscreen,
            },
            "toggle" or "" => new ToggleWindowModeCommand { Source = source },
            _ => Malformed(source, original, $"unknown window mode '{rest}'"),
        };

    private static Command ParseTickRate(string rest, CommandSource source, string original) =>
        TryParseDouble(rest, out var rate) && rate > 0
            ? new SetTickRateCommand { Source = source, TicksPerSecond = rate }
            : Malformed(source, original, $"'{rest}' is not a positive tick rate");

    private static Command ParseStep(string rest, CommandSource source, string original) =>
        int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) && ticks > 0
            ? new StepSimulationCommand { Source = source, Ticks = ticks }
            : Malformed(source, original, $"'{rest}' is not a positive tick count");

    /// <summary>
    /// Parses a log level name. Accepts the Serilog/`Microsoft.Extensions.Logging` names, so the value a
    /// developer types matches what they read in the configuration.
    /// </summary>
    private static Command ParseLogLevel(string rest, CommandSource source, string original)
    {
        var level = rest.ToLowerInvariant();

        return level switch
        {
            "verbose" or "trace" => new SetLogLevelCommand { Source = source, Level = "verbose" },
            "debug" => new SetLogLevelCommand { Source = source, Level = "debug" },
            "info" or "information" => new SetLogLevelCommand { Source = source, Level = "information" },
            "warn" or "warning" => new SetLogLevelCommand { Source = source, Level = "warning" },
            "error" => new SetLogLevelCommand { Source = source, Level = "error" },
            "fatal" or "critical" => new SetLogLevelCommand { Source = source, Level = "fatal" },
            _ => Malformed(
                source,
                original,
                $"'{rest}' is not a log level (verbose|debug|information|warning|error|fatal)"
            ),
        };
    }

    private static Command ParseKey(string rest, CommandSource source, string original)
    {
        // "key <name>" means press; "key <down|up|repeat> <name>" is the explicit form.
        var parts = Split(rest);

        string action;
        string name;
        switch (parts.Length)
        {
            case 1:
                action = "down";
                name = parts[0];
                break;
            case 2:
                action = parts[0].ToLowerInvariant();
                name = parts[1];
                break;
            default:
                return Malformed(source, original, "expected 'key <down|up|repeat> <key>'");
        }

        var pressed = action switch
        {
            "down" or "press" => true,
            "up" or "release" => false,
            "repeat" => true,
            _ => (bool?)null,
        };

        if (pressed is null)
            return Malformed(source, original, $"unknown key action '{action}'");

        if (!KeyNames.TryParse(name, out var key))
            return Malformed(source, original, $"unknown key '{name}'");

        return new KeyCommand
        {
            Source = source,
            Key = key,
            Pressed = pressed.Value,
            Repeat = action == "repeat",
        };
    }

    private static Command ParseMouse(string rest, CommandSource source, string original)
    {
        var parts = Split(rest);
        if (parts.Length != 2)
            return Malformed(source, original, "expected 'mouse <down|up> <button>'");

        var pressed = parts[0].ToLowerInvariant() switch
        {
            "down" or "press" => true,
            "up" or "release" => false,
            _ => (bool?)null,
        };

        if (pressed is null)
            return Malformed(source, original, $"unknown mouse action '{parts[0]}'");

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var button))
            return Malformed(source, original, $"'{parts[1]}' is not a button number");

        return new MouseButtonCommand { Source = source, Button = button, Pressed = pressed.Value };
    }

    private static Command ParseCursor(string rest, CommandSource source, string original)
    {
        var parts = Split(rest);
        if (parts.Length != 2)
            return Malformed(source, original, "expected 'cursor <x> <y>'");

        if (!TryParseDouble(parts[0], out var x) || !TryParseDouble(parts[1], out var y))
            return Malformed(source, original, "cursor coordinates must be finite numbers");

        return new CursorMoveCommand { Source = source, X = x, Y = y };
    }

    private static Command ParseScroll(string rest, CommandSource source, string original)
    {
        var parts = Split(rest);
        if (parts.Length != 2)
            return Malformed(source, original, "expected 'scroll <x> <y>'");

        if (!TryParseDouble(parts[0], out var x) || !TryParseDouble(parts[1], out var y))
            return Malformed(source, original, "scroll offsets must be finite numbers");

        return new ScrollCommand { Source = source, X = x, Y = y };
    }

    private static Command ParseFocus(string rest, CommandSource source, string original) =>
        rest.ToLowerInvariant() switch
        {
            "gained" or "true" or "1" => new FocusCommand { Source = source, Focused = true },
            "lost" or "false" or "0" => new FocusCommand { Source = source, Focused = false },
            _ => Malformed(source, original, $"unknown focus state '{rest}'"),
        };

    private static string[] Split(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>
    /// Parses <c>add-body</c>, whose arguments are all optional so a bare verb still does something useful.
    /// </summary>
    /// <remarks>
    /// Positional rather than named: <c>add-body static r2 0 10 0</c> reads the way it is typed, and the two
    /// optional keywords are recognised by shape rather than by order, so leaving the radius out does not turn
    /// the first coordinate into one.
    /// </remarks>
    private static Command ParseAddBody(string rest, CommandSource source, string original)
    {
        var parts = Split(rest);
        var isStatic = false;
        var radius = 0.5f;
        var coordinates = new List<double>(3);

        foreach (var part in parts)
        {
            if (part.Equals("static", StringComparison.OrdinalIgnoreCase))
            {
                isStatic = true;
                continue;
            }

            if (part.StartsWith('r') && part.Length > 1 && TryParseDouble(part[1..], out var parsedRadius))
            {
                radius = (float)parsedRadius;
                continue;
            }

            if (!TryParseDouble(part, out var coordinate))
                return Malformed(source, original, $"'{part}' is not a coordinate");

            coordinates.Add(coordinate);
        }

        if (radius <= 0.0f)
            return Malformed(source, original, "radius must be greater than zero");

        if (coordinates.Count is not (0 or 3))
            return Malformed(source, original, "expected either no position or 'x y z'");

        return new AddBodyCommand
        {
            Source = source,
            IsStatic = isStatic,
            Radius = radius,
            X = coordinates.Count == 3 ? (float)coordinates[0] : 0.0f,
            Y = coordinates.Count == 3 ? (float)coordinates[1] : 5.0f,
            Z = coordinates.Count == 3 ? (float)coordinates[2] : 0.0f,
        };
    }

    private static Command ParseGravity(string rest, CommandSource source, string original)
    {
        var parts = Split(rest);
        if (parts.Length != 3)
            return Malformed(source, original, "expected 'gravity <x> <y> <z>'");

        if (
            !TryParseDouble(parts[0], out var x)
            || !TryParseDouble(parts[1], out var y)
            || !TryParseDouble(parts[2], out var z)
        )
        {
            return Malformed(source, original, "gravity components must be finite numbers");
        }

        return new SetGravityCommand
        {
            Source = source,
            X = (float)x,
            Y = (float)y,
            Z = (float)z,
        };
    }

    private static Command ParseVolume(string rest, CommandSource source, string original) =>
        TryParseDouble(rest, out var volume) && volume is >= 0.0 and <= 1.0
            ? new SetVolumeCommand { Source = source, Volume = (float)volume }
            : Malformed(source, original, "expected 'volume <0..1>'");

    /// <summary>
    /// Formats a float without trailing zeros, so a round trip produces the same text a developer would type.
    /// </summary>
    private static string Number(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static UnhandledCommand Malformed(CommandSource source, string text, string reason) =>
        new()
        {
            Source = source,
            Text = Clamp(text),
            Reason = reason,
        };

    private static string Clamp(string text) => text.Length <= MaxTextLength ? text : text[..MaxTextLength];
}
