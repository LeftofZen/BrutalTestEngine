using BrutalTestEngine.Engine;

namespace BrutalTestEngine.Tests.Engine;

/// <summary>
/// Covers the variable registry that the MCP surface and the debug panel both read from.
/// </summary>
[TestFixture]
public sealed class CvarRegistryTests
{
    [Test]
    public void An_empty_registry_has_no_variables()
    {
        var registry = new CvarRegistry();

        Assert.That(registry.Count, Is.Zero);
        Assert.That(registry.Snapshot(), Is.Empty);
    }

    [Test]
    public void A_registered_variable_is_readable_by_name()
    {
        var registry = new CvarRegistry();
        registry.RegisterValue("sim.tick", () => 42L, "long", "Ticks executed.");

        Assert.That(registry.TryGet("sim.tick", out var value), Is.True);
        Assert.That(value.Name, Is.EqualTo("sim.tick"));
        Assert.That(value.Value, Is.EqualTo("42"));
        Assert.That(value.Kind, Is.EqualTo("long"));
        Assert.That(value.Description, Is.EqualTo("Ticks executed."));
    }

    [Test]
    public void An_unknown_variable_is_reported_as_missing()
    {
        var registry = new CvarRegistry();

        Assert.That(registry.TryGet("nope", out _), Is.False);
    }

    [Test]
    public void Values_are_read_live_rather_than_captured_at_registration()
    {
        // The property the whole design rests on: a reader sees the engine as it is now, not as it was when
        // the variable was registered. A captured value would make the MCP surface a startup snapshot.
        var counter = 0;
        var registry = new CvarRegistry();
        registry.RegisterValue("counter", () => counter, "int", "A counter.");

        Assert.That(registry.TryGet("counter", out var first), Is.True);
        Assert.That(first.Value, Is.EqualTo("0"));

        counter = 7;

        Assert.That(registry.TryGet("counter", out var second), Is.True);
        Assert.That(second.Value, Is.EqualTo("7"));
    }

    [Test]
    public void Snapshot_is_ordered_by_name_so_output_is_stable()
    {
        var registry = new CvarRegistry();
        registry.RegisterValue("zulu", () => 1, "int", "");
        registry.RegisterValue("alpha", () => 2, "int", "");
        registry.RegisterValue("mike", () => 3, "int", "");

        var names = registry.Snapshot().Select(value => value.Name).ToArray();

        Assert.That(names, Is.EqualTo(new[] { "alpha", "mike", "zulu" }));
    }

    [Test]
    public void Re_registering_a_name_replaces_the_reader_rather_than_duplicating_it()
    {
        var registry = new CvarRegistry();
        registry.RegisterValue("value", () => 1, "int", "first");
        registry.RegisterValue("value", () => 2, "int", "second");

        Assert.That(registry.Count, Is.EqualTo(1));
        Assert.That(registry.TryGet("value", out var value), Is.True);
        Assert.That(value.Value, Is.EqualTo("2"));
        Assert.That(value.Description, Is.EqualTo("second"));
    }

    [Test]
    public void A_reader_that_throws_does_not_take_the_snapshot_down_with_it()
    {
        // One broken variable is not a reason to lose every other one, and a client that asked for the whole
        // set should still get the set.
        var registry = new CvarRegistry();
        registry.Register("broken", () => throw new InvalidOperationException("boom"), "string", "");
        registry.RegisterValue("fine", () => 5, "int", "");

        var values = registry.Snapshot();

        Assert.That(values, Has.Length.EqualTo(2));
        Assert.That(values.Single(value => value.Name == "broken").Value, Does.Contain("InvalidOperationException"));
        Assert.That(values.Single(value => value.Name == "fine").Value, Is.EqualTo("5"));
    }

    [Test]
    public void A_reader_that_throws_is_reported_by_name_lookup_too()
    {
        var registry = new CvarRegistry();
        registry.Register("broken", () => throw new InvalidOperationException("boom"), "string", "");

        Assert.That(registry.TryGet("broken", out var value), Is.True);
        Assert.That(value.Value, Does.Contain("InvalidOperationException"));
    }

