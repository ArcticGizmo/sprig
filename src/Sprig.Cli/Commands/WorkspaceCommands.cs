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
    [Description("The workspace: its branch, slot number or folder name (default: the slot this folder is in, else local)")]
    public string Workspace { get; set; } = "";

    /// <summary>Whether words after the workspace name apps, so one that isn't an ID or a workspace is an app.</summary>
    protected virtual bool TakesApps => false;

    /// <summary>
    /// Fills in what was left out: the ID from the current folder, and the workspace from the worktree it's in, else
    /// <c>local</c>. Outside every repo, a first word that's the name of just one workspace means that workspace. Words
    /// that turn out not to be an ID or one of its workspaces are handed back, for commands whose next argument is an
    /// app.
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
        else if (Here.TryFind(cli) is { } here) (Orchestration, workspace) = here;
        else if (words.Count > 0 && Here.Named(cli, words[0]) is { } named)
        {
            (Orchestration, Workspace) = (named.Orchestration, named.Name);
            words.RemoveAt(0);
            return words;
        }
        else throw Here.NotHere();

        if (words.Count > 0 && cli.Workspaces.Find(Orchestration, words[0]) is { } found)
        {
            workspace = found.Name;
            words.RemoveAt(0);
        }
        else if (words.Count > 0 && !TakesApps)
        {
            workspace = words[0];
            words.RemoveAt(0);
        }
        Workspace = workspace ?? "local";
        return words;
    }
}

[Description("Bring a workspace up: a branch checked out in a slot of worktrees (or the repo where it is), ports, env files and infra, then its apps in the background")]
public sealed class UpCommand(CliContext cli) : Command<UpCommand.Settings>
{
    public sealed class Settings : TrustingSettings
    {
        [CommandArgument(0, "[id]")]
        [Description("A repo ID from the Repos list, an orchestration ID, or an orchestration file (default: the repo this folder is)")]
        public string? Orchestration { get; set; }

        [CommandOption("-b|--branch <branch>")]
        [Description("Check this branch out in a slot of worktrees: the slot it's in already, else the lowest free one, else a new one (default: the slot this folder is in, else the repos where they're checked out, as 'local')")]
        public string? Branch { get; set; }

        [CommandOption("--slot <number>")]
        [Description("The slot to check the branch out in, e.g. 2 for storefront--2")]
        public int? Slot { get; set; }

        [CommandOption("--from <branch>")]
        [Description("Where a branch that doesn't exist yet starts (default: each repo's own default branch)")]
        public string? From { get; set; }

        [CommandOption("--empty-data")]
        [Description("Reusing a free slot, start its infra with empty data rather than the data the last branch left")]
        public bool EmptyData { get; set; }

        [CommandOption("--unset <name>")]
        [Description("Reusing a free slot, clear a value it kept so the default applies again. Repeat for more")]
        public string[] Unset { get; set; } = [];

        [CommandOption("--run")]
        [Description("Follow the apps' logs together here, rather than leaving them in the background. Ctrl+C stops the apps; the infra keeps running")]
        public bool Run { get; set; }

        [CommandOption("--no-apps")]
        [Description("Stop once the worktrees, files and infra are ready, without starting the apps")]
        public bool NoApps { get; set; }

        [CommandOption("--set <name=value>")]
        [Description("Give a setting this value in the workspace, over the orchestration's and the repo's default. Kept for next time. Repeat for more")]
        public string[] Set { get; set; } = [];
    }

