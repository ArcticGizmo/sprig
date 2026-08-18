using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Core.Docker;
using Sprig.Core.Init;
using Sprig.Core.Maps;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// The workspace verbs (create/ls/info/up/down/reset/status/rm/reconcile) and the meta commands
// (open/update/init). The workspace verbs live under the `ws` branch (wired in CliApp); each command
// takes the shared CliContext and preserves the exact --json contract the hand-rolled dispatcher shipped.

[Description("Create an isolated workspace from a map (a slice of its repos) or a single repo")]
public sealed class CreateCommand(CliContext cli) : Command<CreateCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[name]")]
        [Description("Workspace name (omit at a terminal to pick everything interactively)")]
        public string? Name { get; set; }

        [CommandOption("-i|--interactive")]
        [Description("Force the interactive picker (map, repos, name)")]
        public bool Interactive { get; set; }

        [CommandOption("--no-interactive|--ni")]
        [Description("Never prompt — fail instead of asking (implied by --json, a pipe, or CI)")]
        public bool NoInteractive { get; set; }

        [CommandOption("--map <name>")]
        [Description("Create from a named map")]
        public string? Map { get; set; }

        [CommandOption("--repo <path>")]
        [Description("Create from a single repo path")]
        public string? Repo { get; set; }

        [CommandOption("--only <repos>")]
        [Description("Partial: only these map repos (comma-separated or repeated)")]
        public string[] Only { get; set; } = [];

        [CommandOption("--without <repos>")]
        [Description("Partial: every map repo except these")]
        public string[] Without { get; set; } = [];

        [CommandOption("--from <ref>")]
        [Description("Start the parked worktrees from this ref (defaults to each repo's base)")]
        public string? From { get; set; }

        [CommandOption("--skip-infra")]
        [Description("Create only — don't start the workspace's docker infra (it starts by default)")]
        public bool SkipInfra { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        // sprig now favours pooled workspaces (bounded, reusable). `ws create` still works — the nudge
        // goes to stderr so it never touches the stdout/--json contract scripts rely on.
        if (!s.Json)
            Console.Error.WriteLine(
                "note: sprig now favours pooled workspaces — see 'sprig pool checkout'. 'ws create' still works for now.");

        // A bare `create` at a terminal opens the wizard; a named/`--stack`/`--repo` create, or any
        // non-terminal run, goes straight through non-interactively. -i/--ni force the choice.
        var interactive = Interactivity.Resolve(s.Interactive, s.NoInteractive, s.Json,
            hasPrimaryInput: s.Name is not null || s.Map is not null || s.Repo is not null);

        var console = Term.Create();

        // Gather the target either by asking or from the flags/positional.
        MapDefinition? map;
        IReadOnlyList<ResolvedRepo> repos;
        string name;
        string? fromLabel;
        if (interactive)
        {
            if (Prompt(console, s) is not { } picked)
            {
                console.MarkupLine("[yellow]cancelled[/]");
                return 0;
            }
            (map, repos, name) = picked;
            fromLabel = map?.Name;
        }
        else
        {
            var only = CliFormat.SplitList(s.Only);
            var without = CliFormat.SplitList(s.Without);
            if ((only.Count > 0 || without.Count > 0) && s.Map is null)
                throw new ArgumentException("--only/--without narrow a map — they need --map <name>");
            name = s.Name ?? throw new ArgumentException("create requires a workspace name (or run it at a terminal to be prompted)");
            if (s.Map is not null)
                (map, repos) = cli.MapResolver.Resolve(s.Map, MapWithout(s.Map, only, without));
            else if (s.Repo is not null)
                (map, repos) = (null, cli.Workspaces.ResolveSingleRepo(s.Repo));
            else
                throw new ArgumentException("create requires --map <name> or --repo <path> (or run it at a terminal to be prompted)");
            fromLabel = s.Map;
        }

        // Plan up front so pre-flight problems (bad name, duplicate) surface before any output, and so
        // the checklist can list every step, pending, before work starts.
        var plan = cli.Workspaces.PlanCreateFromMap(repos, name);

        // The machine path stays quiet and structured: no checklist, just the record as JSON.
        if (s.Json) { CliOutput.Json(cli.Workspaces.CreateFromMap(name, map, repos, startPoint: s.From)); return 0; }

        console.MarkupLine($"[bold]Creating workspace[/] [green]{Markup.Escape(name)}[/]" +
            (fromLabel is { } m ? $" [dim]from map {Markup.Escape(m)}[/]" : ""));

        InstanceRecord record = null!;
        Checklist.Run(console, plan, progress =>
            record = cli.Workspaces.CreateFromMap(name, map, repos, progress: progress, startPoint: s.From));

        RenderSummary(console, record);

        // Infra starts by default (both interactive and non-interactive); --skip-infra leaves it created-only.
        if (!s.SkipInfra)
            StartInfra(console, record);
        return 0;
    }

    // Bring the freshly-created workspace's infra up. A stack with no compose files is a silent no-op; a
    // start failure (e.g. Docker not running) is a soft warning — the workspace itself was created, so we
    // keep it and point at `sprig ws up` rather than failing the whole create. Mirrors the GUI create flow.
    void StartInfra(IAnsiConsole console, InstanceRecord record)
    {
        if (!record.Repos.Any(r => r.ComposePaths.Count > 0))
            return;

        console.MarkupLine("[bold]Starting infrastructure[/]…");
        try
        {
            cli.Workspaces.Up(record.Workspace);
            console.MarkupLine($"[green]{Glyph.Check(console)}[/] infra up");
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[yellow]note:[/] infra didn't start — {Markup.Escape(ex.Message)}");
            console.MarkupLine($"  start it later with [bold]sprig ws up {Markup.Escape(record.Workspace)}[/]");
        }
    }

    // The `-i` flow: pick a map, choose its repos, then name the workspace. The repo selection becomes a
    // `--without` list handed to the resolver, which wires the slice through the repos' own provides/needs.
    //
    // Returns null when the whole thing is cancelled (ESC at the very first step). The map and repos steps are
    // ESC-navigable: ESC steps back to the previous one (re-showing your earlier choice). The name comes last
    // because TextPrompt can't be cancelled.
    (MapDefinition? map, IReadOnlyList<ResolvedRepo> repos, string name)? Prompt(IAnsiConsole console, Settings s)
    {
        var allMaps = cli.Maps.List();
        if (allMaps.Count == 0)
            throw new ArgumentException("no maps defined — author one in the app, or import it with 'sprig map import'");
        if (s.Map is { } preset && cli.Maps.Get(preset) is null)
            throw new ArgumentException($"unknown map '{preset}'");

        // The map step is skipped (and not a place ESC can land) when there's no real choice: a preset map,
        // or a single defined map.
        var mapFixed = s.Map is not null || allMaps.Count == 1;
        var mapName = s.Map ?? allMaps[0].Name;
        List<string>? chosenRepos = null;

        const int mapStep = 0, reposStep = 1, done = 2;
        var step = mapFixed ? reposStep : mapStep;

        while (step != done)
        {
            switch (step)
            {
                case mapStep:
                    if (Term.SelectOne(console, "Select a [green]map[/]: [grey](esc cancels)[/]",
                            allMaps.Select(x => x.Name)) is not { } picked)
                        return null; // ESC at the first step → cancel the whole create
                    if (picked != mapName) chosenRepos = null; // new map → reset the repo choice
                    mapName = picked;
                    step = reposStep;
                    break;

                case reposStep:
                {
                    var mapRepos = cli.Maps.Get(mapName)!.Repos.Select(r => r.Name).ToList();
                    if (mapRepos.Count <= 1) chosenRepos = mapRepos;
                    else if (Term.SelectMany(console, $"Which [green]repos[/] from [bold]{Markup.Escape(mapName)}[/]?",
                                 mapRepos, chosenRepos ?? mapRepos) is { } chosen)
                        chosenRepos = chosen;
                    else if (mapFixed) return null;   // ESC with no map step to go back to → cancel
                    else { step = mapStep; break; }   // ESC → back to the map pick

                    step = done;
                    break;
                }
            }
        }

        var without = ReposToWithout(mapName, chosenRepos!);
        var (map, repos) = cli.MapResolver.Resolve(mapName, without);
        var name = PromptName(console, s.Name);
        return (map, repos, name);
    }

    string PromptName(IAnsiConsole console, string? preset)
    {
        var prompt = new TextPrompt<string>("Workspace [green]name[/]:")
            .Validate(n =>
                string.IsNullOrWhiteSpace(n) ? ValidationResult.Error("a name is required")
                : cli.Workspaces.Get(n.Trim()) is not null ? ValidationResult.Error($"workspace '{n.Trim()}' already exists")
                : ValidationResult.Success());
        if (!string.IsNullOrWhiteSpace(preset)) prompt.DefaultValue(preset!);
        return console.Prompt(prompt).Trim();
    }

    // After the checklist: the actionable facts it doesn't carry — the allocated ports and where each
    // repo's worktree landed. Setup outcomes already showed as ticks in the checklist above.
    static void RenderSummary(IAnsiConsole console, InstanceRecord record)
    {
        console.MarkupLine($"[green]{Glyph.Check(console)}[/] created workspace [bold]{Markup.Escape(record.Workspace)}[/]");
        if (record.IsPartial)
            console.MarkupLine($"  [yellow]partial[/]: without {Markup.Escape(string.Join(", ", record.ExcludedRepos))}" +
                (record.SkippedPorts.Count > 0
                    ? $"; ports not provisioned: {Markup.Escape(string.Join(", ", record.SkippedPorts))}"
                    : ""));

        if (record.Ports.Count > 0)
        {
            var ports = CliFormat.Table("PORT", "VALUE");
            foreach (var p in record.Ports.OrderBy(p => p.Key))
                ports.AddRow(Markup.Escape(p.Key), Markup.Escape(p.Value.ToString()));
            console.Write(ports);
        }

        foreach (var r in record.Repos)
            console.MarkupLine($"  [dim]{Markup.Escape(r.Name)}[/]  {Markup.Escape(r.WorktreePath)}");

        if (record.Repos.Any(r => r.Setup.Any(step => !step.Success)))
            console.MarkupLine("[yellow]note:[/] a setup command failed — the workspace was kept; finish setup manually in the worktree.");
    }

    /// <summary>Turn <c>--only</c>/<c>--without</c> into the <c>--without</c> exclusion list the map resolver
    /// takes (null/empty = the whole map). Both lists are validated against the map's repos so a typo'd repo
    /// fails loudly, and <c>--only</c> is expressed as "exclude everything else" so the two flags are
    /// interchangeable ways of naming the same slice.</summary>
    IReadOnlyList<string>? MapWithout(string mapName, List<string> only, List<string> without)
    {
        if (only.Count == 0 && without.Count == 0) return null;
        var map = cli.Maps.Get(mapName) ?? throw new Core.Maps.MapException($"unknown map '{mapName}'");
        var mapRepos = map.Repos.Select(r => r.Name).ToList();

        var unknown = only.Concat(without).Where(r => !mapRepos.Contains(r, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            throw new Core.Maps.MapException(
                $"map '{mapName}' has no repo{(unknown.Count == 1 ? "" : "s")} " +
                $"{string.Join(", ", unknown.Select(r => $"'{r}'"))} " +
                $"(it has: {string.Join(", ", mapRepos)})");

        var keep = only.Count > 0
            ? new HashSet<string>(only, StringComparer.Ordinal)
            : new HashSet<string>(mapRepos.Where(r => !without.Contains(r, StringComparer.Ordinal)), StringComparer.Ordinal);
        var exclude = mapRepos.Where(r => !keep.Contains(r)).ToList();
        if (exclude.Count == mapRepos.Count)
            throw new Core.Maps.MapException($"that leaves no repos to create from map '{mapName}'");
        return exclude;
    }

    // The interactive repo pick expressed as the resolver's --without list: every map repo the user didn't
    // choose. A full selection returns null (the whole map).
    IReadOnlyList<string>? ReposToWithout(string mapName, IReadOnlyList<string> chosen)
    {
        var mapRepos = cli.Maps.Get(mapName)!.Repos.Select(r => r.Name).ToList();
        var keep = new HashSet<string>(chosen, StringComparer.Ordinal);
        var exclude = mapRepos.Where(r => !keep.Contains(r)).ToList();
        return exclude.Count == 0 ? null : exclude;
    }
}

[Description("List workspaces")]
public sealed class LsCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var all = cli.Workspaces.List();
        if (s.Json) { CliOutput.Json(all); return 0; }
        if (all.Count == 0)
        {
            cli.Ansi.MarkupLine("[dim]no workspaces yet — create one with[/] [bold]sprig create <name> --repo <path>[/]");
            return 0;
        }

        var table = CliFormat.Table("WORKSPACE", "REPOS", "PORTS", "STATUS");
        foreach (var r in all.OrderBy(r => r.Workspace))
            table.AddRow(
                Markup.Escape(r.Workspace),
                Markup.Escape(string.Join(",", r.Repos.Select(x => x.Name))),
                Markup.Escape(CliFormat.Ports(r.Ports)),
                // A flagged workspace is one a teardown couldn't finish — call it out here rather than
                // the stale lifecycle status, so it stands out in the list as needing a retry.
                r.TeardownFailed
                    ? $"[yellow]teardown failed[/]"
                    : Markup.Escape($"{r.LastStatus}"));
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Everything about one workspace: repos, ports, drift, live containers")]
public sealed class InfoCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)
        var record = cli.Workspaces.Get(workspace) ?? throw new ArgumentException($"unknown workspace '{workspace}'");
        var report = cli.Reconciler.Inspect(workspace);
        // The one-stop view also folds in the live container state that `status` shows. Best-effort:
        // a workspace's record and drift must remain inspectable even when docker isn't running, so a
        // failure here degrades to null rather than taking the whole command down.
        var containers = TryContainers(workspace);

        if (s.Json) { CliOutput.Json(new { record, drift = report, containers }); return 0; }

        var console = cli.Ansi;
        console.MarkupLine($"[bold]{Markup.Escape(record.Workspace)}[/]  " +
            $"[dim]status[/] {Markup.Escape($"{record.LastStatus}")}  " +
            $"[dim]created[/] {Markup.Escape($"{record.CreatedAt:u}")}");

        if (record.TeardownFailed)
        {
            console.MarkupLine("[yellow]teardown failed[/] — record kept; fix the below and run [bold]sprig ws rm[/] again:");
            foreach (var issue in record.TeardownIssues)
                console.MarkupLine($"  [yellow]•[/] {Markup.Escape(issue)}");
        }
        if (record.IsPartial)
        {
            console.MarkupLine($"[yellow]partial[/]: stack '{Markup.Escape($"{record.Stack}")}' without {Markup.Escape(string.Join(", ", record.ExcludedRepos))}");
            if (record.SkippedPorts.Count > 0)
                console.MarkupLine($"  [dim]ports not provisioned:[/] {Markup.Escape(string.Join(", ", record.SkippedPorts))}");
        }

        if (record.Ports.Count > 0)
        {
            var ports = CliFormat.Table("PORT", "VALUE");
            foreach (var p in record.Ports.OrderBy(p => p.Key))
                ports.AddRow(Markup.Escape(p.Key), Markup.Escape(p.Value.ToString()));
            console.Write(ports);
        }
        else console.MarkupLine("[dim]no ports[/]");

        var repos = CliFormat.Table("REPO", "BRANCH", "STATE", "WORKTREE");
        foreach (var r in record.Repos)
        {
            var state = report?.Repos.FirstOrDefault(x => x.WorktreePath == r.WorktreePath)?.State;
            repos.AddRow(
                Markup.Escape(r.Name),
                string.IsNullOrEmpty(r.Branch) ? "[dim]-[/]" : Markup.Escape(r.Branch),
                state is { } st ? CliFormat.StateBadge(st) : "[dim]?[/]",
                $"[dim]{Markup.Escape(r.WorktreePath)}[/]");
        }
        console.Write(repos);

        if (containers is null)
            console.MarkupLine("[dim]containers: docker unavailable[/]");
        else if (containers.Count == 0)
            console.MarkupLine("[dim]containers: none running[/]");
        else
        {
            var table = CliFormat.Table("CONTAINER", "STATE");
            foreach (var c in containers)
                table.AddRow(Markup.Escape(c.Name), Markup.Escape($"{c.State}"));
            console.Write(table);
        }
        return 0;
    }

    // The live container list for a workspace, or null if docker can't be reached.
    IReadOnlyList<ContainerStatus>? TryContainers(string workspace)
    {
        try { return cli.Workspaces.Status(workspace); }
        catch { return null; }
    }
}

