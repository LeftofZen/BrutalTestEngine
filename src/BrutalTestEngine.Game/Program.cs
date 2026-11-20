using System.Globalization;
using BrutalTestEngine.Commands;
using BrutalTestEngine.Engine;

// The game's entry point. It is deliberately blank: it parses the engine's command-line options and runs one
// engine. Gameplay, scenes and content belong here later; for now this project exists to prove the engine is
// a library a game can host, not an application that owns the process.

var options = new EngineOptions();
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
    else if (arg.StartsWith("--database=", StringComparison.OrdinalIgnoreCase))
        options = options with { DatabasePath = arg["--database=".Length..] };
    else if (arg.Equals("--validate", StringComparison.OrdinalIgnoreCase))
        options = options with { EnableValidation = true };
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
            "Usage: BrutalTestEngine [--frames=N] [--tick-rate=HZ] [--web=PORT] [--database=PATH] "
            + "[--validate] [--fullscreen] [--selftest]"
        );
        Console.WriteLine("  --frames=N       Run N frames then exit, instead of waiting for the window to close.");
        Console.WriteLine("  --tick-rate=HZ   Fixed simulation timestep in Hz (default 60).");
        Console.WriteLine("  --validate       Enable Vulkan validation layers.");
        Console.WriteLine("  --fullscreen     Start borderless fullscreen.");
        Console.WriteLine("  --web=PORT       Serve the web command source on PORT.");
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
