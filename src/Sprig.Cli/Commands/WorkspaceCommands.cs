using System.ComponentModel;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;
using Sprig.Engine.Orchestrations;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

/// <summary>Settings for commands that name one workspace of a repo or an orchestration.</summary>
public class WorkspaceSettings : GlobalSettings
{
    [CommandArgument(0, "[id]")]
    [Description("The repo or orchestration ID (default: the repo this folder is, or the workspace whose worktree it's in)")]
    public string Orchestration { get; set; } = "";

    [CommandArgument(1, "[workspace]")]
    [Description("The workspace's name (default: local)")]
    public string Workspace { get; set; } = "";

    /// <summary>Whether words after the workspace name apps, so one that isn't an ID or a workspace is an app.</summary>
    protected virtual bool TakesApps => false;

    /// <summary>
    /// Fills in what was left out: the ID from the current folder, and the workspace from the worktree it's in, else
    /// <c>local</c>. Words that turn out not to be an ID or one of its workspaces are handed back, for commands whose
    /// next argument is an app.
    /// </summary>
    public IReadOnlyList<string> Resolve(CliContext cli)
    {
        var words = new[] { Orchestration, Workspace }.Where(w => !string.IsNullOrEmpty(w)).ToList();
        string? workspace = null;
        if (words.Count > 0 && cli.Orchestrations.Resolve(words[0]) is not null && !File.Exists(words[0]))
        {
            Orchestration = words[0];
            words.RemoveAt(0);
        }
        else (Orchestration, workspace) = Here.Find(cli);

        if (words.Count > 0 && (!TakesApps || cli.Workspaces.Get(Orchestration, words[0]) is not null))
        {
            workspace = words[0];
            words.RemoveAt(0);
        }
        Workspace = workspace ?? "local";
        return words;
    }
}

[Description("Bring a workspace up: worktrees (or the repo where it is), ports, env files and infra, in one step")]
public sealed class UpCommand(CliContext cli) : Command<UpCommand.Settings>
{
    public sealed class Settings : TrustingSettings
    {
        [CommandArgument(0, "[id]")]
        [Description("A repo ID from the Repos list, an orchestration ID, or an orchestration file (default: the repo this folder is)")]
        public string? Orchestration { get; set; }

        [CommandOption("-w|--workspace <name>")]
        [Description("Name a workspace to give it worktrees of its own (default: 'local', which runs the repos where they're checked out)")]
        public string? Workspace { get; set; }

        [CommandOption("--branch <branch>")]
        [Description("The branch for the workspace's worktrees (default: the workspace's name). Made from --from when it doesn't exist")]
        public string? Branch { get; set; }

        [CommandOption("--in-place")]
        [Description("A new named workspace runs the repos where they're checked out, rather than in worktrees of its own")]
        public bool InPlace { get; set; }

        [CommandOption("--from <branch>")]
        [Description("Where a new workspace's branches start (default: each repo's own default branch)")]
        public string? From { get; set; }

        [CommandOption("--random-ports")]
        [Description("A new workspace takes fresh ports each time it comes up, rather than keeping the ones it was given")]
        public bool RandomPorts { get; set; }

        /// <summary>Only what was asked for: an existing workspace keeps its own choices unless one is named.</summary>
        public WorkspaceOptions? Options => InPlace || RandomPorts || From is not null ? new WorkspaceOptions(InPlace, From, RandomPorts) : null;

        [CommandOption("--run")]
        [Description("Then start every app and follow their logs together. Ctrl+C stops the apps; the infra keeps running")]
        public bool Run { get; set; }

        [CommandOption("-d|--detached")]
        [Description("Then start every app, wait until they're ready, and leave them running in the background")]
        public bool Detached { get; set; }

        [CommandOption("--set <name=value>")]
        [Description("Give a setting this value in the workspace, over the orchestration's and the repo's default. Kept for next time. Repeat for more")]
        public string[] Set { get; set; } = [];
    }

