using Brutal.GlfwApi;
using Brutal.Numerics;
using BrutalTestEngine.Commands;

namespace BrutalTestEngine.Input;

/// <summary>
/// Translates GLFW callbacks into commands for the input thread.
/// </summary>
/// <remarks>
/// <para>
/// GLFW callbacks run on the main thread and must return immediately, so they cannot do anything except
/// describe what happened. That description is a command: turning a callback into a
/// <see cref="KeyCommand"/> and posting it to the bus is the entire job, and it is what keeps input on
/// the same footing as every other source.
/// </para>
/// <para>
/// The translation lives here rather than inside the callbacks so that the mapping from GLFW's vocabulary
/// to the engine's is in one place, and so the callbacks stay one-liners.
/// </para>
/// </remarks>
public static class InputCommands
{
    public static KeyCommand Key(GlfwKey key, GlfwKeyAction action) =>
        new()
        {
            Source = CommandSource.Input,
            Key = (int)key,
            Pressed = action != GlfwKeyAction.Release,
            Repeat = action == GlfwKeyAction.Repeat,
        };

    public static MouseButtonCommand MouseButton(GlfwMouseButton button, GlfwButtonAction action) =>
        new()
        {
            Source = CommandSource.Input,
            Button = (int)button,
            Pressed = action == GlfwButtonAction.Press,
        };

    public static CursorMoveCommand CursorMove(double2 position) =>
        new()
        {
            Source = CommandSource.Input,
            X = position.X,
            Y = position.Y,
        };

    public static ScrollCommand Scroll(double2 offset) =>
        new()
        {
            Source = CommandSource.Input,
            X = offset.X,
            Y = offset.Y,
        };

    public static TextInputCommand Text(uint codePoint) =>
        new()
        {
            Source = CommandSource.Input,
            Text = char.ConvertFromUtf32((int)codePoint),
        };

    public static FocusCommand Focus(bool focused) =>
        new()
        {
            Source = CommandSource.Input,
            Focused = focused,
        };

    public static FocusCommand Focus(GlfwFocusEvent focusEvent) =>
        Focus(focusEvent == GlfwFocusEvent.GainFocus);
}
