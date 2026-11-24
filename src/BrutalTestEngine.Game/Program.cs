using System.Globalization;
using Brutal;
using BrutalTestEngine;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;
using BrutalTestEngine.Logging;
using Serilog.Events;

// The game's entry point. It is deliberately blank: it parses the engine's command-line options and runs one
// engine. Gameplay, scenes and content belong here later; for now this project exists to prove the engine is
// a library a game can host, not an application that owns the process.

var options = new EngineOptions();
var logOptions = new EngineLogOptions();
foreach (var arg in args)
{
    if (arg.StartsWith("--frames=", StringComparison.OrdinalIgnoreCase))
        options = options with { FrameLimit = int.Parse(arg["--frames=".Length..], CultureInfo.InvariantCulture) };
    else if (arg.StartsWith("--tick-rate=", StringComparison.OrdinalIgnoreCase))
        options = options with
        {
            TicksPerSecond = double.Parse(arg["--tick-rate=".Length..], CultureInfo.InvariantCulture),
        };
    else if (arg.StartsWith("--web=", StringComparison.OrdinalIgnoreCase))
        options = options with { WebPort = int.Parse(arg["--web=".Length..], CultureInfo.InvariantCulture) };
    else if (arg.StartsWith("--mcp=", StringComparison.OrdinalIgnoreCase))
        options = options with { McpPort = int.Parse(arg["--mcp=".Length..], CultureInfo.InvariantCulture) };
    else if (arg.StartsWith("--database=", StringComparison.OrdinalIgnoreCase))
        options = options with { DatabasePath = arg["--database=".Length..] };
    else if (arg.StartsWith("--log=", StringComparison.OrdinalIgnoreCase))
        logOptions = logOptions with { MinimumLevel = ParseLogLevel(arg["--log=".Length..]) };
    else if (arg.StartsWith("--log-file=", StringComparison.OrdinalIgnoreCase))
        logOptions = logOptions with { FilePath = arg["--log-file=".Length..] };
    else if (arg.StartsWith("--captures=", StringComparison.OrdinalIgnoreCase))
        options = options with { CaptureDirectory = arg["--captures=".Length..] };
    else if (arg.Equals("--validate", StringComparison.OrdinalIgnoreCase))
        options = options with { EnableValidation = true, EnableTracing = true };
    else if (arg.Equals("--trace", StringComparison.OrdinalIgnoreCase))
        options = options with { EnableTracing = true };
    else if (arg.Equals("--renderdoc", StringComparison.OrdinalIgnoreCase))
        options = options with { EnableRenderDoc = true };
    else if (arg.Equals("--physics", StringComparison.OrdinalIgnoreCase))
        options = options with { PhysicsFactory = PhysicsWorlds.Jolt };
    else if (arg.Equals("--no-audio", StringComparison.OrdinalIgnoreCase))
        options = options with { AudioFactory = AudioSystems.None };
    else if (arg.Equals("--fullscreen", StringComparison.OrdinalIgnoreCase))
        options = options with { StartFullscreen = true };
    else if (arg.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
        options = options with
        {
            RunCommandSelfTest = true,
            WebPort = 41800,
            DatabasePath = Path.Combine(Path.GetTempPath(), "bte-selftest.db"),
            FrameLimit = 600,
        };
    else if (arg is "-h" or "--help")
    {
        Console.WriteLine(
            "Usage: BrutalTestEngine [--frames=N] [--tick-rate=HZ] [--web=PORT] [--mcp=PORT] [--database=PATH] "
            + "[--log=LEVEL] [--log-file=PATH] [--validate] [--trace] [--renderdoc] "
            + "[--captures=DIR] [--physics] [--no-audio] [--fullscreen] [--selftest]"
        );
        Console.WriteLine("  --frames=N       Run N frames then exit, instead of waiting for the window to close.");
        Console.WriteLine("  --tick-rate=HZ   Fixed simulation timestep in Hz (default 60).");
        Console.WriteLine("  --log=LEVEL      Log level: verbose|debug|information|warning|error|fatal.");
        Console.WriteLine("  --log-file=PATH  Also write rolling log files to PATH.");
        Console.WriteLine("  --validate       Enable Vulkan validation layers and debug tracing.");
        Console.WriteLine("  --trace          Enable Vulkan debug labels for GPU capture, without validation.");
        Console.WriteLine("  --renderdoc      Enable RenderDoc in-application capture when attached.");
        Console.WriteLine("  --captures=DIR   Where RenderDoc writes captures (default: its own).");
        Console.WriteLine("  --physics        Enable the Jolt physics world (off by default; see the README).");
        Console.WriteLine("  --no-audio       Do not open an audio device.");
        Console.WriteLine("  --fullscreen     Start borderless fullscreen.");
        Console.WriteLine("  --web=PORT       Serve the web command source on PORT.");
        Console.WriteLine("  --mcp=PORT       Serve the MCP server on PORT, exposing every engine variable.");
        Console.WriteLine("  --database=PATH  Watch a SQLite database for commands.");
        Console.WriteLine("  --selftest       Run the command self-test and exit.");
        Console.WriteLine("  -h, --help       Show this help.");
        Console.WriteLine();
        Console.WriteLine(CommandParser.HelpText);
        return 0;
    }
    else
    {
        Console.Error.WriteLine($"Unknown argument: {arg}");
        return 2;
    }
}

if (options.TicksPerSecond <= 0)
{
    Console.Error.WriteLine("--tick-rate must be greater than zero.");
    return 2;
}

if (options.WebPort is { } webPortCheck && webPortCheck is < 1024 or > 65535)
{
    Console.Error.WriteLine("--web must be a port between 1024 and 65535.");
    return 2;
}

if (options.McpPort is { } mcpPortCheck && mcpPortCheck is < 1024 or > 65535)
{
    Console.Error.WriteLine("--mcp must be a port between 1024 and 65535.");
    return 2;
}

// Logging is the first thing that happens, so anything that goes wrong from here on has somewhere to go. The
// pipeline is asynchronous, so this call returns immediately and no thread ever blocks writing a log line.
EngineLog.Initialize(logOptions);

try
{
    using var engine = Engine.Create(options);
    engine.Run();

    Console.WriteLine(
        $"Presented {engine.FrameCount} frame(s), simulated {engine.TickCount} tick(s), "
            + $"accepted {engine.CommandsAccepted} command(s), "
            + $"applied {engine.EngineCommandsApplied} engine command(s)."
    );

    // Printed after the run so a headless `--frames` invocation reports what each thread cost, which is
    // the only timing information available when there is no debug panel to look at.
    Console.WriteLine();
    Console.WriteLine("Thread timings:");
    engine.Timers.Describe(Console.WriteLine);

    if (engine.Capture.IsAvailable)
    {
        // Reported as "attached" plus a count, because those are two different facts: RenderDoc being hooked in
        // says nothing about whether a capture was asked for, and a run that took none is not a failure.
        Console.WriteLine();
        Console.WriteLine(
            $"RenderDoc {engine.Capture.ApiVersionNumber} attached: "
                + $"{engine.Capture.CaptureCount} capture(s) taken. Use the `capture` command to take one."
        );
    }

    if (EngineLog.DroppedCount > 0)
        Console.Error.WriteLine($"Warning: the log dropped {EngineLog.DroppedCount} event(s); raise its buffer.");

    if (engine.SelfTest is not { } selfTest)
        return 0;

    Console.WriteLine();
    Console.WriteLine("Command self-test report:");
    foreach (var line in selfTest.Report)
        Console.WriteLine($"  {line}");

    if (!selfTest.Finished)
    {
        Console.Error.WriteLine("Command self-test did not finish before the frame limit was reached.");
        return 3;
    }

    if (!selfTest.Passed)
    {
        Console.Error.WriteLine("Command self-test FAILED.");
        return 4;
    }

    Console.WriteLine("Command self-test PASSED.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Fatal: {exception}");
    return 1;
}
finally
{
    // Drains the asynchronous sink. Without this the last events before an exit are lost, which is usually
    // exactly the ones that explain the exit.
    EngineLog.Shutdown();
}

static LogEventLevel ParseLogLevel(string value) =>
    value.ToLowerInvariant() switch
    {
        "verbose" or "trace" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "information" or "info" => LogEventLevel.Information,
        "warning" or "warn" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" or "critical" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