    protected override ValidationResult Validate(CommandContext context, Settings s)
        => SettingValues.Parse(s.Set, out _) is { } error ? ValidationResult.Error(error) : ValidationResult.Success();

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (s.Json && s.Run && !s.Detached)
            throw new InvalidOperationException("--run follows the apps' logs, which isn't JSON; use --detached with --json");
        if (s.InPlace && (s.Branch is not null || s.From is not null))
            throw new InvalidOperationException("--in-place runs the repos where they're checked out, so it takes no --branch or --from");
        var named = s.Workspace ?? (s.Branch is { } b ? WorkspaceService.NameFromBranch(b) : null);
        if (named is null && s.From is not null)
            throw new InvalidOperationException("--from starts a workspace's own branches; name the workspace with -w <name>");
        var (id, worktreeOf) = s.Orchestration is { } given ? (given, null) : Here.Find(cli);
        // Run from inside a workspace's worktree, it's that workspace; otherwise the repo in place, as "local".
        var workspace = named ?? worktreeOf ?? "local";
        if (OrchestrationLoad.Load(cli, id, s, workspace) is not { } loaded) return 1;
        var root = Path.GetDirectoryName(loaded.OrchestrationPath)!;

        var options = s.Options;
        if (named is null && worktreeOf is null && cli.Workspaces.Get(loaded.Id, workspace) is null)
            options = (options ?? new WorkspaceOptions()) with { InPlace = true };

