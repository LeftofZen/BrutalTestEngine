using BrutalTestEngine.Engine;
using BrutalTestEngine.Input;

namespace BrutalTestEngine.Commands;

/// <summary>
/// Where a command came from. Recorded on every command so the engine can apply per-sender policy
/// (authorisation, rate limiting, audit logging) without knowing anything about the transport that
/// carried it.
/// </summary>
public enum CommandSource
{
    /// <summary>An engine-internal caller, for example a startup action or an injected test command.</summary>
    System,

    /// <summary>Keyboard, mouse or focus input captured by GLFW on the main thread.</summary>
    Input,

    /// <summary>A datagram received by the network thread.</summary>
    Network,

    /// <summary>A host command line or a remote console connection.</summary>
    Remote,

    /// <summary>An event delivered by the web listener.</summary>
    Web,

    /// <summary>A row change observed by the database watcher.</summary>
    Database,
}

/// <summary>
/// Which thread is allowed to execute a command.
/// </summary>
/// <remarks>
/// <para>
/// This is the routing half of the command pattern. Input is captured on the main thread, network
/// datagrams arrive on the network thread and database rows change on the IO thread, but none of those
/// threads may mutate simulation state or touch the window. Every command declares who owns its effect
/// and the bus delivers it to exactly that thread.
/// </para>
/// <para>
/// One target per state-owning thread, not one per feature: the split is a statement about whose
/// invariants the command touches, which is what makes the single decision point meaningful.
/// </para>
/// </remarks>
public enum CommandTarget
{
    /// <summary>Applied on the main thread, for example changing the window mode.</summary>
    Engine,

    /// <summary>Applied on the input thread, which folds raw input into the input snapshot.</summary>
    Input,

    /// <summary>Applied inside the fixed-timestep simulation tick.</summary>
    Simulation,
}

/// <summary>
/// The base type for every command that crosses the engine's thread boundaries.
/// </summary>
/// <remarks>
/// <para>
/// All input and logic in the running engine funnels through commands. That gives the engine a single,
/// serialised, observable place where decisions are made: keyboard and mouse events, network
/// datagrams, remote console (rcon) lines, web events and database row changes all become commands and
/// all land on the same concurrent queue. Nothing mutates engine or simulation state directly.
/// </para>
/// <para>
/// That uniformity is what makes the target architecture possible. A seamless world sharded by player
/// and NPC density needs one deterministic stream of intents that can be validated, ordered, recorded
/// and replayed identically on every node, regardless of which transport an intent arrived on.
/// </para>
/// <para>
/// The bus stamps each command with a monotonically increasing <see cref="Sequence"/> and the wall
/// clock time it was accepted, so a recorded command stream is both ordered and replayable.
/// </para>
/// </remarks>
public abstract record Command
{
    /// <summary>Where the command came from.</summary>
    public required CommandSource Source { get; init; }

    /// <summary>Which thread applies the command.</summary>
    public abstract CommandTarget Target { get; }

    /// <summary>The bus sequence number, assigned when the command was accepted.</summary>
    public long Sequence { get; internal set; }

    /// <summary>Wall clock time, in seconds since the bus started, at which the command was accepted.</summary>
    public double TimestampSeconds { get; internal set; }

    /// <summary>A short human-readable description, used by the debug console and the command log.</summary>
    public abstract string Describe();
}

/// <summary>Shuts the engine down. Handled on the main thread, which owns the loop.</summary>
public sealed record QuitCommand : Command
{
    public override CommandTarget Target => CommandTarget.Engine;

    public override string Describe() => "quit";
}

/// <summary>Switches between windowed and borderless fullscreen.</summary>
public sealed record SetWindowModeCommand : Command
{
    public required WindowMode Mode { get; init; }

    public override CommandTarget Target => CommandTarget.Engine;

    public override string Describe() => $"window-mode {Mode}";
}

/// <summary>Toggles between windowed and borderless fullscreen.</summary>
public sealed record ToggleWindowModeCommand : Command
{
    public override CommandTarget Target => CommandTarget.Engine;

    public override string Describe() => "window-mode-toggle";
}

/// <summary>Changes the simulation's fixed tick rate.</summary>
public sealed record SetTickRateCommand : Command
{
    public required double TicksPerSecond { get; init; }

    public override CommandTarget Target => CommandTarget.Engine;

    public override string Describe() => $"tick-rate {TicksPerSecond:0.##}";
}