[Description("Tear down a workspace (--force also deletes the branch)")]
public sealed class RmCommand(CliContext cli) : Command<RmCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[workspace]")]
        [Description("Workspace to destroy (omit at a terminal to pick interactively)")]
        public string? Workspace { get; set; }

        [CommandOption("-i|--interactive")]
        [Description("Force the interactive picker (choose the workspace, then confirm)")]
        public bool Interactive { get; set; }

        [CommandOption("--no-interactive|--ni")]
        [Description("Never prompt — fail instead of asking (implied by --json, a pipe, or CI)")]
        public bool NoInteractive { get; set; }

        [CommandOption("--force")]
        [Description("Also delete the git branch")]
        public bool Force { get; set; }

        [CommandOption("--yes")]
        [Description("Skip the confirmation prompt (required in a script, a pipe, CI, --json, or --ni)")]
        public bool Yes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        // At a terminal we always confirm before destroying — so a bare `rm` picks then confirms, and a
        // named `rm feat` still asks. --yes is the pre-confirmation that skips the prompt (and the only way
        // through without a terminal, in a script/CI/--json); --ni forces that non-interactive path.
        var interactive = Interactivity.Resolve(s.Interactive, s.NoInteractive, s.Json, hasPrimaryInput: s.Yes);

        var console = Term.Create();

        // Pick the workspace: interactively when none was named, otherwise straight from the positional.
        string workspace;
        if (s.Workspace is { } named)
            workspace = named;
        else if (interactive)
        {
            var all = cli.Workspaces.List();
            if (all.Count == 0) { console.MarkupLine("[yellow]no workspaces to destroy[/]"); return 0; }
            if (Term.SelectOne(console, "Select a [green]workspace[/] to destroy: [grey](esc cancels)[/]",
                    all.Select(w => w.Workspace).OrderBy(x => x, StringComparer.Ordinal)) is not { } pick)
            {
                console.MarkupLine("[yellow]cancelled[/]");
                return 0;
            }
            workspace = pick;
        }
        else
            throw new ArgumentException("rm requires a workspace name (or run it at a terminal to be prompted)");

        // Confirm the irreversible teardown. --yes pre-confirms; at a terminal we ask (and offer the
        // branch-delete choice, unless --force already settled it); without a terminal, no --yes refuses.
        var force = s.Force;
        if (!s.Yes)
        {
            if (!interactive)
            {
                var msg = $"refusing to remove '{workspace}' without --yes";
                if (s.Json) CliOutput.Json(new { ok = false, error = msg });
                else Console.Error.WriteLine(msg);
                return 1;
            }
            if (!s.Force)
                force = console.Prompt(new ConfirmationPrompt("Also delete the git branch (loses any commits made in the worktree)?") { DefaultValue = false });
            if (!console.Prompt(new ConfirmationPrompt($"Destroy '{Markup.Escape(workspace)}'? Infra is stopped, volumes wiped, worktrees removed.") { DefaultValue = false }))
            {
                console.MarkupLine("[yellow]cancelled[/]");
                return 0;
            }
        }

        // The machine path stays quiet and structured: no checklist, just the result object.
        if (s.Json)
        {
            cli.Workspaces.Remove(workspace, force);
            // Teardown keeps a flagged record when it couldn't finish; a leftover record means it
            // was only partial, so report that rather than a clean removal (and exit non-zero).
            if (cli.Workspaces.Get(workspace) is { TeardownFailed: true } leftover)
            {
                CliOutput.Json(new { ok = false, workspace, action = "remove", teardownFailed = true, issues = leftover.TeardownIssues });
                return 1;
            }
            return CliOutput.Ok(true, "", new { ok = true, workspace, action = "remove", branchDeleted = force });
        }

        var record = cli.Workspaces.Get(workspace) ?? throw new ArgumentException($"unknown workspace '{workspace}'");
        var plan = cli.Workspaces.PlanRemove(record, force);
        console.MarkupLine($"[bold]Destroying workspace[/] [red]{Markup.Escape(workspace)}[/]");
        Checklist.Run(console, plan, progress => cli.Workspaces.Remove(workspace, force, progress));

        // A kept, flagged record means some layer couldn't be torn down. Say what, and point at the
        // retry — teardown is idempotent, so re-running once the blocker is fixed finishes the job.
        if (cli.Workspaces.Get(workspace) is { TeardownFailed: true } kept)
        {
            console.MarkupLine($"[yellow]teardown incomplete[/] for [bold]{Markup.Escape(workspace)}[/] — record kept so you can retry:");
            foreach (var issue in kept.TeardownIssues)
                console.MarkupLine($"  [yellow]•[/] {Markup.Escape(issue)}");
            console.MarkupLine($"  fix the above, then run [bold]sprig ws rm {Markup.Escape(workspace)}[/] again");
            return 1;
        }

        console.MarkupLine($"[green]{Glyph.Check(console)}[/] destroyed workspace [bold]{Markup.Escape(workspace)}[/]" +
            (force ? " [dim](branch deleted)[/]" : ""));
        return 0;
    }
}