        var progress = new SyncProgress<WorkspaceEvent>(e => Report(e, s.Json));
        try
        {
            var record = UpApproving(loaded, workspace, options, s, progress, cancellation);
            if (record is null) return 1;
            var plan = cli.Workspaces.LastPlan(record.Orchestration, record.Name)!;
            var runApps = s.Run || s.Detached;
            if (!runApps)
            {
                if (s.Json) CliOutput.Json(new { ok = true, workspace = record, plan });
                else Summary(record, plan, root, appsStart: false);
                return 0;
            }

            if (!s.Json) Summary(record, plan, root, appsStart: true);
            var specs = RunnerSpecs.For(record, plan);
            if (specs.Count == 0)
            {
                if (s.Json) CliOutput.Json(new { ok = true, workspace = record, plan, runners = Array.Empty<RunnerStatus>() });
                else cli.Ansi.MarkupLine("[dim]The orchestration has no apps to run.[/]");
                return 0;
            }
            return s.Detached ? StartDetached(record, plan, specs, s.Json, cancellation) : new AppFollow(cli, record, specs).Run();
        }
        catch (WorkspaceException e)
        {
            if (s.Json) CliOutput.Json(new { ok = false, error = e.Message, problems = e.Problems, asks = e.Asks });
            else
            {
                Console.Error.WriteLine($"error: {e.Message}");
                if (e.Problems.Count > 0) PlanPrinter.Problems(cli.Ansi, e.Problems, root);
                if (e.Asks.Count > 0)
                    cli.Ansi.MarkupLine($"[dim]Give {(e.Asks.Count == 1 ? "it" : "them")} a value with {Markup.Escape(string.Join(" ", e.Asks.Select(a => $"--set {a.Name}=...")))}[/]");
            }
            return 1;
        }
    }

    /// <summary>Up, asking to approve the definitions a new workspace's worktrees bring (its branch can change them), and
    /// for values of settings nothing gives one, and going again. Null when they weren't approved.</summary>
    WorkspaceRecord? UpApproving(LoadedOrchestration loaded, string workspace, WorkspaceOptions? options, Settings s,
        IProgress<WorkspaceEvent> progress, CancellationToken cancellation)
    {
        var runApps = s.Run || s.Detached;
        SettingValues.Parse(s.Set, out var given);
        var values = new Dictionary<string, string>(given, StringComparer.Ordinal);
        for (var attempt = 1; ; attempt++)
        {
            string? held = null;
            try
            {
                return cli.Workspaces.Up(loaded, workspace, s.Branch, options, progress, cancellation,
                    runApps ? (record, plan) => held = Prebuild(record, plan) : null, values);
            }
            catch (WorkspaceException e) when (e.Untrusted.Count > 0 && attempt < 3)
            {
                if (!TrustFlow.Approve(cli, e.Untrusted, s)) return null;
            }
            catch (WorkspaceException e) when (e.Asks.Count > 0 && attempt < 3 && cli.Interactive(s.Json))
            {
                cli.Ansi.MarkupLine($"[yellow]{(e.Asks.Count == 1 ? "A setting has" : "Some settings have")} no value yet.[/] It's kept on the workspace.");
                foreach (var ask in e.Asks.Where(a => !values.ContainsKey(a.Name)))
                {
                    var prompt = new TextPrompt<string>($"  [bold]{Markup.Escape(ask.Name)}[/] [dim]({Markup.Escape(ask.Type)}, for {Markup.Escape(ask.Owner)})[/]:");
                    if (ask.Type == "secret") prompt = prompt.Secret();
                    values[ask.Name] = cli.Ansi.Prompt(prompt);
                }
            }
            catch (Exception) when (held is not null)
            {
                cli.Host.TrySend(new HostRequest("stop", Workspace: held));
                throw;
            }
        }
    }

    /// <summary>
    /// Has the host build the apps while the infra comes up, held until starting them after <c>up</c> releases them.
    /// Returns the workspace's key when it did. Only a head start: if it fails, starting them afterwards builds as usual.
    /// </summary>
    string? Prebuild(WorkspaceRecord record, Plan plan)
    {
        var specs = RunnerSpecs.For(record, plan);
        if (specs.Count == 0) return null;
        try
        {
            return cli.Host.Send(new HostRequest("start", specs, Restart: [], Hold: true)).Ok ? record.Key : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    int StartDetached(WorkspaceRecord record, Plan plan, IReadOnlyList<RunnerSpec> specs, bool json, CancellationToken cancellation)
    {
        cli.Ansi.WriteLine();
        StartCommand.Send(cli, specs, []);
        var runners = StartCommand.WaitUntilReady(cli, record.Key, specs, json, cancellation);
        var ok = runners.All(r => r.State.Satisfies());
        if (json) CliOutput.Json(new { ok, workspace = record, plan, runners });
        else
        {
            var names = $"{Markup.Escape(record.Orchestration)} {Markup.Escape(record.Name)}";
            cli.Ansi.MarkupLine($"[dim]The apps keep running in the background. Watch them: sprig ps; read one's log: sprig logs {names} <app>; " +
                                $"stop them: sprig stop {names}[/]");
        }
        return ok ? 0 : 1;
    }

    string? _lastOutput;

    void Report(WorkspaceEvent e, bool json)
    {
        if (json) return;
        // Compose prints some progress lines twice.
        if (e.State == StepState.Output && e.Detail == _lastOutput) return;
        _lastOutput = e.State == StepState.Output ? e.Detail : null;
        switch (e.State)
        {
            case StepState.Running:
                cli.Ansi.MarkupLine($"[blue]…[/] {Markup.Escape(e.Step)}");
                break;
            case StepState.Done:
                cli.Ansi.MarkupLine($"[green]{Glyph.Check(cli.Ansi)}[/] {Markup.Escape(e.Step)}{(e.Detail is { } d ? $"  [dim]{Markup.Escape(d)}[/]" : "")}");
                break;
            case StepState.Failed:
                cli.Ansi.MarkupLine($"[red]x[/] {Markup.Escape(e.Step)}");
                break;
            case StepState.Output when !string.IsNullOrWhiteSpace(e.Detail):
                cli.Ansi.MarkupLine($"    [dim]{Markup.Escape(e.Detail!.Trim())}[/]");
                break;
        }
    }

    /// <param name="appsStart">Sprig starts the apps next, so there's no need to say how to start them by hand.</param>
    void Summary(WorkspaceRecord record, Plan plan, string root, bool appsStart)
    {
        cli.Ansi.WriteLine();
        var where = record.InPlace ? "repos in place" : $"branch {record.Branch}";
        cli.Ansi.MarkupLine($"[bold]{Markup.Escape(record.Orchestration)}[/] / [bold]{Markup.Escape(record.Name)}[/] is up  [dim]({Markup.Escape(where)}, {Markup.Escape(record.PortPolicy)} ports)[/]");

        var apps = new Table().Border(TableBorder.Rounded).AddColumns("App", "Folder", "Url", "Values in");
        foreach (var app in plan.Apps)
        {
            var repo = record.Repos.Single(r => r.RepoPath == app.RepoPath);
            var dir = Path.GetFullPath(Path.Combine(repo.Dir, app.Path));
            var url = plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name) is { Port: { } port } ? $"http://localhost:{port}" : "";
            var files = record.Files.Where(f => f.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(dir, f.Path).Replace('\\', '/')).ToArray();
            apps.AddRow(Markup.Escape($"{repo.Name}/{app.Name}"), Markup.Escape(dir.Replace('\\', '/')), Markup.Escape(url),
                files.Length > 0 ? Markup.Escape(string.Join(", ", files)) : $"[dim]none: sprig env {Markup.Escape(record.Orchestration)} {Markup.Escape(record.Name)} {Markup.Escape(app.Name)}[/]");
        }
        cli.Ansi.Write(apps);

        if (plan.Containers.Count + plan.Compose.Count > 0)
        {
            var infra = new Table().Border(TableBorder.Rounded).AddColumns("Infra", "Ports");
            foreach (var c in plan.Containers) infra.AddRow(Markup.Escape($"{c.Owner}/{c.Name}"), Markup.Escape(string.Join(", ", c.Ports)));
            foreach (var c in plan.Compose)
                foreach (var svc in c.Services)
                    infra.AddRow(Markup.Escape($"{c.Owner}/{svc.Name}"), Markup.Escape(string.Join(", ", svc.Mappings)));
            cli.Ansi.Write(infra);
        }

        var warnings = plan.Problems.Where(p => p.Severity == ProblemSeverity.Warning).ToArray();
        if (warnings.Length > 0) PlanPrinter.Problems(cli.Ansi, warnings, root);
        var names = $"{Markup.Escape(record.Orchestration)} {Markup.Escape(record.Name)}";
        if (!appsStart)
            cli.Ansi.MarkupLine($"[dim]Start the apps: sprig start {names} (or from their folders: dotnet run, npm run dev). " +
                                $"Stop the infra with: sprig down {names}[/]");
    }
}

