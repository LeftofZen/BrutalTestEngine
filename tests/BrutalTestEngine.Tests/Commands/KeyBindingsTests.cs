using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Input;

namespace BrutalTestEngine.Tests.Commands;

/// <summary>
/// Bindings turn a physical key into an intent. They are the reason the engine's own shortcuts go through
/// the bus like any other source rather than mutating the window directly.
/// </summary>
[TestFixture]
public class KeyBindingsTests
{
    private static readonly KeyCommand F11Pressed = new()
    {
        Source = CommandSource.Input,
        Key = InputKeys.F11,
        Pressed = true,
        Repeat = false,
    };

    [Test]
    public void F11_toggles_the_window_mode_by_default()
    {
        var command = KeyBindings.CreateDefault().Resolve(F11Pressed);

        Assert.Multiple(() =>
        {
            Assert.That(command, Is.TypeOf<ToggleWindowModeCommand>());

            // The intent is attributed to input, not to the binding table, so per-source policy still works.
            Assert.That(command!.Source, Is.EqualTo(CommandSource.Input));
        });
    }

    [Test]
    public void An_unbound_key_resolves_to_nothing()
    {
        var command = KeyBindings.CreateDefault().Resolve(
            new KeyCommand
            {
                Source = CommandSource.Input,
                Key = InputKeys.Space,
                Pressed = true,
                Repeat = false,
            }
        );

        Assert.That(command, Is.Null);
    }

    [Test]
    public void Releasing_f11_is_not_bound_by_default()
    {
        // Only the press toggles: binding the release too would toggle twice per keystroke.
        var command = KeyBindings.CreateDefault().Resolve(
            new KeyCommand
            {
                Source = CommandSource.Input,
                Key = InputKeys.F11,
                Pressed = false,
                Repeat = false,
            }
        );

        Assert.That(command, Is.Null);
    }

    [Test]
    public void Press_and_release_bindings_are_independent_tables()
    {
        var bindings = new KeyBindings()
            .OnPress(InputKeys.Space, () => new SetPausedCommand { Source = CommandSource.Input, Paused = true })
            .OnRelease(
                InputKeys.Space,
                () => new SetPausedCommand { Source = CommandSource.Input, Paused = false }
            );

        var pressed = bindings.Resolve(
            new KeyCommand
            {
                Source = CommandSource.Input,
                Key = InputKeys.Space,
                Pressed = true,
                Repeat = false,
            }
        ) as SetPausedCommand;
        var released = bindings.Resolve(
            new KeyCommand
            {
                Source = CommandSource.Input,
                Key = InputKeys.Space,
                Pressed = false,
                Repeat = false,
            }
        ) as SetPausedCommand;

        Assert.Multiple(() =>
        {
            Assert.That(pressed, Is.Not.Null);
            Assert.That(pressed!.Paused, Is.True);
            Assert.That(released, Is.Not.Null);
            Assert.That(released!.Paused, Is.False);
        });
    }

    [Test]
    public void Each_resolve_produces_a_fresh_command_instance()
    {
        var bindings = KeyBindings.CreateDefault();

        // Commands carry per-post state (sequence, timestamp), so a shared instance would be a bug.
        Assert.That(bindings.Resolve(F11Pressed), Is.Not.SameAs(bindings.Resolve(F11Pressed)));
    }
}