[Description("Bring the workspace's docker infra up")]
public sealed class UpCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)
        cli.Workspaces.Up(workspace);
        return CliOutput.Ok(s.Json, $"infra up for '{workspace}'", new { ok = true, workspace, action = "up" });
    }
}

[Description("Stop infra (--volumes also wipes data)")]
public sealed class DownCommand(CliContext cli) : Command<DownCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--volumes")]
        [Description("Also remove docker volumes (wipes data)")]
        public bool Volumes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)
        cli.Workspaces.Down(workspace, s.Volumes);
        return CliOutput.Ok(s.Json, $"infra down for '{workspace}'{(s.Volumes ? " (volumes removed)" : "")}",
            new { ok = true, workspace, action = "down", volumesRemoved = s.Volumes });
    }
}

[Description("Restart infra (down then up, keeping volumes)")]
public sealed class RestartCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)
        cli.Workspaces.RestartInfra(workspace);
        return CliOutput.Ok(s.Json, $"infra restarted for '{workspace}'", new { ok = true, workspace, action = "restart" });
    }
}

[Description("Deprecated alias of 'restart' — restart infra (down then up)")]
public sealed class ResetCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)
        // 'reset' now means the git resync (see 'ws refresh'); infra restart moved to 'ws restart'. This
        // stays as an alias for one release — nudge, but don't disrupt the --json contract.
        if (!s.Json)
            cli.Ansi.MarkupLine("[dim]note: 'ws reset' restarts infra and is now called [bold]ws restart[/]; " +
                "for the git resync see [bold]ws refresh[/].[/]");
        cli.Workspaces.RestartInfra(workspace);
        return CliOutput.Ok(s.Json, $"infra restarted for '{workspace}'", new { ok = true, workspace, action = "restart" });
    }
}

