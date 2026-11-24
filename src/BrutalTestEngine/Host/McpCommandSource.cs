using System.Collections.Concurrent;
using BrutalTestEngine.Commands;

namespace BrutalTestEngine.Host;

/// <summary>
/// The bridge between an MCP client and the engine's command bus.
/// </summary>
/// <remarks>
/// <para>
/// An MCP tool call arrives on an HTTP worker thread and must not touch engine state, so it does what every
/// other source does: it parses the text with the shared parser and posts the result to the bus. The engine
/// then applies it on the thread that owns the effect, exactly as if the line had been typed at the console.
/// There is no second path into the engine, and no MCP-specific command vocabulary to keep in sync.
/// </para>
/// <para>
/// The one thing a tool call needs that a fire-and-forget source does not is an answer. "Did the engine
/// accept this?" is answered by the bus sequence number, which is assigned synchronously inside
/// <see cref="CommandBus.Post"/> and therefore needs no waiting. "What did the engine say?" is answered by
/// the notice the engine publishes when it applies the command, which is asynchronous — so the source
/// records the sequence it is waiting for and reports the notice once the engine has passed it. That keeps
/// the tool call non-blocking on the engine's frame loop while still returning a real result.
/// </para>
/// </remarks>
public sealed class McpCommandSource
{
    private const int MaxRecentCommands = 64;

    private readonly CommandBus _bus;
    private readonly ConcurrentQueue<Command> _recent = new();
    private long _commandsPosted;
    private long _rejected;

    /// <param name="bus">The command bus tool calls are posted to.</param>
    public McpCommandSource(CommandBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <summary>Number of commands posted from MCP tool calls.</summary>
    public long CommandsPosted => Interlocked.Read(ref _commandsPosted);

    /// <summary>Number of tool calls whose text produced no command at all.</summary>
    public long RejectedCount => Interlocked.Read(ref _rejected);

    /// <summary>The most recent commands posted from MCP, oldest first.</summary>
    public Command[] RecentCommands => [.. _recent];

    /// <summary>
    /// Parses a line and posts every command it contains, returning the sequence number of the last one.
    /// </summary>
    /// <remarks>
    /// A multi-line payload is accepted for the same reason every other transport accepts one: the grammar is
    /// line-oriented, so a batch is just several lines. The returned sequence is the last command's, which is
    /// the one whose application implies all the earlier ones have been applied too.
    /// </remarks>
    public long Post(string text, out int commandCount)
    {
        ArgumentNullException.ThrowIfNull(text);

        var commands = CommandParser.ParseMany(text, CommandSource.Mcp);
        var sequence = 0L;
        commandCount = 0;

        foreach (var command in commands)
        {
            sequence = _bus.Post(command);
            commandCount++;

            _recent.Enqueue(command);
            while (_recent.Count > MaxRecentCommands)
                _recent.TryDequeue(out _);
        }

        if (commandCount == 0)
        {
            Interlocked.Increment(ref _rejected);
            return 0;
        }

        Interlocked.Add(ref _commandsPosted, commandCount);
        return sequence;
    }
}