    [Test]
    public void Values_are_formatted_with_the_invariant_culture()
    {
        // A variable must read the same on every machine, or a client comparing two runs sees a difference
        // that is only a decimal separator.
        var registry = new CvarRegistry();
        registry.RegisterValue("ratio", () => 1.5, "double", "");

        Assert.That(registry.TryGet("ratio", out var value), Is.True);
        Assert.That(value.Value, Is.EqualTo("1.5"));
    }

    [Test]
    public void Booleans_are_readable()
    {
        var registry = new CvarRegistry();
        registry.RegisterValue("paused", () => true, "bool", "");

        Assert.That(registry.TryGet("paused", out var value), Is.True);
        Assert.That(value.Value, Is.EqualTo("True"));
    }

    [Test]
    public void DescribeAll_lists_every_variable_ordered_by_name()
    {
        var registry = new CvarRegistry();
        registry.RegisterValue("zulu", () => 3, "int", "Third.");
        registry.RegisterValue("alpha", () => 1, "int", "First.");

        var lines = registry.DescribeAll().Split('\n');

        Assert.That(lines, Has.Length.EqualTo(2));
        Assert.That(lines[0], Does.StartWith("alpha = 1"));
        Assert.That(lines[1], Does.StartWith("zulu = 3"));
    }

    [Test]
    public void DescribeAll_includes_the_kind_and_the_description()
    {
        var registry = new CvarRegistry();
        registry.RegisterValue("sim.tick", () => 42L, "long", "Ticks executed.");

        Assert.That(registry.DescribeAll(), Is.EqualTo("sim.tick = 42 [long] Ticks executed."));
    }

    [Test]
    public void DescribeAll_omits_the_excluded_variable()
    {
        var registry = new CvarRegistry();
        registry.RegisterValue("alpha", () => 1, "int", "");
        registry.RegisterValue("help", () => "listing", "string", "");

        var text = registry.DescribeAll("help");

        Assert.That(text, Does.Contain("alpha = 1"));
        Assert.That(text, Does.Not.Contain("help ="));
    }

    [Test]
    public void DescribeAll_does_not_recurse_when_the_excluded_variable_reads_it()
    {
        // The reason the exclusion exists: a variable whose reader walks the registry would re-enter its own
        // reader and recurse until the stack ran out. Reading it must return, not overflow.
        var registry = new CvarRegistry();
        registry.RegisterValue("alpha", () => 1, "int", "");
        registry.Register("help", () => registry.DescribeAll("help"), "string", "Every other variable.");

        Assert.That(registry.TryGet("help", out var value), Is.True);
        Assert.That(value.Value, Does.Contain("alpha = 1"));
        Assert.That(value.Value, Does.Not.Contain("help ="));
    }

    [Test]
    public void DescribeAll_tolerates_a_reader_that_throws()
    {
        var registry = new CvarRegistry();
        registry.Register("broken", () => throw new InvalidOperationException("boom"), "string", "");
        registry.RegisterValue("fine", () => 1, "int", "");

        var text = registry.DescribeAll();

        Assert.That(text, Does.Contain("broken = <error: InvalidOperationException>"));
        Assert.That(text, Does.Contain("fine = 1"));
    }

    [Test]
    public void DescribeAll_of_an_empty_registry_is_empty()
    {
        Assert.That(new CvarRegistry().DescribeAll(), Is.Empty);
    }

    [Test]
    public void Registering_a_blank_name_throws()
    {
        var registry = new CvarRegistry();

        Assert.That(() => registry.RegisterValue("  ", () => 1, "int", ""), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void Registering_a_null_reader_throws()
    {
        var registry = new CvarRegistry();

        Assert.That(() => registry.Register("name", null!, "int", ""), Throws.TypeOf<ArgumentNullException>());
    }
}
