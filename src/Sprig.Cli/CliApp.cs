using Sprig.Cli.Commands;
using Sprig.Engine.Definitions;
using Sprig.Engine.Settings;
using Sprig.Engine.Trellises;
using Sprig.Engine.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli;

/// <summary>The CLI's whole surface: a Spectre.Console <see cref="CommandApp"/> over Sprig.Engine. <c>--json</c>
/// output is a stability contract, so it is written straight to stdout, never through the markup renderer.</summary>
public static class CliApp
{
    // SprigPaths' default root honours SPRIG_STORE, so a throwaway store applies here and in the app alike.
    public static int Run(string[] args) => Run(args, new SprigPaths());

    internal static int Run(string[] args, ISprigPaths paths)
    {
        // A failure before or inside a command still has to honour --json, so detect it up front.
        var json = args.Contains("--json");

        // Spectre caches the console it renders help and errors through, so one shared console (late-bound to
        // Console.Out) serves every run instead of a fresh one per run that a later test would find disposed.
        var ansi = SharedConsole;
        AnsiConsole.Console = ansi;

        var trust = new FileTrustStore(paths);
        var context = new CliContext(paths, new FileSettingsStore(paths), trust,
            new DefinitionRunner(trust, new DotnetRunHost(), paths), new TrellisRegistry(paths), ansi);

        var registrar = new TypeRegistrar(new Dictionary<Type, object>
        {
            [typeof(CliContext)] = context,
            [typeof(IAnsiConsole)] = ansi,
        });

        var app = new CommandApp(registrar);
        app.Configure(Configure);

        try
        {
            return app.Run(args);
        }
        catch (Exception ex)
        {
            // Exit code 1 either way; machine callers get { ok: false, error }, humans a line on stderr.
            if (json) CliOutput.Json(new { ok = false, error = ex.Message });
            else Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static void Configure(IConfigurator config)
    {
        config.SetApplicationName("sprig");
        config.SetApplicationVersion(Version());
        // Errors bubble to Run's catch so the --json/stderr contract and exit code 1 live in one place.
        config.PropagateExceptions();
        // A typo'd flag should fail loudly instead of quietly running a command it didn't mean.
        config.UseStrictParsing();

        config.AddCommand<OpenCommand>("open");
        config.AddCommand<UpdateCommand>("update");
        config.AddCommand<PlanCommand>("plan");

        config.AddBranch("trellis", trellis =>
        {
            trellis.SetDescription("Register the trellis files that put repos together");
            trellis.AddCommand<TrellisAddCommand>("add");
            trellis.AddCommand<TrellisLsCommand>("ls");
            trellis.AddCommand<TrellisRmCommand>("rm");
        });

        config.AddBranch("trust", trust =>
        {
            trust.SetDescription("Approve definition files to run (by exact content)");
            trust.AddCommand<TrustAddCommand>("add");
            trust.AddCommand<TrustLsCommand>("ls");
        });

        config.AddBranch("shell", shell =>
        {
            shell.SetDescription("Shell integration (PATH entries and shims)");
            shell.AddCommand<ShellShimsCommand>("shims");
            shell.AddCommand<ShellInstallCommand>("install");
        });
    }

    internal static string Version()
        => typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    static readonly IAnsiConsole SharedConsole = CreateSharedConsole();

    static IAnsiConsole CreateSharedConsole()
    {
        // Colour a real terminal, but stay plain when stdout is redirected: scripts and captured output want
        // text, not escape codes.
        var redirected = Console.IsOutputRedirected;
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = redirected ? AnsiSupport.No : AnsiSupport.Yes,
            ColorSystem = redirected ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Out = new AnsiConsoleOutput(new ConsoleForwardingWriter()),
        });
        // The forwarding writer isn't a real terminal, so detection would assume 80 columns.
        console.Profile.Width = SafeWidth(redirected);
        return console;
    }

    static int SafeWidth(bool redirected)
    {
        if (redirected) return 200;
        try { return Console.WindowWidth > 0 ? Console.WindowWidth : 200; }
        catch { return 200; }
    }
}

/// <summary>Forwards every write to whatever <see cref="Console.Out"/> currently is, so the shared console
/// follows <see cref="Console.SetOut"/> (the seam tests swap per case) instead of capturing one writer.</summary>
sealed class ConsoleForwardingWriter : TextWriter
{
    public override System.Text.Encoding Encoding => Console.OutputEncoding;
    public override void Write(char value) => Console.Out.Write(value);
    public override void Write(string? value) => Console.Out.Write(value);
    public override void Flush() => Console.Out.Flush();
}