[Description("Resync a workspace's repos to their base branch (keeps installed deps)")]
public sealed class RefreshCommand(CliContext cli) : Command<RefreshCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--only <repos>")]
        [Description("Only refresh these repos (comma-separated or repeated)")]
        public string[] Only { get; set; } = [];

        [CommandOption("--force")]
        [Description("Discard commits not in the base branch (a refresh resets to base)")]
        public bool Force { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)
        var only = CliFormat.SplitList(s.Only);

        // Machine path stays quiet; human path drives the live checklist (resync → env → setup → infra).
        if (s.Json) { CliOutput.Json(cli.Workspaces.RefreshToBase(workspace, only, s.Force)); return 0; }

        var current = cli.Workspaces.Get(workspace)
            ?? throw new ArgumentException($"unknown workspace '{workspace}'");
        // A map/ad-hoc workspace refreshes from its own recorded config + stored module scopes — there is no
        // separate overlay to re-resolve (the map isn't consulted after checkout).
        IReadOnlyList<ResolvedRepo>? resolvedRepos = null;

        var console = Term.Create();
        console.MarkupLine($"[bold]Refreshing[/] [green]{Markup.Escape(workspace)}[/]…");
        var plan = cli.Workspaces.PlanRefresh(current, only.Count > 0 ? only : null, resolvedRepos);
        InstanceRecord record = null!;
        Checklist.Run(console, plan, progress =>
            record = cli.Workspaces.RefreshToBase(workspace, only, s.Force, removeVolumes: false, progress, resolvedRepos));

        console.MarkupLine($"[green]{Glyph.Check(console)}[/] refreshed workspace [bold]{Markup.Escape(workspace)}[/]" +
            (only.Count > 0 ? $" [dim]({Markup.Escape(string.Join(", ", only))})[/]" : ""));
        foreach (var r in record.Repos)
            console.MarkupLine($"  [dim]{Markup.Escape(r.Name)}[/]  {Markup.Escape(r.WorktreePath)}");
        if (record.Repos.Any(r => r.Setup.Any(step => !step.Success)))
            console.MarkupLine("[yellow]note:[/] a setup command failed — finish setup manually in the worktree.");
        return 0;
    }
}

