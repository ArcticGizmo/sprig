using Sprig.Cli.Commands;
using Sprig.Core.Compose;
using Sprig.Core.Docker;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Pools;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Settings;
using Sprig.Core.Setup;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli;

/// <summary>The CLI's whole surface — a Spectre.Console <see cref="CommandApp"/> over Sprig.Core. A
/// supported, shipped front-end (not a dev-only harness); <c>--json</c> output is a stability contract,
/// so it is written straight to stdout, never through the markup renderer.</summary>
public static class CliApp
{
    public static int Run(string[] args)
    {
        // SPRIG_STORE points the central store somewhere other than %LOCALAPPDATA%\sprig — useful for
        // running against a throwaway store and the seam the CLI tests drive the dispatcher through.
        var root = Environment.GetEnvironmentVariable("SPRIG_STORE");
        return Run(args, new SprigPaths(string.IsNullOrWhiteSpace(root) ? null : root));
    }

    internal static int Run(string[] args, ISprigPaths paths)
    {
        // --json is a global flag; a failure before/inside a command still has to honour it. Detect it
        // up front so the outer catch can pick the machine payload over the human line.
        var json = args.Contains("--json");

        // Spectre caches the console it renders help/errors through, so we can't hand it a fresh
        // per-run instance — the in-process test suite would render into a disposed writer from an
        // earlier case. Instead one shared console whose output is late-bound to the current
        // Console.Out (see ConsoleForwardingWriter) serves every run; assigning the static each time is
        // cheap and idempotent.
        var ansi = SharedConsole;
        AnsiConsole.Console = ansi;

        var runner = new ProcessRunner();
        var git = new GitService(runner);
        var settings = new FileSettingsStore(paths);
        var ports = new FilePortStore(paths, settings);
        var instances = new InstanceStore(paths);
        var workspaces = new WorkspaceService(git, ports, instances, new EnvClobberService(),
            new ComposeGenerator(), new DockerService(runner), paths, new SetupRunner(runner));
        var reconciler = new WorkspaceReconciler(git, instances);
        var registry = new RepoRegistryStore(paths);
        var stacks = new StackStore(paths, registry, instances, settings);
        var resolver = new StackResolver(registry, stacks, git);
        var pools = new PoolService(stacks, instances, resolver, workspaces, paths);
        var maps = new Core.Maps.MapStore(paths, registry);
        var mapResolver = new Core.Maps.MapResolver(registry, maps, git, paths);

        var context = new CliContext(paths, workspaces, reconciler, registry, stacks, resolver,
            pools, maps, mapResolver, settings, git, ansi);

        // The registrar hands the one CliContext (and the console) to every command; commands are
        // constructed by reflection from there.
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
            // Machine callers get a parseable failure ({ ok: false, error }); humans get the same
            // message on stderr. Either way the exit code is 1 — the primary signal for scripts.
            if (json) CliOutput.Json(new { ok = false, error = ex.Message });
            else Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // Dispatch convention: the workspace is the primary object, so its verbs are top-level and
    // unqualified (create/ls/info/up/…). Every other object is namespaced (repo/stack/settings). A
    // `ws`/`workspace` branch mirrors the workspace verbs so the noun-verb form works too.
    static void Configure(IConfigurator config)
    {
        config.SetApplicationName("sprig");
        config.SetApplicationVersion(Version());
        // Let domain/parse errors bubble to Run's catch so the --json/stderr error contract and the
        // exit-code-1 promise live in exactly one place.
        config.PropagateExceptions();
        // Reject unknown options rather than silently ignore them — the hand-rolled parser did, and a
        // typo'd flag should fail loudly instead of quietly running a command it didn't mean.
        config.UseStrictParsing();

        config.AddCommand<CdCommand>("cd");
        config.AddCommand<PathCommand>("path");
        config.AddCommand<OpenCommand>("open");
        config.AddCommand<UpdateCommand>("update");
        config.AddCommand<InitCommand>("init");

        config.AddBranch("repo", repo =>
        {
            repo.SetDescription("Register repositories sprig builds workspaces from");
            repo.AddCommand<RepoAddCommand>("add");
            repo.AddCommand<RepoLsCommand>("ls");
            repo.AddCommand<RepoRmCommand>("rm");
        });

        config.AddBranch("stack", stack =>
        {
            stack.SetDescription("Define and manage stacks (named sets of repos wired together)");
            stack.AddCommand<StackCreateCommand>("create");
            stack.AddCommand<StackEditCommand>("edit");
            stack.AddCommand<StackLsCommand>("ls");
            stack.AddCommand<StackShowCommand>("show");
            stack.AddCommand<StackRmCommand>("rm");
            stack.AddCommand<StackExportCommand>("export");
            stack.AddCommand<StackImportCommand>("import");
        });

        config.AddBranch("pool", pool =>
        {
            pool.SetDescription("Check out and manage the pooled workspaces built from a stack");
            pool.AddCommand<PoolCheckoutCommand>("checkout");
            pool.AddCommand<PoolReleaseCommand>("release");
            pool.AddCommand<PoolStatusCommand>("status");
        });

        // EXPERIMENTAL (the Graph Turn) — self-describing repos wired through a map. Runs alongside stacks
        // during the transition; becomes the primary create path when stacks are retired (M7).
        config.AddBranch("map", map =>
        {
            map.SetDescription("[experimental] Check out workspaces from a map (self-describing repos)");
            map.AddCommand<MapLsCommand>("ls");
            map.AddCommand<MapShowCommand>("show");
            map.AddCommand<MapImportCommand>("import");
            map.AddCommand<MapCreateCommand>("create");
        });

        config.AddBranch("settings", settings =>
        {
            settings.SetDescription("Port allocation policy");
            settings.AddCommand<SettingsShowCommand>("show");
            settings.AddCommand<SettingsSetCommand>("set");
        }).WithAlias("config");

        // `ws`/`workspace <verb>` — the workspace is scoped under its own noun so the top level stays
        // uncluttered (repo/stack/settings + the meta commands). This is the only home for these verbs.
        config.AddBranch("ws", ws =>
        {
            ws.SetDescription("Create and manage workspaces (create, up, down, status, rm, ...)");
            ws.AddCommand<CreateCommand>("create");
            ws.AddCommand<LsCommand>("ls");
            ws.AddCommand<InfoCommand>("info");
            ws.AddCommand<UpCommand>("up");
            ws.AddCommand<DownCommand>("down");
            ws.AddCommand<RefreshCommand>("refresh");
            ws.AddCommand<RestartCommand>("restart");
            ws.AddCommand<ResetCommand>("reset");
            ws.AddCommand<StatusCommand>("status");
            ws.AddCommand<RmCommand>("rm").WithAlias("remove");
            ws.AddCommand<ReconcileCommand>("reconcile").WithAlias("doctor");
        }).WithAlias("workspace");
    }

    static string Version()
        => typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // One console for the whole process (see Run for why it can't be per-run). Its output is late-bound
    // to Console.Out, so it always writes wherever stdout currently points — real stdout in production,
    // the test's captured writer under xUnit. Detection runs against a non-tty writer, so it renders
    // plain (no ANSI/colour); the rounded box borders still come through.
    static readonly IAnsiConsole SharedConsole = CreateSharedConsole();

    static IAnsiConsole CreateSharedConsole()
    {
        // Colour a real terminal, but stay plain when stdout is redirected (a pipe, a file, the test
        // harness) — scripts and captured output want text, not escape codes. Decided per process, at
        // first use, which is the only invocation a shipped CLI ever sees.
        var redirected = Console.IsOutputRedirected;
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = redirected ? AnsiSupport.No : AnsiSupport.Yes,
            ColorSystem = redirected ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Out = new AnsiConsoleOutput(new ConsoleForwardingWriter()),
        });
        // The forwarding writer isn't a real terminal, so detection assumes 80 cols and wraps tables
        // narrowly. Use the real width when we have one, else a generous fallback.
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

/// <summary>A <see cref="TextWriter"/> that forwards every write to whatever <see cref="Console.Out"/>
/// currently is, resolved per call. Lets the single shared <see cref="IAnsiConsole"/> follow
/// <see cref="Console.SetOut"/> (the seam the tests swap per case) instead of capturing one writer.</summary>
sealed class ConsoleForwardingWriter : TextWriter
{
    public override System.Text.Encoding Encoding => Console.OutputEncoding;
    public override void Write(char value) => Console.Out.Write(value);
    public override void Write(string? value) => Console.Out.Write(value);
    public override void Flush() => Console.Out.Flush();
}
