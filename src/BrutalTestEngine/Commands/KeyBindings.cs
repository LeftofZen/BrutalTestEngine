using BrutalTestEngine.Engine;
using BrutalTestEngine.Input;

namespace BrutalTestEngine.Commands;

/// <summary>
/// Maps a physical key to the intent it stands for.
/// </summary>
/// <remarks>
/// <para>
/// A key press is two things at once: a change to the held-key state the simulation reads, and possibly a
/// request for an action. The command pattern keeps those separate. The raw
/// <see cref="KeyCommand"/> always goes on the bus, because the simulation needs the held-key state; a binding
/// additionally contributes an intent, and that intent is a command like any other.
/// </para>
/// <para>
/// Keeping the table here rather than inside the GLFW callback matters because it is the same table a remote
/// source will use. Bindings are data, so a later remapping feature or an rcon <c>bind</c> verb changes the
/// table instead of the input path, and a bound action cannot reach engine state except by posting a command
/// the engine chose to handle.
/// </para>
/// </remarks>
public sealed class KeyBindings
{
    private readonly Dictionary<int, Func<Command>> _press = [];
    private readonly Dictionary<int, Func<Command>> _release = [];

    /// <summary>Binds a key press to an intent.</summary>
    public KeyBindings OnPress(int key, Func<Command> command)
    {
        _press[key] = command;
        return this;
    }

    /// <summary>Binds a key release to an intent.</summary>
    public KeyBindings OnRelease(int key, Func<Command> command)
    {
        _release[key] = command;
        return this;
    }

    /// <summary>
    /// The intent a key press or release stands for, or <c>null</c> when it is unbound.
    /// </summary>
    public Command? Resolve(KeyCommand key)
    {
        var table = key.Pressed ? _press : _release;
        return table.TryGetValue(key.Key, out var factory) ? factory() : null;
    }

    /// <summary>
    /// The bindings the engine ships with. F11 toggles fullscreen, which is the one action the engine itself
    /// owns; how the window is presented is an engine concern, not a simulation one.
    /// </summary>
    public static KeyBindings CreateDefault() =>
        new KeyBindings()
            .OnPress(
                InputKeys.F11,
                () => new ToggleWindowModeCommand { Source = CommandSource.Input }
            );
}
