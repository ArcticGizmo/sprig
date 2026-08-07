using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Core.Docker;
using Sprig.Core.Init;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;

namespace Sprig.Cli.Commands;

// The workspace verbs and the meta commands (open/update/init). The workspace is the primary object,
// so these verbs stay top-level and unqualified; `ws`/`workspace` mirrors them as an optional prefix
// (wired in CliApp). Each command takes the shared CliContext and preserves the exact --json contract
// and human wording the hand-rolled dispatcher shipped.

[Description("Create an isolated workspace from a stack or a single repo")]
public sealed class CreateCommand(CliContext cli) : Command<CreateCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[name]")]
        [Description("Workspace name (omit with -i)")]
        public string? Name { get; set; }

        [CommandOption("-i|--interactive")]
        [Description("Pick stack, repos, modules and name interactively")]
        public bool Interactive { get; set; }

        [CommandOption("--stack <name>")]
        [Description("Create from a named stack")]
        public string? Stack { get; set; }

        [CommandOption("--repo <path>")]
        [Description("Create from a single repo path")]
        public string? Repo { get; set; }

        [CommandOption("--only <repos>")]
        [Description("Partial: only these stack repos (comma-separated or repeated)")]
        public string[] Only { get; set; } = [];

        [CommandOption("--without <repos>")]
        [Description("Partial: every stack repo except these")]
        public string[] Without { get; set; } = [];
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (s.Interactive && s.Json)
            throw new ArgumentException("-i is interactive — it can't be combined with --json");

        // A console bound straight to real stdout: both the prompts and the live checklist need to
        // drive the cursor, which throws on a redirected handle. (The shared console is late-bound for
        // tests, so never reads as interactive.) `ws create` is never driven by the in-process tests.
        var console = CreateConsole();

        // Gather the target either by asking (‑i) or from the flags/positional as before.
        ResolvedStack resolved;
        string name;
        if (s.Interactive)
        {
            if (Console.IsInputRedirected)
                throw new ArgumentException("-i needs an interactive terminal (stdin is redirected)");
            (resolved, name) = Prompt(console, s);
        }
        else
        {
            var only = CliFormat.SplitList(s.Only);
            var without = CliFormat.SplitList(s.Without);
            if ((only.Count > 0 || without.Count > 0) && s.Stack is null)
                throw new ArgumentException("--only/--without narrow a stack — they need --stack <name>");
            name = s.Name ?? throw new ArgumentException("create requires a workspace name (or use -i)");
            resolved = s.Stack is not null
                    ? cli.Resolver.Resolve(s.Stack, Selection(cli.Stacks, s.Stack, only, without))
                : s.Repo is not null ? cli.Workspaces.ResolveSingleRepo(s.Repo)
                : throw new ArgumentException("create requires --stack <name> or --repo <path> (or use -i)");
        }

        // Plan up front so pre-flight problems (bad name, duplicate) surface before any output, and so
        // the checklist can list every step, pending, before work starts.
        var plan = cli.Workspaces.PlanCreate(resolved, name);

        // The machine path stays quiet and structured: no checklist, just the record as JSON.
        if (s.Json) { CliOutput.Json(cli.Workspaces.Create(resolved, name)); return 0; }

        console.MarkupLine($"[bold]Creating workspace[/] [green]{Markup.Escape(name)}[/]" +
            (resolved.StackName is { } st ? $" [dim]from stack {Markup.Escape(st)}[/]" : ""));

        var rows = plan.Select(p => new StepRow(p.Id, p.Label, p.SubStep)).ToList();
        var record = console.Profile.Capabilities.Interactive
            ? RunLive(console, resolved, name, rows)
            : RunPlain(console, resolved, name, rows);

        RenderSummary(console, record);
        return 0;
    }

    static IAnsiConsole CreateConsole()
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Detect,
            ColorSystem = ColorSystemSupport.Detect,
            Out = new AnsiConsoleOutput(Console.Out),
        });
        // Redirected: detection assumes 80 cols and wraps long worktree paths; widen it. Interactive:
        // keep the real terminal width so the live checklist and prompts lay out correctly.
        if (Console.IsOutputRedirected) console.Profile.Width = 200;
        return console;
    }

    // The `-i` flow: pick a stack, choose its repos and each repo's modules (all selected by default),
    // then name the workspace. Repos are narrowed via the resolver; modules are narrowed by rewriting
    // each repo's config so only the chosen ones remain in EffectiveModules.
    (ResolvedStack resolved, string name) Prompt(IAnsiConsole console, Settings s)
    {
        var allStacks = cli.Stacks.List();
        if (allStacks.Count == 0)
            throw new ArgumentException("no stacks defined — create one with 'sprig stack create' first");

        var stackName = s.Stack is { } preset
            ? (cli.Stacks.Get(preset) is not null ? preset : throw new ArgumentException($"unknown stack '{preset}'"))
            : allStacks.Count == 1
                ? allStacks[0].Name
                : console.Prompt(new SelectionPrompt<string>()
                    .Title("Select a [green]stack[/]:")
                    .PageSize(12)
                    .AddChoices(allStacks.Select(x => x.Name)));

        var stack = cli.Stacks.Get(stackName)!;

        // Repos — all pre-selected; Required() blocks submitting an empty set.
        var repos = stack.Repos.Count <= 1
            ? stack.Repos.ToList()
            : console.Prompt(PreselectAll(new MultiSelectionPrompt<string>()
                    .Title($"Which [green]repos[/] from [bold]{Markup.Escape(stackName)}[/]?")
                    .Required()
                    .PageSize(12)
                    .InstructionsText("[grey](space toggles, enter accepts — all selected by default)[/]")
                    .AddChoices(stack.Repos), stack.Repos));

        var resolved = cli.Resolver.Resolve(stackName, repos.Count == stack.Repos.Count ? null : repos);

        // Modules per repo — only worth asking when a repo has more than one.
        var narrowed = new List<ResolvedRepo>();
        foreach (var repo in resolved.Repos)
        {
            var modules = repo.Config.EffectiveModules;
            if (modules.Count <= 1) { narrowed.Add(repo); continue; }

            var names = modules.Select(m => m.Name).ToList();
            var chosen = console.Prompt(PreselectAll(new MultiSelectionPrompt<string>()
                .Title($"Which [green]modules[/] of [bold]{Markup.Escape(repo.Name)}[/]?")
                .Required()
                .PageSize(12)
                .InstructionsText("[grey](space toggles, enter accepts)[/]")
                .AddChoices(names), names));

            if (chosen.Count == modules.Count) { narrowed.Add(repo); continue; }
            var keep = new HashSet<string>(chosen, StringComparer.Ordinal);
            var kept = modules.Where(m => keep.Contains(m.Name)).ToList();
            // Rewrite the config so EffectiveModules is exactly the chosen set: put them in Modules and
            // clear the legacy flat fields (which would otherwise synthesise a root module back in).
            narrowed.Add(repo with { Config = repo.Config with { Modules = kept, Env = null, Compose = null, Setup = null } });
        }
        resolved = resolved with { Repos = narrowed };

        var namePrompt = new TextPrompt<string>("Workspace [green]name[/]:")
            .Validate(n =>
                string.IsNullOrWhiteSpace(n) ? ValidationResult.Error("a name is required")
                : cli.Workspaces.Get(n.Trim()) is not null ? ValidationResult.Error($"workspace '{n.Trim()}' already exists")
                : ValidationResult.Success());
        if (!string.IsNullOrWhiteSpace(s.Name)) namePrompt.DefaultValue(s.Name!);
        var name = console.Prompt(namePrompt).Trim();

        return (resolved, name);
    }

    // Pre-select every choice so the default is "use everything" — the common case.
    static MultiSelectionPrompt<string> PreselectAll(MultiSelectionPrompt<string> prompt, IEnumerable<string> items)
    {
        foreach (var item in items) prompt.Select(item);
        return prompt;
    }

    // A live checklist: every step shows up front (pending), then ticks over to running → done/warning
    // as the Core reports it, streaming the running step's latest output line beneath it — the same
    // shape as the GUI's progress window. Interactive terminals only (it redraws in place).
    InstanceRecord RunLive(IAnsiConsole console, ResolvedStack resolved, string name, List<StepRow> rows)
    {
        var byId = rows.ToDictionary(r => r.Id);
        InstanceRecord record = null!;
        console.Live(Checklist(rows))
            .AutoClear(false)
            .Start(ctx =>
            {
                var progress = new SyncProgress<WorkspaceStepProgress>(p =>
                {
                    if (!byId.TryGetValue(p.StepId, out var row)) return;
                    if (!string.IsNullOrEmpty(p.Output)) row.Output = p.Output!.Trim();
                    else { row.State = p.State; if (p.Detail is { } d) row.Detail = d; }
                    ctx.UpdateTarget(Checklist(rows));
                });
                record = cli.Workspaces.Create(resolved, name, progress);
                ctx.UpdateTarget(Checklist(rows));
            });
        return record;
    }

    // Redirected/piped fallback (no cursor control): emit one line as each step starts, so the output
    // is still a readable running log rather than a frozen pause.
    InstanceRecord RunPlain(IAnsiConsole console, ResolvedStack resolved, string name, List<StepRow> rows)
    {
        var byId = rows.ToDictionary(r => r.Id);
        var progress = new SyncProgress<WorkspaceStepProgress>(p =>
        {
            if (!byId.TryGetValue(p.StepId, out var row) || !string.IsNullOrEmpty(p.Output)) return;
            row.State = p.State;
            if (p.State == WorkspaceStepState.Running)
                console.MarkupLine($"[grey]»[/] {Markup.Escape(row.Label)}");
            else if (p.State is WorkspaceStepState.Warning or WorkspaceStepState.Error && p.Detail is { } d)
                console.MarkupLine($"  [yellow]{Markup.Escape(d)}[/]");
        });
        return cli.Workspaces.Create(resolved, name, progress);
    }

    // The checklist as it stands right now: one line per planned step (sub-steps indented), a state
    // glyph, plus the running/failed step's latest output line dimmed beneath it.
    static IRenderable Checklist(IReadOnlyList<StepRow> rows)
    {
        var lines = new List<IRenderable>();
        foreach (var r in rows)
        {
            var indent = r.SubStep ? "    " : "";
            var marker = r.State switch
            {
                WorkspaceStepState.Done => "[green]✓[/]",
                WorkspaceStepState.Warning => "[yellow]![/]",
                WorkspaceStepState.Error => "[red]✗[/]",
                WorkspaceStepState.Running => "[blue]»[/]",
                _ => "[grey]○[/]",
            };
            var label = Markup.Escape(r.Label);
            var body = r.State == WorkspaceStepState.Pending ? $"[grey]{label}[/]" : label;
            var detail = string.IsNullOrWhiteSpace(r.Detail) ? "" : $" [dim]{Markup.Escape(r.Detail!)}[/]";
            lines.Add(new Markup($"{indent}{marker} {body}{detail}"));
            if (!string.IsNullOrEmpty(r.Output) &&
                r.State is WorkspaceStepState.Running or WorkspaceStepState.Warning or WorkspaceStepState.Error)
                lines.Add(new Markup($"{indent}    [dim]{Markup.Escape(Truncate(r.Output!))}[/]"));
        }
        return new Rows(lines);
    }

    // After the checklist: the actionable facts it doesn't carry — the allocated ports and where each
    // repo's worktree landed. Setup outcomes already showed as ticks in the checklist above.
    static void RenderSummary(IAnsiConsole console, InstanceRecord record)
    {
        console.MarkupLine($"[green]✔[/] created workspace [bold]{Markup.Escape(record.Workspace)}[/]");
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

    static string Truncate(string s, int max = 100)
        => s.Length <= max ? s : s[..(max - 1)] + "...";

    /// <summary>Mutable checklist row backing the live display: the current state, detail note and
    /// latest streamed output line for one planned step.</summary>
    sealed class StepRow(string id, string label, bool subStep)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public bool SubStep { get; } = subStep;
        public WorkspaceStepState State { get; set; } = WorkspaceStepState.Pending;
        public string? Detail { get; set; }
        public string? Output { get; set; }
    }

    /// <summary>Turn <c>--only</c>/<c>--without</c> into the repo subset to create (null = the whole
    /// stack). <c>--without</c> is resolved against the stack's repo list so the two flags are
    /// interchangeable ways of saying the same thing; naming an unknown repo is an error either way.</summary>
    static IReadOnlyList<string>? Selection(StackStore stacks, string stackName, List<string> only, List<string> without)
    {
        if (only.Count == 0 && without.Count == 0) return null;
        var stack = stacks.Get(stackName) ?? throw new StackException($"unknown stack '{stackName}'");

        // Validate both lists against the stack (Include does it for --only; do the same for --without
        // so a typo'd exclusion fails loudly instead of silently keeping every repo).
        var included = StackSelection.Include(stack, only.Count > 0 ? only : null);
        var unknown = without.Where(r => !stack.Repos.Contains(r, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
            throw new StackException(
                $"stack '{stackName}' has no repo{(unknown.Count == 1 ? "" : "s")} " +
                $"{string.Join(", ", unknown.Select(r => $"'{r}'"))} " +
                $"(it has: {string.Join(", ", stack.Repos)})");

        var drop = new HashSet<string>(without, StringComparer.Ordinal);
        var selection = included.Where(r => !drop.Contains(r)).ToList();
        if (selection.Count == 0)
            throw new StackException($"that leaves no repos to create from stack '{stackName}'");
        return selection;
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
            Console.WriteLine("no workspaces yet — create one with: sprig create <name> --repo <path>");
            return 0;
        }

        var table = CliFormat.Table("WORKSPACE", "REPOS", "PORTS", "STATUS");
        foreach (var r in all.OrderBy(r => r.Workspace))
            table.AddRow(
                Markup.Escape(r.Workspace),
                Markup.Escape(string.Join(",", r.Repos.Select(x => x.Name))),
                Markup.Escape(CliFormat.Ports(r.Ports)),
                Markup.Escape($"{r.LastStatus}"));
        cli.Ansi.Write(table);
        return 0;
    }
}

