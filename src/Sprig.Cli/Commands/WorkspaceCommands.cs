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

namespace Sprig.Cli.Commands;

// The workspace verbs (create/ls/info/up/down/reset/status/rm/reconcile) and the meta commands
// (open/update/init). The workspace verbs live under the `ws` branch (wired in CliApp); each command
// takes the shared CliContext and preserves the exact --json contract the hand-rolled dispatcher shipped.

[Description("Create an isolated workspace from a stack or a single repo")]
public sealed class CreateCommand(CliContext cli) : Command<CreateCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[name]")]
        [Description("Workspace name (omit at a terminal to pick everything interactively)")]
        public string? Name { get; set; }

        [CommandOption("-i|--interactive")]
        [Description("Force the interactive picker (stack, repos, modules, name)")]
        public bool Interactive { get; set; }

        [CommandOption("--no-interactive|--ni")]
        [Description("Never prompt — fail instead of asking (implied by --json, a pipe, or CI)")]
        public bool NoInteractive { get; set; }

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
            hasPrimaryInput: s.Name is not null || s.Stack is not null || s.Repo is not null);

        var console = Term.Create();

        // Gather the target either by asking or from the flags/positional.
        ResolvedStack resolved;
        string name;
        if (interactive)
        {
            if (Prompt(console, s) is not { } picked)
            {
                console.MarkupLine("[yellow]cancelled[/]");
                return 0;
            }
            (resolved, name) = picked;
        }
        else
        {
            var only = CliFormat.SplitList(s.Only);
            var without = CliFormat.SplitList(s.Without);
            if ((only.Count > 0 || without.Count > 0) && s.Stack is null)
                throw new ArgumentException("--only/--without narrow a stack — they need --stack <name>");
            name = s.Name ?? throw new ArgumentException("create requires a workspace name (or run it at a terminal to be prompted)");
            resolved = s.Stack is not null
                    ? cli.Resolver.Resolve(s.Stack, Selection(cli.Stacks, s.Stack, only, without))
                : s.Repo is not null ? cli.Workspaces.ResolveSingleRepo(s.Repo)
                : throw new ArgumentException("create requires --stack <name> or --repo <path> (or run it at a terminal to be prompted)");
        }

        // Plan up front so pre-flight problems (bad name, duplicate) surface before any output, and so
        // the checklist can list every step, pending, before work starts.
        var plan = cli.Workspaces.PlanCreate(resolved, name);

        // The machine path stays quiet and structured: no checklist, just the record as JSON.
        if (s.Json) { CliOutput.Json(cli.Workspaces.Create(resolved, name)); return 0; }

        console.MarkupLine($"[bold]Creating workspace[/] [green]{Markup.Escape(name)}[/]" +
            (resolved.StackName is { } st ? $" [dim]from stack {Markup.Escape(st)}[/]" : ""));

        InstanceRecord record = null!;
        Checklist.Run(console, plan, progress => record = cli.Workspaces.Create(resolved, name, progress));

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

    // The `-i` flow: pick a stack, choose its repos and each repo's modules (all selected by default),
    // then name the workspace. Repos are narrowed via the resolver; modules are narrowed by rewriting
    // each repo's config so only the chosen ones remain in EffectiveModules.
    //
    // Returns null when the whole thing is cancelled (ESC at the very first step). The steps — stack,
    // repos, then modules per multi-module repo — are ESC-navigable: ESC steps back to the previous one
    // (re-showing your earlier choice), recomputing anything downstream. The name comes last because
    // TextPrompt can't be cancelled.
    (ResolvedStack resolved, string name)? Prompt(IAnsiConsole console, Settings s)
    {
        var allStacks = cli.Stacks.List();
        if (allStacks.Count == 0)
            throw new ArgumentException("no stacks defined — create one with 'sprig stack create' first");
        if (s.Stack is { } preset && cli.Stacks.Get(preset) is null)
            throw new ArgumentException($"unknown stack '{preset}'");

        // A step is skipped (and not a place ESC can land) when there's no real choice: a preset/only
        // stack, a single-repo stack.
        var stackFixed = s.Stack is not null || allStacks.Count == 1;
        var stackName = s.Stack ?? allStacks[0].Name;
        List<string> repos = [];
        ResolvedStack resolved = null!;
        List<ResolvedRepo> moduleRepos = [];
        var moduleSel = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        const int stackStep = 0, reposStep = 1, moduleStep = 2, done = 3;
        var step = stackFixed ? reposStep : stackStep;
        var modIdx = 0;

        while (step != done)
        {
            switch (step)
            {
                case stackStep:
                    if (Term.SelectOne(console, "Select a [green]stack[/]: [grey](esc cancels)[/]",
                            allStacks.Select(x => x.Name)) is not { } picked)
                        return null; // ESC at the first step → cancel the whole create
                    if (picked != stackName) { repos = []; moduleSel.Clear(); } // new stack → reset downstream
                    stackName = picked;
                    step = reposStep;
                    break;

                case reposStep:
                {
                    var stack = cli.Stacks.Get(stackName)!;
                    if (stack.Repos.Count <= 1) repos = stack.Repos.ToList();
                    else if (Term.SelectMany(console, $"Which [green]repos[/] from [bold]{Markup.Escape(stackName)}[/]?",
                                 stack.Repos, repos.Count > 0 ? repos : stack.Repos) is { } chosen)
                        repos = chosen;
                    else if (stackFixed) return null;      // ESC with no stack step to go back to → cancel
                    else { step = stackStep; break; }      // ESC → back to the stack pick

                    resolved = cli.Resolver.Resolve(stackName, repos.Count == stack.Repos.Count ? null : repos);
                    moduleRepos = resolved.Repos.Where(r => r.Config.EffectiveModules.Count > 1).ToList();
                    step = moduleStep;
                    modIdx = 0;
                    break;
                }

                case moduleStep:
                {
                    if (modIdx >= moduleRepos.Count) { step = done; break; }
                    var repo = moduleRepos[modIdx];
                    var names = repo.Config.EffectiveModules.Select(m => m.Name).ToList();
                    var pre = moduleSel.TryGetValue(repo.Name, out var prev) ? prev : names;
                    if (Term.SelectMany(console, $"Which [green]modules[/] of [bold]{Markup.Escape(repo.Name)}[/]?",
                            names, pre) is { } chosen)
                    {
                        moduleSel[repo.Name] = chosen;
                        modIdx++;
                    }
                    else if (--modIdx < 0) { step = reposStep; } // ESC before the first module → back to repos
                    break;
                }
            }
        }

        var name = PromptName(console, s.Name);
        return (ApplyModuleSelection(resolved, moduleSel), name);
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

    // Rewrite each repo whose modules were narrowed so EffectiveModules is exactly the chosen set: put
    // them in Modules and clear the legacy flat fields (which would otherwise synthesise a root module
    // back in). Repos left at "all modules" pass through untouched.
    static ResolvedStack ApplyModuleSelection(ResolvedStack resolved, IReadOnlyDictionary<string, List<string>> selection)
    {
        if (selection.Count == 0) return resolved;
        var narrowed = new List<ResolvedRepo>();
        foreach (var repo in resolved.Repos)
        {
            var modules = repo.Config.EffectiveModules;
            if (!selection.TryGetValue(repo.Name, out var chosen) || chosen.Count == modules.Count)
            {
                narrowed.Add(repo);
                continue;
            }
            var keep = new HashSet<string>(chosen, StringComparer.Ordinal);
            var kept = modules.Where(m => keep.Contains(m.Name)).ToList();
            narrowed.Add(repo with { Config = repo.Config with { Modules = kept, Env = null, Compose = null, Setup = null } });
        }
        return resolved with { Repos = narrowed };
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
        // For a pooled workspace, resolve its stack so the refresh honours the stack overlay (e.g.
        // stack-carried setup); an ad-hoc workspace has no stack and refreshes from its .sprig.json.
        var resolvedRepos = current.Stack is { } st ? cli.Resolver.Resolve(st, null).Repos : null;

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
