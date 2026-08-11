using System.ComponentModel;
using Sprig.Core.Pools;
using Sprig.Core.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig pool <sub>` — check out and manage the pooled workspaces built from a stack.

[Description("Show a stack's pool: its workspaces and how many of maxSlots are claimed")]
public sealed class PoolStatusCommand(CliContext cli) : Command<PoolStatusCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "<stack>")]
        [Description("Stack name")]
        public string Stack { get; set; } = "";
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var status = cli.Pools.Status(s.Stack);
        if (s.Json) { CliOutput.Json(status); return 0; }

        var console = cli.Ansi;
        console.MarkupLine(
            $"[bold]{Markup.Escape(status.Stack)}[/] pool  " +
            $"[dim]{status.ClaimedCount}/{status.MaxSlots} claimed[/]" +
            (status.Workspaces.Count > 0
                ? $" [dim]({status.FreeCount} free, {status.Headroom} unbuilt)[/]"
                : "") +
            (status.DegradedCount > 0 ? $" [yellow]· {status.DegradedCount} degraded[/]" : ""));

        if (status.Workspaces.Count == 0)
        {
            console.MarkupLine("[dim]no workspaces yet — check one out with[/] [bold]sprig pool checkout " +
                $"{Markup.Escape(status.Stack)}[/]");
            return 0;
        }

        var table = CliFormat.Table("WORKSPACE", "STATE", "LABEL", "LAST USED", "PORTS");
        foreach (var w in status.Workspaces)
            table.AddRow(
                Markup.Escape(w.Workspace),
                StateCell(w),
                Markup.Escape(w.Label ?? "-"),
                Markup.Escape(w.LastUsedAt?.ToString("u") ?? "-"),
                Markup.Escape(CliFormat.Ports(w.Ports)));
        console.Write(table);
        return 0;
    }

    // claimed / free, flagged degraded when the last setup run failed.
    static string StateCell(InstanceRecord w)
    {
        var state = w.Claimed ? "[green]claimed[/]" : "[dim]free[/]";
        return w.SetupFailed ? $"{state} [yellow](setup failed)[/]" : state;
    }
}

[Description("Check out a workspace from a stack's pool (label it, choose how it's handled)")]
public sealed class PoolCheckoutCommand(CliContext cli) : Command<PoolCheckoutCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[stack]")]
        [Description("Stack to check out from (omit at a terminal to pick)")]
        public string? Stack { get; set; }

        [CommandOption("--label <label>")]
        [Description("A label to recognise this checkout by (metadata only)")]
        public string? Label { get; set; }

        [CommandOption("--workspace <name>")]
        [Description("Reuse a specific unclaimed workspace")]
        public string? Workspace { get; set; }

        [CommandOption("--new")]
        [Description("Materialise a brand-new workspace (fails if the pool is full)")]
        public bool New { get; set; }

        [CommandOption("--fresh")]
        [Description("Reuse handling: reset all repos to base and wipe volumes (keeps installed deps)")]
        public bool Fresh { get; set; }

        [CommandOption("--as-is")]
        [Description("Reuse handling: resume exactly as left (default)")]
        public bool AsIs { get; set; }

        [CommandOption("--refresh <repos>")]
        [Description("Reuse handling: reset only these repos to base (comma-separated or repeated)")]
        public string[] Refresh { get; set; } = [];

        [CommandOption("--force")]
        [Description("For fresh/refresh: discard commits not in the base branch")]
        public bool Force { get; set; }

        [CommandOption("-i|--interactive")]
        [Description("Force the interactive pickers")]
        public bool Interactive { get; set; }

        [CommandOption("--no-interactive|--ni")]
        [Description("Never prompt — fail instead of asking")]
        public bool NoInteractive { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var interactive = Interactivity.Resolve(s.Interactive, s.NoInteractive, s.Json, hasPrimaryInput: s.Stack is not null);
        var console = Term.Create();

        var stackName = s.Stack ?? PickStack(console, interactive);
        if (stackName is null) { console.MarkupLine("[yellow]cancelled[/]"); return 0; }

        var status = cli.Pools.Status(stackName);

        if (ResolveTarget(console, status, s, interactive) is not { } target)
        {
            if (!s.Json) console.MarkupLine("[yellow]cancelled[/]");
            return 0;
        }

        var mode = CheckoutMode.AsIs;
        IReadOnlyList<string> refreshRepos = [];
        if (!target.IsNew)
        {
            if (ResolveMode(console, s, interactive, target.Workspace!) is not { } m)
            {
                console.MarkupLine("[yellow]cancelled[/]");
                return 0;
            }
            (mode, refreshRepos) = (m.Mode, m.Repos);
        }

        var label = ResolveLabel(console, s, interactive);
        var existing = target.IsNew ? null : target.Workspace;

        // Machine path stays quiet and structured: no checklist, just the record.
        if (s.Json)
        {
            CliOutput.Json(cli.Pools.Checkout(stackName, existing, label, mode, refreshRepos, s.Force));
            return 0;
        }

        // Human path: plan up front, then drive the live checklist while the checkout runs — the same
        // feedback create gives (worktrees, dependency install, infra), for every mode.
        console.MarkupLine($"[bold]Checking out[/] from [green]{Markup.Escape(stackName)}[/]…");
        var plan = cli.Pools.PlanCheckout(stackName, existing, mode, refreshRepos);
        InstanceRecord record = null!;
        Checklist.Run(console, plan, progress =>
            record = cli.Pools.Checkout(stackName, existing, label, mode, refreshRepos, s.Force, progress));

        console.MarkupLine($"[green]{Glyph.Check(console)}[/] checked out [bold]{Markup.Escape(record.Workspace)}[/] " +
            $"[dim]({Markup.Escape(label)})[/]");
        foreach (var r in record.Repos)
            console.MarkupLine($"  [dim]{Markup.Escape(r.Name)}[/]  {Markup.Escape(r.WorktreePath)}");
        if (record.SetupFailed)
            console.MarkupLine("[yellow]note:[/] setup failed — this workspace is [yellow]degraded[/]; " +
                "finish setup in the worktree before relying on it.");
        console.MarkupLine($"  [dim]enter it with[/] [bold]sprig cd {Markup.Escape(record.Workspace)}[/]");
        return 0;
    }

    string? PickStack(IAnsiConsole console, bool interactive)
    {
        var all = cli.Stacks.List();
        if (all.Count == 0)
            throw new ArgumentException("no stacks defined — create one with 'sprig stack create' first");
        if (!interactive)
            throw new ArgumentException("checkout requires a stack name");
        return Term.SelectOne(console, "Check out from which [green]stack[/]? [grey](esc cancels)[/]",
            all.Select(x => x.Name));
    }

    (string? Workspace, bool IsNew)? ResolveTarget(IAnsiConsole console, PoolStatus status, Settings s, bool interactive)
    {
        if (s.Workspace is { } named) return (named, false); // reuse specific (the service validates it)
        if (s.New)
        {
            if (status.Headroom <= 0)
                throw new PoolException($"pool '{status.Stack}' is full — release one before --new");
            return (null, true);
        }

        var free = status.Workspaces.Where(w => !w.Claimed).ToList();

        if (interactive)
        {
            const string newChoice = "+ new workspace";
            var byLabel = new Dictionary<string, string>(StringComparer.Ordinal);
            var choices = new List<string>();
            foreach (var w in free)
            {
                var d = DescribeFree(w);
                byLabel[d] = w.Workspace;
                choices.Add(d);
            }
            if (status.Headroom > 0) choices.Add(newChoice);
            if (choices.Count == 0)
                throw new PoolException(
                    $"pool '{status.Stack}' is full ({status.ClaimedCount}/{status.MaxSlots} claimed) — release one first");
            if (choices.Count == 1 && free.Count == 0) return (null, true); // only option is "new"

            var pick = Term.SelectOne(console,
                $"Check out which [green]workspace[/] from [bold]{Markup.Escape(status.Stack)}[/]? [grey](esc cancels)[/]", choices);
            if (pick is null) return null;
            return pick == newChoice ? (null, true) : (byLabel[pick], false);
        }

        // Non-interactive default: reuse the least-recently-used free workspace, else build a new one,
        // else the pool is exhausted.
        if (free.Count > 0)
            return (free.OrderBy(f => f.LastUsedAt ?? DateTimeOffset.MinValue).First().Workspace, false);
        if (status.Headroom > 0) return (null, true);
        throw new PoolException($"pool '{status.Stack}' is full — release one first");
    }

    (CheckoutMode Mode, IReadOnlyList<string> Repos)? ResolveMode(IAnsiConsole console, Settings s, bool interactive, string workspace)
    {
        var explicitCount = (s.Fresh ? 1 : 0) + (s.AsIs ? 1 : 0) + (s.Refresh.Length > 0 ? 1 : 0);
        if (explicitCount > 1)
            throw new ArgumentException("choose only one of --fresh / --as-is / --refresh");
        if (s.Fresh) return (CheckoutMode.Fresh, []);
        if (s.AsIs) return (CheckoutMode.AsIs, []);
        if (s.Refresh.Length > 0) return (CheckoutMode.Refresh, CliFormat.SplitList(s.Refresh));
        if (!interactive) return (CheckoutMode.AsIs, []); // safe default: resume, discard nothing

        const string asIs = "as-is — resume where you left off";
        const string fresh = "fresh — reset all repos to base, clean DB";
        const string refresh = "refresh some repos";
        var pick = Term.SelectOne(console, $"How should [bold]{Markup.Escape(workspace)}[/] be handled? [grey](esc cancels)[/]",
            [asIs, fresh, refresh]);
        if (pick is null) return null;
        if (pick == asIs) return (CheckoutMode.AsIs, []);
        if (pick == fresh) return (CheckoutMode.Fresh, []);

        var repoNames = (cli.Workspaces.Get(workspace)?.Repos ?? []).Select(r => r.Name).ToList();
        var chosen = Term.SelectMany(console, $"Which [green]repos[/] to reset to base?", repoNames, repoNames);
        return chosen is null ? null : (CheckoutMode.Refresh, chosen);
    }

    string ResolveLabel(IAnsiConsole console, Settings s, bool interactive)
    {
        if (!string.IsNullOrWhiteSpace(s.Label)) return s.Label!.Trim();
        if (!interactive) throw new ArgumentException("checkout requires --label <label>");
        return console.Prompt(new TextPrompt<string>("Label this workspace:")
            .Validate(n => string.IsNullOrWhiteSpace(n)
                ? ValidationResult.Error("a label is required")
                : ValidationResult.Success())).Trim();
    }

    // A free workspace's picker line: name, its last label, and roughly how long ago it was released — so
    // you can pick the leftover state you want (e.g. "the one that already has product-X built").
    static string DescribeFree(InstanceRecord w)
    {
        var label = string.IsNullOrEmpty(w.Label) ? "unused" : w.Label!;
        var when = w.LastUsedAt is { } t ? $", freed {Ago(t)}" : "";
        return $"{w.Workspace}  ({label}{when})";
    }

    static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.UtcNow - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}

[Description("Release a claimed workspace back to the pool (docker down; nothing removed from disk)")]
public sealed class PoolReleaseCommand(CliContext cli) : Command<PoolReleaseCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandArgument(0, "[stack]")]
        [Description("Only list this stack's claimed workspaces (omit for all)")]
        public string? Stack { get; set; }

        [CommandOption("--workspace <name>")]
        [Description("Release this workspace (omit at a terminal to pick)")]
        public string? Workspace { get; set; }

        [CommandOption("-i|--interactive")]
        [Description("Force the interactive picker")]
        public bool Interactive { get; set; }

        [CommandOption("--no-interactive|--ni")]
        [Description("Never prompt — fail instead of asking")]
        public bool NoInteractive { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var interactive = Interactivity.Resolve(s.Interactive, s.NoInteractive, s.Json, hasPrimaryInput: s.Workspace is not null);
        var console = Term.Create();

        string workspace;
        if (s.Workspace is { } named)
            workspace = named;
        else
        {
            var claimed = cli.Pools.ClaimedWorkspaces(s.Stack);
            if (claimed.Count == 0)
                return CliOutput.Ok(s.Json, "no claimed workspaces to release", new { ok = true, released = (string?)null });
            if (!interactive)
                throw new ArgumentException("release requires --workspace (or run it at a terminal to pick)");

            var byLabel = new Dictionary<string, string>(StringComparer.Ordinal);
            var choices = claimed.Select(c =>
            {
                var d = $"{c.Workspace}  ({(string.IsNullOrEmpty(c.Label) ? "-" : c.Label)})";
                byLabel[d] = c.Workspace;
                return d;
            }).ToList();
            var pick = Term.SelectOne(console, "Release which [green]workspace[/]? [grey](esc cancels)[/]", choices);
            if (pick is null) { console.MarkupLine("[yellow]cancelled[/]"); return 0; }
            workspace = byLabel[pick];
        }

        var record = cli.Pools.Release(workspace);
        return CliOutput.Ok(s.Json,
            $"released '{record.Workspace}' (docker down; nothing removed from disk)",
            new { ok = true, workspace = record.Workspace, action = "release" });
    }
}
