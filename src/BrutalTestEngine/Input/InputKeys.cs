using BrutalTestEngine.Commands;
using Brutal.GlfwApi;

namespace BrutalTestEngine.Input;

/// <summary>
/// Maps the numeric key codes carried by <see cref="KeyCommand"/> back to readable GLFW names.
/// </summary>
/// <remarks>
/// Commands carry keys as plain integers so that the simulation, the network and the parser can all
/// refer to a key without depending on GLFW, which is a main-thread-only library. Only the debug UI
/// and the command log need to turn one back into a name, and they run on the main thread, so the
/// GLFW dependency is confined here.
/// </remarks>
public static class KeyNames
{
    /// <summary>A readable name for a key code, falling back to the raw number.</summary>
    public static string Describe(int key) =>
        Enum.IsDefined(typeof(GlfwKey), key) ? ((GlfwKey)key).ToString() : $"key{key}";

    /// <summary>
    /// Parses a key name such as <c>Left</c> or <c>f11</c> (case-insensitive) into its key code.
    /// </summary>
    public static bool TryParse(string name, out int key)
    {
        if (Enum.TryParse<GlfwKey>(name, ignoreCase: true, out var parsed))
        {
            key = (int)parsed;
            return true;
        }

        key = 0;
        return false;
    }

    /// <summary>Every key name the parser accepts, for help output.</summary>
    public static string AllNames => string.Join(", ", Enum.GetNames<GlfwKey>());
}

/// <summary>
/// The GLFW key codes the engine's own logic reacts to, as plain integers so the simulation can read
/// them without depending on GLFW.
/// </summary>
/// <remarks>
/// The simulation must not touch GLFW: GLFW is main-thread-only. Exposing the key codes as constants
/// keeps the simulation decoupled while still letting it ask "is this key held".
/// </remarks>
public static class InputKeys
{
    public static readonly int Left = (int)GlfwKey.Left;
    public static readonly int Right = (int)GlfwKey.Right;
    public static readonly int Up = (int)GlfwKey.Up;
    public static readonly int Down = (int)GlfwKey.Down;
    public static readonly int Space = (int)GlfwKey.Space;
    public static readonly int Escape = (int)GlfwKey.Escape;
    public static readonly int F11 = (int)GlfwKey.F11;
}