[Description("Stop a workspace's infra, keeping its data, worktrees and ports")]
public sealed class DownCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        s.Resolve(cli);
        var record = cli.Workspaces.Down(s.Orchestration, s.Workspace, new SyncProgress<WorkspaceEvent>(e => Echo(cli, e, s.Json)), cancellation);
        return CliOutput.Ok(s.Json, $"{record.Orchestration}/{record.Name} is down", new { ok = true, workspace = record });
    }

    internal static void Echo(CliContext cli, WorkspaceEvent e, bool json)
    {
        if (json) return;
        if (e.State == StepState.Done) cli.Ansi.MarkupLine($"[green]{Glyph.Check(cli.Ansi)}[/] {Markup.Escape(e.Step)}{(e.Detail is { } d ? $"  [dim]{Markup.Escape(d)}[/]" : "")}");
        else if (e.State == StepState.Failed) cli.Ansi.MarkupLine($"[red]x[/] {Markup.Escape(e.Step)}");
    }
}

[Description("Remove a workspace: its containers and their data, its worktrees, and the values it wrote")]
public sealed class RmCommand(CliContext cli) : Command<RmCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--delete-branch")]
        [Description("Also delete the workspace's branch in each repo")]
        public bool DeleteBranch { get; set; }

        [CommandOption("--force")]
        [Description("Remove worktrees with uncommitted changes, and delete branches with unpushed commits")]
        public bool Force { get; set; }

        [CommandOption("-y|--yes")]
        [Description("Don't ask first")]
        public bool Yes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        s.Resolve(cli);
        var record = cli.Workspaces.Get(s.Orchestration, s.Workspace) ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace named '{s.Workspace}'");
        if (!s.Yes)
        {
            if (!cli.Interactive(s.Json)) throw new InvalidOperationException("removing a workspace deletes its databases; pass --yes to confirm");
            var what = record.InPlace ? "its containers and their data, and the values it wrote into the repos"
                : $"its containers and their data, and its worktrees{(s.DeleteBranch ? $" and the branch '{record.Branch}'" : "")}";
            if (!cli.Ansi.Prompt(new ConfirmationPrompt($"Remove {Markup.Escape(record.Orchestration)}/{Markup.Escape(record.Name)}: {Markup.Escape(what)}?") { DefaultValue = false }))
                return 1;
        }
        cli.Workspaces.Remove(s.Orchestration, s.Workspace, new RemoveOptions(s.DeleteBranch, s.Force),
            new SyncProgress<WorkspaceEvent>(e => DownCommand.Echo(cli, e, s.Json)), cancellation);
        return CliOutput.Ok(s.Json, $"removed {s.Orchestration}/{s.Workspace}", new { ok = true, orchestration = s.Orchestration, workspace = s.Workspace });
    }
}