    protected override ValidationResult Validate(CommandContext context, Settings s)
    {
        if (SettingValues.Parse(s.Set, out _) is { } error) return ValidationResult.Error(error);
        if (s.Run && s.NoApps) return ValidationResult.Error("--run follows the apps and --no-apps doesn't start them; choose one");
        return ValidationResult.Success();
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (s.Json && s.Run)
            throw new InvalidOperationException("--run follows the apps' logs, which isn't JSON; leave it off with --json and the apps run in the background");
        if (s.Branch is null && (s.From is not null || s.EmptyData || s.Unset.Length > 0))
            throw new InvalidOperationException("--from, --empty-data and --unset are for checking a branch out; name it with -b <branch>");
        var (id, slotHere) = s.Orchestration is { } given ? (given, null) : Here.Find(cli, s.Branch);
        // Without a branch: the slot named, or the one this folder is in, else the repos in place as "local".
        var workspace = s.Slot is { } slot ? slot.ToString(System.Globalization.CultureInfo.InvariantCulture) : slotHere ?? "local";
        if (OrchestrationLoad.Load(cli, id, s, s.Branch is null ? workspace : null) is not { } loaded) return 1;
        var root = Path.GetDirectoryName(loaded.OrchestrationPath)!;

        var progress = new SyncProgress<WorkspaceEvent>(e => Report(e, s.Json));
        try
        {
            var record = UpApproving(loaded, workspace, s, progress, cancellation);
            if (record is null) return 1;
            var plan = cli.Workspaces.LastPlan(record.Orchestration, record.Name)!;
            if (s.NoApps)
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
            return s.Run ? new AppFollow(cli, record, specs).Run() : StartDetached(record, plan, specs, s.Json, cancellation);
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
    WorkspaceRecord? UpApproving(LoadedOrchestration loaded, string workspace, Settings s,
        IProgress<WorkspaceEvent> progress, CancellationToken cancellation)
    {
        var runApps = !s.NoApps;
        SettingValues.Parse(s.Set, out var given);
        var values = new Dictionary<string, string>(given, StringComparer.Ordinal);
        // A new workspace with no branch is the repos where they're checked out.
        var options = s.Branch is null && workspace == "local" && cli.Workspaces.Get(loaded.Id, workspace) is null ? new WorkspaceOptions(InPlace: true) : null;
        IReadOnlyCollection<string>? unset = s.Unset;
        for (var attempt = 1; ; attempt++)
        {
            string? held = null;
            try
            {
                Action<WorkspaceRecord, Plan>? beforeInfra = runApps ? (record, plan) => held = Prebuild(record, plan) : null;
                var record = s.Branch is { } branch
                    ? cli.Workspaces.CheckOut(loaded, branch, new CheckOutOptions(s.Slot, s.From, s.EmptyData), progress, cancellation, beforeInfra, values, unset)
                    : cli.Workspaces.Up(loaded, workspace, null, options, progress, cancellation, beforeInfra, values);
                return record;
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
            var names = Markup.Escape(Words(record));
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
        cli.Ansi.MarkupLine(record.InPlace
            ? $"[bold]{Markup.Escape(record.Orchestration)}[/] / [bold]{Markup.Escape(record.Name)}[/] is up  [dim](repos in place)[/]"
            : $"[bold]{Markup.Escape(record.Branch ?? "")}[/] is up in [bold]{Markup.Escape(record.SlotName)}[/]  [dim]({Markup.Escape((record.Folder ?? "").Replace('\\', '/'))})[/]");

        var apps = new Table().Border(TableBorder.Rounded).AddColumns("App", "Folder", "Url", "Values in");
        foreach (var app in plan.Apps)
        {
            var repo = record.Repos.Single(r => r.RepoPath == app.RepoPath);
            var dir = Path.GetFullPath(Path.Combine(repo.Dir, app.Path));
            var url = plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name) is { Port: { } port } ? $"http://localhost:{port}" : "";
            var files = app.ValueFiles();
            apps.AddRow(Markup.Escape($"{repo.Name}/{app.Name}"), Markup.Escape(dir.Replace('\\', '/')), Markup.Escape(url),
                files.Count > 0 ? Markup.Escape(string.Join(", ", files)) : "[dim]none[/]");
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
        var names = Markup.Escape(Words(record));
        if (!appsStart)
            cli.Ansi.MarkupLine($"[dim]Start the apps: sprig start {names} (or from their folders: dotnet run, npm run dev). " +
                                $"Stop the infra with: sprig down {names}[/]");
    }

    /// <summary>How to name the workspace on a command line: its ID, then its branch (or its name, in place).</summary>
    internal static string Words(WorkspaceRecord record) => $"{record.Orchestration} {record.Branch ?? record.Name}";

    /// <summary>The <c>sprig up</c> that brings this workspace up again.</summary>
    internal static string UpLine(WorkspaceRecord record)
        => record.InPlace ? $"sprig up {record.Orchestration}" : $"sprig up {record.Orchestration} -b {record.Branch ?? "<branch>"}";
}

[Description("Free a slot for another branch: stop its apps and infra and detach its worktrees, keeping its branch, data, ports and values")]
public sealed class ReleaseCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        s.Resolve(cli);
        var before = cli.Workspaces.Get(s.Orchestration, s.Workspace) ?? throw new InvalidOperationException($"{s.Orchestration} has no workspace '{s.Workspace}'");
        var record = cli.Workspaces.Release(s.Orchestration, s.Workspace, new SyncProgress<WorkspaceEvent>(e => DownCommand.Echo(cli, e, s.Json)), cancellation);
        if (s.Json) { CliOutput.Json(new { ok = true, workspace = record }); return 0; }
        CliOutput.Success(before.IsFree ? $"{record.SlotName} was already free" : $"{record.SlotName} is free; '{before.Branch}' is kept, with its commits");
        cli.Ansi.MarkupLine($"[dim]Check the next branch out in it with: sprig up {Markup.Escape(record.Orchestration)} -b <branch>[/]");
        return 0;
    }
}

[Description("Stop a workspace's infra, keeping its data, worktrees and ports")]
public sealed class DownCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        s.Resolve(cli);
        var record = cli.Workspaces.Down(s.Orchestration, s.Workspace, new SyncProgress<WorkspaceEvent>(e => Echo(cli, e, s.Json)), cancellation);
        return CliOutput.Ok(s.Json, record.IsFree ? $"{record.SlotName} is free, so nothing was running" : $"{record.SlotName} ({record.Label}) is down",
            new { ok = true, workspace = record });
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
                : $"its containers and their data, and its worktrees{(s.DeleteBranch && record.Branch is { } b ? $" and the branch '{b}'" : "")}";
            if (!cli.Ansi.Prompt(new ConfirmationPrompt($"Remove {Markup.Escape(record.SlotName)}: {Markup.Escape(what)}?") { DefaultValue = false }))
                return 1;
        }
        cli.Workspaces.Remove(s.Orchestration, s.Workspace, new RemoveOptions(s.DeleteBranch, s.Force),
            new SyncProgress<WorkspaceEvent>(e => DownCommand.Echo(cli, e, s.Json)), cancellation);
        return CliOutput.Ok(s.Json, $"removed {record.SlotName}", new { ok = true, orchestration = s.Orchestration, workspace = s.Workspace });
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
            cli.Ansi.MarkupLine("[dim]No workspaces. In a repo's folder, check a branch out in a slot of its own with: sprig up -b <branch> (or run it in place with: sprig up)[/]");
            return 0;
        }
        var table = new Table().Border(TableBorder.Rounded).AddColumns("ID", "Slot", "Branch", "State", "Folder", "Updated");
        foreach (var r in workspaces)
        {
            var state = r.State switch
            {
                WorkspaceState.Up => "[green]up[/]",
                WorkspaceState.Failed => $"[red]failed[/] [dim]{Markup.Escape(r.Error ?? "")}[/]",
                WorkspaceState.Starting => "[yellow]starting[/]",
                WorkspaceState.Released => "[dim]free[/]",
                _ => "[dim]down[/]",
            };
            table.AddRow(Markup.Escape(r.Orchestration), Markup.Escape(r.SlotName),
                r.InPlace ? "[dim](in place)[/]" : r.Branch is { } b ? Markup.Escape(b) : "[dim](detached)[/]", state,
                Markup.Escape((r.Folder ?? string.Join(", ", r.Repos.Select(x => x.Dir))).Replace('\\', '/')),
                r.UpdatedAt.LocalDateTime.ToString("g"));
        }
        cli.Ansi.Write(table);
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