[Description("Live container status only (a subset of info)")]
public sealed class StatusCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json);
        if (workspace is null) return 0; // interactive cancel (ESC)
        var containers = cli.Workspaces.Status(workspace);
        if (s.Json) { CliOutput.Json(containers); return 0; }
        if (containers.Count == 0) { cli.Ansi.MarkupLine("[dim]no containers running[/]"); return 0; }
        var table = CliFormat.Table("CONTAINER", "STATE");
        foreach (var c in containers)
            table.AddRow(Markup.Escape(c.Name), Markup.Escape($"{c.State}"));
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Detect (and optionally repair) drift, one workspace or all")]
public sealed class ReconcileCommand(CliContext cli) : Command<ReconcileCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[workspace]")]
        [Description("A single workspace (omit to check all)")]
        public string? Workspace { get; set; }

        [CommandOption("--repair")]
        [Description("Repair any drift found")]
        public bool Repair { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var reports = s.Workspace is null
            ? cli.Reconciler.InspectAll()
            : cli.Reconciler.Inspect(s.Workspace) is { } r ? [r] : throw new ArgumentException($"unknown workspace '{s.Workspace}'");

        // Run repairs first (when asked) so both output paths can report what was done — the JSON
        // path folds them into one object rather than trailing plain text after the blob.
        var repairs = s.Repair
            ? reports.Where(r => r.HasDrift)
                .Select(r => new { workspace = r.Workspace, actions = cli.Reconciler.Repair(r.Workspace) })
                .ToList()
            : null;

        if (s.Json)
        {
            if (repairs is null) CliOutput.Json(reports);
            else CliOutput.Json(new { reports, repairs });
            return 0;
        }

        var console = cli.Ansi;
        if (reports.Count == 0) console.MarkupLine("[dim]no workspaces to check[/]");
        else
            foreach (var report in reports)
            {
                var flag = report.IsHealthy ? "[green]healthy[/]" : report.HasDrift ? "[yellow]drift[/]" : "[dim]gone[/]";
                console.MarkupLine($"{flag} [bold]{Markup.Escape(report.Workspace)}[/]");
                foreach (var repo in report.Repos)
                    console.MarkupLine($"    {Markup.Escape(repo.RepoName)}  {CliFormat.StateBadge(repo.State)}  " +
                        $"[dim]{Markup.Escape(repo.WorktreePath)}[/]");
            }

        if (repairs is not null)
            foreach (var fix in repairs)
                foreach (var action in fix.actions)
                    console.MarkupLine($"[green]{Glyph.Check(console)}[/] repaired: {Markup.Escape(action)}");
        return 0;
    }
}

