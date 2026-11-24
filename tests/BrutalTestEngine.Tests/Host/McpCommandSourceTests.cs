using BrutalTestEngine.Commands;
using BrutalTestEngine.Host;

namespace BrutalTestEngine.Tests.Host;

/// <summary>
/// Covers the MCP command source: the bridge between a tool call and the engine's command bus.
/// </summary>
/// <remarks>
/// The point of these tests is that an MCP tool call is not a privileged path. It parses text with the shared
/// grammar and posts to the same bus as every other source, so what is asserted here is that it does exactly
/// that and nothing more.
/// </remarks>
[TestFixture]
public sealed class McpCommandSourceTests
{
    [Test]
    public void A_tool_call_posts_a_command_to_the_bus()
    {
        using var bus = new CommandBus();
        var source = new McpCommandSource(bus);

        var sequence = source.Post("pause", out var count);

        Assert.That(count, Is.EqualTo(1));
        Assert.That(sequence, Is.GreaterThan(0));
        Assert.That(bus.AcceptedCount, Is.EqualTo(1));
        Assert.That(bus.TryDequeue(CommandTarget.Simulation, out var command), Is.True);
        Assert.That(command, Is.TypeOf<SetPausedCommand>());
    }

    [Test]
    public void Commands_are_attributed_to_the_mcp_source()
    {
        // "Who asked" has to survive the hop, or per-source policy has nothing to act on.
        using var bus = new CommandBus();
        var source = new McpCommandSource(bus);

        source.Post("tick-rate 120", out _);

        Assert.That(bus.TryDequeue(CommandTarget.Engine, out var command), Is.True);
        Assert.That(command.Source, Is.EqualTo(CommandSource.Mcp));
    }

    [Test]
    public void A_multi_line_payload_posts_every_line()
    {
        using var bus = new CommandBus();
        var source = new McpCommandSource(bus);

        var sequence = source.Post("pause\nstep 5\nresume", out var count);

        Assert.That(count, Is.EqualTo(3));
        Assert.That(bus.AcceptedCount, Is.EqualTo(3));

        // The returned sequence is the last command's, so waiting for it implies the earlier ones are applied.
        Assert.That(sequence, Is.EqualTo(3));
    }

    [Test]
    public void Text_that_produces_no_command_is_counted_as_rejected()
    {
        using var bus = new CommandBus();
        var source = new McpCommandSource(bus);

        var sequence = source.Post("\n\n   \n", out var count);

        Assert.That(count, Is.Zero);
        Assert.That(sequence, Is.Zero);
        Assert.That(source.RejectedCount, Is.EqualTo(1));
        Assert.That(bus.AcceptedCount, Is.Zero);
    }

    [Test]
    public void An_unknown_verb_still_reaches_the_bus_as_an_unhandled_command()
    {
        // An unrecognised intent is a decision the engine makes, not something the transport silently drops.
        using var bus = new CommandBus();
        var source = new McpCommandSource(bus);

        source.Post("frobnicate the widget", out var count);

        Assert.That(count, Is.EqualTo(1));
        Assert.That(bus.TryDequeue(CommandTarget.Engine, out var command), Is.True);
        Assert.That(command, Is.TypeOf<UnhandledCommand>());
    }

    [Test]
    public void Posted_commands_are_recorded_for_inspection()
    {
        using var bus = new CommandBus();
        var source = new McpCommandSource(bus);

        source.Post("pause", out _);
        source.Post("resume", out _);

        var recent = source.RecentCommands;

        Assert.That(recent, Has.Length.EqualTo(2));
        Assert.That(recent[0], Is.TypeOf<SetPausedCommand>());
        Assert.That(recent[1], Is.TypeOf<SetPausedCommand>());
    }

    [Test]
    public void The_recent_command_history_is_bounded()
    {
        using var bus = new CommandBus();
        var source = new McpCommandSource(bus);

        for (var i = 0; i < 200; i++)
            source.Post("pause", out _);

        Assert.That(source.RecentCommands.Length, Is.LessThanOrEqualTo(64));
        Assert.That(source.CommandsPosted, Is.EqualTo(200));
    }

    [Test]
    public void A_null_payload_throws()
    {
        using var bus = new CommandBus();
        var source = new McpCommandSource(bus);

        Assert.That(() => source.Post(null!, out _), Throws.TypeOf<ArgumentNullException>());
    }

    [Test]
    public void A_null_bus_throws()
    {
        Assert.That(() => new McpCommandSource(null!), Throws.TypeOf<ArgumentNullException>());
    }
}