/// <summary>
/// Asks the engine to run one or more of the caller's own words as if a local user had typed them.
/// </summary>
/// <remarks>
/// A remote console connection cannot know which component will understand a line, so it asks the
/// engine instead of guessing. That is expressed here as an intent from a trusted source rather than
/// as a privileged back door straight into the queues.
/// </remarks>
public sealed record InjectInputCommand : Command
{
    public required string Text { get; init; }

    public override CommandTarget Target => CommandTarget.Engine;

    public override string Describe() => $"inject \"{Text}\"";
}

/// <summary>Asks the engine to describe the commands it accepts.</summary>
public sealed record HelpCommand : Command
{
    public override CommandTarget Target => CommandTarget.Engine;

    public override string Describe() => "help";
}

/// <summary>A key was pressed, released or auto-repeated.</summary>
public sealed record KeyCommand : Command
{
    public required int Key { get; init; }
    public required bool Pressed { get; init; }
    public required bool Repeat { get; init; }

    public override CommandTarget Target => CommandTarget.Input;

    public override string Describe() =>
        $"key {(Pressed ? (Repeat ? "repeat" : "down") : "up")} {KeyNames.Describe(Key)}";
}

/// <summary>A mouse button changed state.</summary>
public sealed record MouseButtonCommand : Command
{
    public required int Button { get; init; }
    public required bool Pressed { get; init; }

    public override CommandTarget Target => CommandTarget.Input;

    public override string Describe() => $"mouse {(Pressed ? "down" : "up")} {Button}";
}

/// <summary>The cursor moved.</summary>
public sealed record CursorMoveCommand : Command
{
    public required double X { get; init; }
    public required double Y { get; init; }

    public override CommandTarget Target => CommandTarget.Input;

    public override string Describe() => $"cursor {X:0.0},{Y:0.0}";
}

/// <summary>The user scrolled.</summary>
public sealed record ScrollCommand : Command
{
    public required double X { get; init; }
    public required double Y { get; init; }

    public override CommandTarget Target => CommandTarget.Input;

    public override string Describe() => $"scroll {X:0.##},{Y:0.##}";
}

/// <summary>A character was typed.</summary>
public sealed record TextInputCommand : Command
{
    public required string Text { get; init; }

    public override CommandTarget Target => CommandTarget.Input;

    public override string Describe() => $"text \"{Text}\"";
}

/// <summary>The window gained or lost focus. Loss releases every held key and button.</summary>
public sealed record FocusCommand : Command
{
    public required bool Focused { get; init; }

    public override CommandTarget Target => CommandTarget.Input;

    public override string Describe() => $"focus {(Focused ? "gained" : "lost")}";
}

/// <summary>
/// Advances the simulation by an exact number of ticks, ignoring wall-clock time.
/// </summary>
/// <remarks>
/// This is a simulation-targeted command rather than an engine one because deterministic replay is the
/// whole point: stepping a fixed number of ticks from a known state has to happen inside the tick loop,
/// in the same place a real tick happens, or it is not reproducible.
/// </remarks>
public sealed record StepSimulationCommand : Command
{
    public required int Ticks { get; init; }

    public override CommandTarget Target => CommandTarget.Simulation;

    public override string Describe() => $"step {Ticks}";
}

/// <summary>Halts or resumes the passage of simulated time.</summary>
public sealed record SetPausedCommand : Command
{
    public required bool Paused { get; init; }

    public override CommandTarget Target => CommandTarget.Simulation;

    public override string Describe() => Paused ? "pause" : "resume";
}

/// <summary>Returns the simulation to its starting state.</summary>
public sealed record ResetSimulationCommand : Command
{
    public override CommandTarget Target => CommandTarget.Simulation;

    public override string Describe() => "reset-simulation";
}

/// <summary>
/// A line of text that a source (console, rcon, network, web) asked the engine to execute, and which
/// no component was able to parse.
/// </summary>
/// <remarks>
/// It stays a first-class command rather than an error because "unknown instruction" is still a
/// decision the authoritative engine has to make: in the sharded design an unrecognised intent from a
/// peer node is diagnosed and logged here rather than dropped at the transport.
/// </remarks>
public sealed record UnhandledCommand : Command
{
    public required string Text { get; init; }
    public string? Reason { get; init; }

    public override CommandTarget Target => CommandTarget.Engine;

    public override string Describe() =>
        Reason is { Length: > 0 } ? $"unhandled \"{Text}\" ({Reason})" : $"unhandled \"{Text}\"";
}