[Description("Launch the sprig desktop app")]
public sealed class OpenCommand : Command<GlobalSettings>
{
    // Launch the desktop app (sprig-gui) that ships alongside the CLI — the escape hatch to the GUI
    // when you want something more granular than the terminal offers. Detaches (UseShellExecute, no
    // wait) so the terminal is handed straight back, exactly as if you'd double-clicked the app.
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var exe = LocateGui() ?? throw new FileNotFoundException(
            "could not find the sprig app (sprig-gui). Install sprig via the installer so both " +
            "ship together, or build Sprig.App alongside the CLI.");

        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        AnsiConsole.MarkupLine("opening [bold]sprig[/]…");
        return 0;
    }

    // In the Velopack package sprig-gui(.exe) sits in the same directory as sprig(.exe), so look
    // beside ourselves first. Failing that (a dev build, where each project has its own bin output),
    // swap the Sprig.Cli segment of our path for Sprig.App — same bin/<config>/<tfm> layout either
    // side — and probe there. Covers both worlds with no env var or config to set.
    static string? LocateGui()
    {
        var exeName = OperatingSystem.IsWindows() ? "sprig-gui.exe" : "sprig-gui";
        var baseDir = AppContext.BaseDirectory;

        foreach (var dir in new[] { baseDir, baseDir.Replace("Sprig.Cli", "Sprig.App") })
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

[Description("Install a newer release in place (--check only reports)")]
public sealed class UpdateCommand : Command<UpdateCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--check")]
        [Description("Only report whether an update is available")]
        public bool Check { get; set; }
    }

    // Install a newer release in place (or, with --check, just report whether one exists). Delegates
    // to CliUpdater, which drives Velopack's check/download/apply against the same feed as the app.
    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation) => CliUpdater.Run(s.Check);
}

