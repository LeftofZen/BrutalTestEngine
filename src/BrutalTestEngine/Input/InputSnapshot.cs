using Brutal.Numerics;

namespace BrutalTestEngine.Input;

/// <summary>
/// The input state the simulation reads. The input thread owns the authoritative copy and publishes
/// a snapshot after each batch of events, so the simulation never touches GLFW state and never blocks
/// on the input thread.
/// </summary>
/// <remarks>
/// Instances are reused by the triple buffer that carries them, so the setters are internal: the
/// buffer guarantees the writer and the reader never hold the same instance at the same time, which
/// keeps the public surface read-only without allocating a new object every publish.
/// </remarks>
public sealed class InputSnapshot
{
    /// <summary>Keys currently held down.</summary>
    public IReadOnlySet<int> KeysDown { get; internal set; } = new HashSet<int>();

    /// <summary>Mouse buttons currently held down.</summary>
    public IReadOnlySet<int> ButtonsDown { get; internal set; } = new HashSet<int>();

    /// <summary>Cursor position in window coordinates.</summary>
    public double2 CursorPosition { get; internal set; }

    /// <summary>Accumulated scroll offset since the previous snapshot.</summary>
    public double2 ScrollDelta { get; internal set; }

    /// <summary>Text typed since the previous snapshot.</summary>
    public string TypedText { get; internal set; } = string.Empty;

    /// <summary>Whether the window currently has keyboard focus.</summary>
    public bool Focused { get; internal set; } = true;

    /// <summary>Number of events folded into this snapshot.</summary>
    public long EventCount { get; internal set; }

    public static InputSnapshot Empty { get; } = new();

    /// <summary>True when the given key is held down.</summary>
    public bool IsKeyDown(int key) => KeysDown.Contains(key);

    /// <summary>True when the given mouse button is held down.</summary>
    public bool IsButtonDown(int button) => ButtonsDown.Contains(button);
}
