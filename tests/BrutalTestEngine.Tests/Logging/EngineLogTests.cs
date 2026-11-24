using Brutal.Logging;
using BrutalTestEngine.Logging;
using Microsoft.Extensions.Logging;

namespace BrutalTestEngine.Tests.Logging;

/// <summary>
/// The logging contract the engine depends on: a disabled category must be free, and the pipeline must be
/// safe to use from any thread.
/// </summary>
/// <remarks>
/// These pin the property that motivated the whole design. If a future change makes a disabled log call
/// allocate, the engine's hot loops silently get slower and nothing else would notice.
/// </remarks>
[TestFixture]
public class EngineLogTests
{
    private const int Iterations = 20_000;

    /// <summary>
    /// A disabled category must not allocate. This is what "zero-copy on the caller thread" means concretely:
    /// the interpolated-string handler is never run, so nothing is formatted and nothing is boxed.
    /// </summary>
    [Test]
    public void A_disabled_category_does_not_allocate_on_the_caller_thread()
    {
        var category = LogCategory.Make($"test.disabled.{Guid.NewGuid():N}");
        category.IsCategoryEnabled = false;

        // Warm up first: the first call through a fresh category can still cause one-time framework work,
        // and measuring it would look like a per-call cost.
        for (var i = 0; i < 16; i++)
            category.Info($"warmup {i}");

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < Iterations; i++)
            category.Info($"iteration {i} value {i * 2}");

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.That(allocated, Is.Zero, $"a disabled log call allocated {allocated} bytes over {Iterations} calls");
    }

    /// <summary>
    /// Disabling a category must be honoured by the enabled check too, not only by the message call. Callers
    /// use <see cref="LogCategory.IsEnabled"/> to guard work that would otherwise be thrown away.
    /// </summary>
    [Test]
    public void Disabling_a_category_makes_its_levels_report_as_disabled()
    {
        var category = LogCategory.Make($"test.levels.{Guid.NewGuid():N}");

        category.IsCategoryEnabled = false;

        Assert.Multiple(() =>
        {
            Assert.That(category.IsEnabled(LogLevel.Information), Is.False);
            Assert.That(category.IsEnabled(LogLevel.Error), Is.False);
        });

        category.IsCategoryEnabled = true;

        Assert.That(category.IsEnabled(LogLevel.Error), Is.True);
    }

    /// <summary>
    /// The engine caches category references in fields and calls them from several threads, so two requests
    /// for the same name must be the same object. A per-call instance would defeat the caching and split one
    /// subsystem's logs across two independent enable switches.
    /// </summary>
    [Test]
    public void The_same_category_name_returns_the_same_instance()
    {
        var name = $"test.shared.{Guid.NewGuid():N}";

        Assert.That(LogCategory.Make(name), Is.SameAs(LogCategory.Make(name)));
    }

    /// <summary>
    /// A host that never configures logging must still get a working engine, so the engine falls back to a
    /// default pipeline rather than throwing on the first log call.
    /// </summary>
    [Test]
    public void InitializeDefault_is_safe_after_logging_is_already_configured()
    {
        EngineLog.Initialize(
            new EngineLogOptions { Console = false, MinimumLevel = Serilog.Events.LogEventLevel.Warning }
        );

        Assert.DoesNotThrow(EngineLog.InitializeDefault);
        Assert.That(EngineLog.IsInitialized, Is.True);
    }

    /// <summary>
    /// Changing the level at runtime has to reach the same switch the categories consult, or the `log-level`
    /// command would appear to work while changing nothing.
    /// </summary>
    [Test]
    public void Changing_the_minimum_level_updates_the_shared_switch()
    {
        EngineLog.Initialize(new EngineLogOptions { Console = false });

        var original = EngineLog.MinimumLevel;
        try
        {
            EngineLog.MinimumLevel = Serilog.Events.LogEventLevel.Debug;

            Assert.That(EngineLog.LevelSwitch.MinimumLevel, Is.EqualTo(Serilog.Events.LogEventLevel.Debug));
        }
        finally
        {
            EngineLog.MinimumLevel = original;
        }
    }

    /// <summary>
    /// Every named category has to exist, because a thread that logs through a null reference would fail at
    /// the worst possible moment.
    /// </summary>
    [Test]
    public void Every_named_category_is_available()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EngineLog.Categories.Engine, Is.Not.Null);
            Assert.That(EngineLog.Categories.Main, Is.Not.Null);
            Assert.That(EngineLog.Categories.Simulation, Is.Not.Null);
            Assert.That(EngineLog.Categories.Render, Is.Not.Null);
            Assert.That(EngineLog.Categories.Vulkan, Is.Not.Null);
            Assert.That(EngineLog.Categories.Input, Is.Not.Null);
            Assert.That(EngineLog.Categories.Network, Is.Not.Null);
            Assert.That(EngineLog.Categories.Io, Is.Not.Null);
            Assert.That(EngineLog.Categories.Commands, Is.Not.Null);
            Assert.That(EngineLog.Categories.Capture, Is.Not.Null);
        });
    }

    [OneTimeTearDown]
    public void TearDown() => EngineLog.Shutdown();
}