[Description("Everything about one workspace: repos, ports, drift, live containers")]
public sealed class InfoCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        var record = cli.Workspaces.Get(s.Workspace) ?? throw new ArgumentException($"unknown workspace '{s.Workspace}'");
        var report = cli.Reconciler.Inspect(s.Workspace);
        // The one-stop view also folds in the live container state that `status` shows. Best-effort:
        // a workspace's record and drift must remain inspectable even when docker isn't running, so a
        // failure here degrades to null rather than taking the whole command down.
        var containers = TryContainers(s.Workspace);

        if (s.Json) { CliOutput.Json(new { record, drift = report, containers }); return 0; }

        Console.WriteLine($"workspace: {record.Workspace}   status: {record.LastStatus}   created: {record.CreatedAt:u}");
        if (record.IsPartial)
        {
            Console.WriteLine($"partial: stack '{record.Stack}' without {string.Join(", ", record.ExcludedRepos)}");
            if (record.SkippedPorts.Count > 0)
                Console.WriteLine($"  ports not provisioned: {string.Join(", ", record.SkippedPorts)}");
        }
        Console.WriteLine($"ports: {CliFormat.Ports(record.Ports)}");
        foreach (var r in record.Repos)
        {
            var state = report?.Repos.FirstOrDefault(x => x.WorktreePath == r.WorktreePath)?.State;
            Console.WriteLine($"  {r.Name}");
            Console.WriteLine($"    worktree: {r.WorktreePath}  [{state}]");
            Console.WriteLine($"    branch:   {r.Branch}");
        }
        Console.WriteLine(containers switch
        {
            null => "containers: (docker unavailable)",
            { Count: 0 } => "containers: none running",
            _ => "containers:",
        });
        if (containers is { Count: > 0 })
            foreach (var c in containers)
                Console.WriteLine($"  {c.Name}  {c.State}");
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
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--force")]
        [Description("Also delete the git branch")]
        public bool Force { get; set; }

        [CommandOption("--yes")]
        [Description("Confirm the (irreversible) removal")]
        public bool Yes { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        if (!s.Yes)
        {
            var msg = $"refusing to remove '{s.Workspace}' without --yes";
            if (s.Json) CliOutput.Json(new { ok = false, error = msg });
            else Console.Error.WriteLine(msg);
            return 1;
        }
        cli.Workspaces.Remove(s.Workspace, s.Force);
        return CliOutput.Ok(s.Json, $"removed '{s.Workspace}'{(s.Force ? " (including branch)" : "")}",
            new { ok = true, workspace = s.Workspace, action = "remove", branchDeleted = s.Force });
    }
}

