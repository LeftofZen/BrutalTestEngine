using System.Text;

namespace BrutalTestEngine.Engine;

/// <summary>
/// One readable engine variable: its name, its current value as text, and enough metadata for a consumer to
/// know what it is looking at.
/// </summary>
/// <param name="Name">The variable's name, for example <c>sim.tick</c>.</param>
/// <param name="Value">The current value, rendered as text.</param>
/// <param name="Kind">The value's shape, for example <c>long</c>, <c>double</c>, <c>bool</c> or <c>string</c>.</param>
/// <param name="Description">What the variable means.</param>
public readonly record struct CvarValue(string Name, string Value, string Kind, string Description);

/// <summary>
/// The engine's registry of readable variables: the single place a non-human consumer can ask what the
/// engine currently thinks.
/// </summary>
/// <remarks>
/// <para>
/// The debug panel is a rendering of engine state for a person looking at a screen. This is the same state
/// for a program looking at the process, and it exists because the two have different failure modes: a
/// screenshot cannot be diffed, cannot be asserted on, and cannot be read at all when the window is
/// minimised or the machine is headless.
/// </para>
/// <para>
/// A cvar is a <em>reader</em>, not a field. Registering a delegate rather than a value means the registry
/// never holds a stale copy: the value is produced at the moment it is asked for, from whatever thread owns
/// it, through the same accessors the debug panel uses. That is what makes it safe to read a running engine
/// from another thread without a lock on the engine's state.
/// </para>
/// <para>
/// Registration happens once, during composition, on the main thread. Reads may come from any thread
/// afterwards, so the table is guarded by a lock and every read is a snapshot. The cost is a dictionary
/// lookup and a delegate call per variable, which is irrelevant next to the fact that the alternative is
/// reading torn state.
/// </para>
/// <para>
/// Values are rendered as text rather than as a typed union because the consumers are a debug panel, a log
/// line and a JSON document, and all three want text. A typed union would have to be re-rendered by each of
/// them anyway, and would have to be extended for every new variable shape.
/// </para>
/// </remarks>
public sealed class CvarRegistry
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private readonly record struct Entry(Func<string> Read, string Kind, string Description);

    /// <summary>Number of registered variables.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    /// <summary>
    /// Registers a variable whose value is produced by a reader that already returns text.
    /// </summary>
    /// <param name="name">The variable's name. Dotted and lower-case by convention, for example <c>net.poll</c>.</param>
    /// <param name="read">Produces the current value. Called on whichever thread asks.</param>
    /// <param name="kind">The value's shape, for example <c>long</c> or <c>double</c>.</param>
    /// <param name="description">What the variable means.</param>
    public void Register(string name, Func<string> read, string kind, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        lock (_gate)
            _entries[name] = new Entry(read, kind, description ?? string.Empty);
    }

    /// <summary>
    /// Registers a variable whose value is read as a number, a boolean or any other formattable value.
    /// </summary>
    /// <remarks>
    /// A separate name from <see cref="Register(string, Func{string}, string, string)"/> rather than an
    /// overload, because a lambda's return type does not participate in overload resolution: a
    /// <c>Func&lt;long&gt;</c> would bind to the text overload and fail to compile. Naming the two
    /// differently makes the choice explicit at every call site, and the value is formatted with the
    /// invariant culture so a variable reads the same on every machine.
    /// </remarks>
    public void RegisterValue<T>(string name, Func<T> read, string kind, string description)
    {
        ArgumentNullException.ThrowIfNull(read);

        Register(
            name,
            () => Convert.ToString(read(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            kind,
            description
        );
    }

    /// <summary>
    /// Reads every variable, ordered by name so the output is stable between calls and can be diffed.
    /// </summary>
    /// <remarks>
    /// The readers are invoked outside the lock. A reader that is slow, or that itself takes a lock, must not
    /// be able to stall registration or another reader — and a reader that threw must not take the whole
    /// snapshot down with it, because one broken variable is not a reason to lose every other one.
    /// </remarks>
    public CvarValue[] Snapshot() => ReadAll(excludeName: null);

    /// <summary>
    /// Renders every variable except one as a multi-line <c>name = value</c> listing, ordered by name.
    /// </summary>
    /// <param name="excludeName">
    /// A variable to leave out of the listing. A variable whose own reader calls this method must exclude
    /// itself, or reading it recurses until the stack runs out.
    /// </param>
    /// <remarks>
    /// <para>
    /// This exists so one variable can describe the whole registry — the <c>help</c> variable is the reason —
    /// without every caller having to walk <see cref="Snapshot"/> and format the result itself. The exclusion
    /// is applied to the name list <em>before</em> any reader runs, so the excluded variable's reader is never
    /// invoked and cannot re-enter this method.
    /// </para>
    /// <para>
    /// The format is one variable per line: <c>name = value [kind] description</c>, with the description
    /// omitted when it is empty. It is deliberately plain text, because the consumers are a log line, a
    /// console and a model reading a tool result, and all three want something a person can scan.
    /// </para>
    /// </remarks>
    public string DescribeAll(string? excludeName = null)
    {
        var values = ReadAll(excludeName);

        var text = new StringBuilder();
        foreach (var value in values)
        {
            if (text.Length > 0)
                text.Append('\n');

            text.Append(value.Name).Append(" = ").Append(value.Value);
            text.Append(" [").Append(value.Kind).Append(']');

            if (value.Description.Length > 0)
                text.Append(' ').Append(value.Description);
        }

        return text.ToString();
    }

    /// <summary>
    /// Reads every variable except <paramref name="excludeName"/>, ordered by name.
    /// </summary>
    /// <remarks>
    /// The single implementation behind <see cref="Snapshot"/> and <see cref="DescribeAll"/>, so the two
    /// cannot drift on ordering, error handling or locking. The name list is copied under the lock and the
    /// readers are invoked outside it.
    /// </remarks>
    private CvarValue[] ReadAll(string? excludeName)
    {
        KeyValuePair<string, Entry>[] entries;

        lock (_gate)
        {
            if (excludeName is null)
            {
                entries = [.. _entries];
            }
            else
            {
                var kept = new List<KeyValuePair<string, Entry>>(_entries.Count);

                foreach (var entry in _entries)
                {
                    if (!string.Equals(entry.Key, excludeName, StringComparison.Ordinal))
                        kept.Add(entry);
                }

                entries = [.. kept];
            }
        }

        Array.Sort(entries, static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        var values = new CvarValue[entries.Length];
        for (var i = 0; i < entries.Length; i++)
        {
            var (name, entry) = (entries[i].Key, entries[i].Value);

            string value;
            try
            {
                value = entry.Read();
            }
            catch (Exception exception)
            {
                value = $"<error: {exception.GetType().Name}>";
            }

            values[i] = new CvarValue(name, value, entry.Kind, entry.Description);
        }

        return values;
    }

    /// <summary>Reads one variable by name.</summary>
    public bool TryGet(string name, out CvarValue value)
    {
        Entry entry;

        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out entry))
            {
                value = default;
                return false;
            }
        }

        string text;
        try
        {
            text = entry.Read();
        }
        catch (Exception exception)
        {
            text = $"<error: {exception.GetType().Name}>";
        }

        value = new CvarValue(name, text, entry.Kind, entry.Description);
        return true;
    }
}