[Description("List workspaces")]
public sealed class WorkspacesCommand(CliContext cli) : Command<WorkspacesCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[id]")]
        [Description("Only the workspaces of this orchestration or repo ID")]
        public string? Orchestration { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var workspaces = cli.Workspaces.List().Where(r => s.Orchestration is null || r.Orchestration.Equals(s.Orchestration, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (s.Json) { CliOutput.Json(workspaces); return 0; }
        if (workspaces.Length == 0)
        {
            cli.Ansi.MarkupLine("[dim]No workspaces. In a repo's folder, run it in place with: sprig up (or sprig up -w <name> for worktrees of its own)[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("ID", "Workspace", "Branch", "State", "Folder", "Updated");
        foreach (var r in workspaces)
        {
            var state = r.State switch
            {
                WorkspaceState.Up => "[green]up[/]",
                WorkspaceState.Failed => $"[red]failed[/] [dim]{Markup.Escape(r.Error ?? "")}[/]",
                WorkspaceState.Starting => "[yellow]starting[/]",
                _ => "[dim]down[/]",
            };
            table.AddRow(Markup.Escape(r.Orchestration), Markup.Escape(r.Name), Markup.Escape(r.Branch ?? "(in place)"), state,
                Markup.Escape((r.Folder ?? string.Join(", ", r.Repos.Select(x => x.Dir))).Replace('\\', '/')),
                r.UpdatedAt.LocalDateTime.ToString("g"));
        }
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Print the env vars an app of a workspace gets, for an IDE launch profile or a shell")]
public sealed class EnvCommand(CliContext cli) : Command<EnvCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandArgument(2, "[app]")]
        [Description("The app, as <name> or <repo>/<name>")]
        public string App { get; set; } = "";

        protected override bool TakesApps => true;
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        s.App = s.Resolve(cli).FirstOrDefault() ?? s.App;
        if (s.App.Length == 0) throw new InvalidOperationException("name the app: sprig env [id] [workspace] <app>");
        var plan = cli.Workspaces.LastPlan(s.Orchestration, s.Workspace)
            ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace named '{s.Workspace}' that has come up");
        var matches = plan.Apps.Where(a => a.Name == s.App || $"{a.Owner}/{a.Name}" == s.App).ToArray();
        var app = matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException($"no app '{s.App}' in {s.Orchestration}; it has {string.Join(", ", plan.Apps.Select(a => $"{a.Owner}/{a.Name}"))}"),
            _ => throw new InvalidOperationException($"'{s.App}' is in more than one repo; name it as <repo>/{s.App}"),
        };
        var env = app.Env.Where(e => e.File is null).Select(e => (e.Key, Value: e.Value.Value ?? "")).ToArray();
        if (s.Json) CliOutput.Json(env.ToDictionary(e => e.Key, e => e.Value));
        else foreach (var (key, value) in env) Console.WriteLine($"{key}={value}");
        return 0;
    }
}

/// <summary>Calls back on the reporting thread, so progress prints in order. A tool's stdout and stderr arrive on
/// different threads, hence the lock.</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    readonly Lock _gate = new();

    public void Report(T value)
    {
        lock (_gate) report(value);
    }
}