[Description("Detect & propose a .sprig.json for a repo")]
public sealed class InitCommand(CliContext cli) : Command<InitCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[repo]")]
        [Description("Repo path (default: current directory)")]
        public string? RepoArg { get; set; }

        [CommandOption("--repo <path>")]
        [Description("Repo path (alternative to the positional)")]
        public string? Repo { get; set; }

        [CommandOption("--print")]
        [Description("Preview the proposal without writing")]
        public bool Print { get; set; }

        [CommandOption("--force")]
        [Description("Overwrite an existing .sprig.json")]
        public bool Force { get; set; }

        [CommandOption("--register")]
        [Description("Register the repo after writing")]
        public bool Register { get; set; }

        [CommandOption("--map")]
        [Description("[experimental] Propose the map model (provides/needs) instead of stack inputs")]
        public bool Map { get; set; }
    }

    static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var repo = s.Repo ?? s.RepoArg ?? Environment.CurrentDirectory;
        var root = Path.GetFullPath(repo);

        if (!Directory.Exists(root))
            throw new ArgumentException($"path does not exist: {root}");

        var inspector = new InitInspector(cli.Git);
        var proposal = inspector.InspectMap(root);   // map is the only model now (--map kept as a no-op alias)
        var text = JsonSerializer.Serialize(proposal.Config, ConfigJsonOptions);

        // --print previews without touching disk — the one read-only path. --json pairs with it to
        // get the proposal as a machine object; without --print, --json reports the write instead.
        if (s.Print)
        {
            if (s.Json) CliOutput.Json(proposal);
            else
            {
                foreach (var note in proposal.Notes)
                    cli.Ansi.MarkupLine($"[yellow]note:[/] {Markup.Escape(note)}");
                Console.WriteLine(text); // raw JSON preview — kept unstyled so it stays copy-pasteable
            }
            return 0;
        }

        var target = Path.Combine(root, ".sprig.json");
        if (File.Exists(target) && !s.Force)
        {
            var msg = $".sprig.json already exists at {target} — pass --force to overwrite, or --print to preview";
            if (s.Json) CliOutput.Json(new { ok = false, error = msg });
            else Console.Error.WriteLine(msg);
            return 1;
        }

        File.WriteAllText(target, text + "\n");
        var registered = s.Register ? cli.Repos.Add(root).Name : null;

        if (s.Json)
        {
            CliOutput.Json(new { ok = true, path = target, registered, notes = proposal.Notes });
            return 0;
        }

        foreach (var note in proposal.Notes)
            cli.Ansi.MarkupLine($"[yellow]note:[/] {Markup.Escape(note)}");
        CliOutput.Success($"wrote {target}");
        if (registered is not null)
            CliOutput.Success($"registered '{registered}'");
        else
            cli.Ansi.MarkupLine($"[dim]next:[/] [bold]sprig repo add \"{Markup.Escape(root)}\"[/]   " +
                $"[dim](then add it to a stack, or: sprig create <name> --repo \"{Markup.Escape(root)}\")[/]");
        return 0;
    }
}