[Description("Bring the workspace's docker infra up")]
public sealed class UpCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        cli.Workspaces.Up(s.Workspace);
        return CliOutput.Ok(s.Json, $"infra up for '{s.Workspace}'", new { ok = true, workspace = s.Workspace, action = "up" });
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
        cli.Workspaces.Down(s.Workspace, s.Volumes);
        return CliOutput.Ok(s.Json, $"infra down for '{s.Workspace}'{(s.Volumes ? " (volumes removed)" : "")}",
            new { ok = true, workspace = s.Workspace, action = "down", volumesRemoved = s.Volumes });
    }
}

[Description("Restart infra (down then up)")]
public sealed class ResetCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        cli.Workspaces.Reset(s.Workspace);
        return CliOutput.Ok(s.Json, $"infra reset for '{s.Workspace}'", new { ok = true, workspace = s.Workspace, action = "reset" });
    }
}

[Description("Live container status only (a subset of info)")]
public sealed class StatusCommand(CliContext cli) : Command<WorkspaceSettings>
{
    protected override int Execute(CommandContext context, WorkspaceSettings s, CancellationToken cancellation)
    {
        var containers = cli.Workspaces.Status(s.Workspace);
        if (s.Json) { CliOutput.Json(containers); return 0; }
        if (containers.Count == 0) { Console.WriteLine("no containers running"); return 0; }
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

        if (reports.Count == 0) Console.WriteLine("no workspaces to check");
        else
            foreach (var report in reports)
            {
                var flag = report.IsHealthy ? "ok" : report.HasDrift ? "DRIFT" : "gone";
                Console.WriteLine($"[{flag}] {report.Workspace}");
                foreach (var repo in report.Repos)
                    Console.WriteLine($"    {repo.RepoName}: {repo.State}  ({repo.WorktreePath})");
            }

        if (repairs is not null)
            foreach (var fix in repairs)
                foreach (var action in fix.actions)
                    Console.WriteLine($"repaired: {action}");
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
        Console.WriteLine("opening sprig…");
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

        var proposal = new InitInspector(cli.Git).Inspect(root);
        var text = JsonSerializer.Serialize(proposal.Config, ConfigJsonOptions);

        // --print previews without touching disk — the one read-only path. --json pairs with it to
        // get the proposal as a machine object; without --print, --json reports the write instead.
        if (s.Print)
        {
            if (s.Json) CliOutput.Json(proposal);
            else
            {
                foreach (var note in proposal.Notes)
                    Console.WriteLine($"note: {note}");
                Console.WriteLine(text);
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
            Console.WriteLine($"note: {note}");
        Console.WriteLine($"wrote {target}");
        if (registered is not null)
            Console.WriteLine($"registered '{registered}'");
        else
            Console.WriteLine($"next: sprig repo add \"{root}\"   (then add it to a stack, or: sprig create <name> --repo \"{root}\")");
        return 0;
    }
}
