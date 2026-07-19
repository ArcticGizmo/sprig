using System.Reflection;

// Sprig CLI — internal harness that drives Sprig.Core during development.
// Not the shipped product (the Avalonia app is); this exists so every milestone
// is runnable and testable end-to-end before the UI exists.

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine($"""
        sprig {version} — worktree + infrastructure isolation (dev harness)

        USAGE:
            sprig <command> [options]

        COMMANDS:
            (none yet — Core spine under construction, see docs/tasks-m0-m1.md)

        OPTIONS:
            -h, --help       Show this help
            -v, --version    Show version
        """);
    return 0;
}

if (args[0] is "-v" or "--version" or "version")
{
    Console.WriteLine(version);
    return 0;
}

Console.Error.WriteLine($"unknown command: {args[0]} (try --help)");
return 1;
