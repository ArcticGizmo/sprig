using System.ComponentModel;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig ws switch` — move a workspace onto new work without waiting for the old branch to be tidied up. The
// branch it leaves is never deleted, reset or pushed; that's for git, the forge or the caller.

[Description("Move a workspace to a new branch, an existing branch, or a detached ref (the old branch is kept)")]
public sealed class SwitchCommand(CliContext cli) : Command<SwitchCommand.Settings>
{
    public sealed class Settings : WorkspaceSettings
    {
        [CommandOption("--new <branch>")]
        [Description("Cut a new branch (from --from, default each repo's base)")]
        public string? New { get; set; }

        [CommandOption("--branch <branch>")]
        [Description("Check out an existing branch, local or on a remote")]
        public string? Branch { get; set; }

        [CommandOption("--detach")]
        [Description("Park at --from (default each repo's base) with no branch")]
        public bool Detach { get; set; }

        [CommandOption("--from <ref>")]
        [Description("Start point for --new, or where --detach parks")]
        public string? From { get; set; }

        [CommandOption("--only <repos>")]
        [Description("Only switch these repos (comma-separated or repeated)")]
        public string[] Only { get; set; } = [];

        [CommandOption("--fresh")]
        [Description("Also wipe data volumes and recreate infra")]
        public bool Fresh { get; set; }

        [CommandOption("--no-interactive|--ni")]
        [Description("Never prompt — fail instead of asking")]
        public bool NoInteractive { get; set; }
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        // A named workspace with no mode flag still offers the branch picker at a terminal.
        var interactive = Interactivity.Resolve(false, s.NoInteractive, s.Json,
            hasPrimaryInput: s.New is not null || s.Branch is not null || s.Detach);
        var workspace = WorkspacePrompt.Resolve(cli, s.Workspace, s.Json || !interactive);
        if (workspace is null) return 0; // interactive cancel (ESC)
        var record = cli.Workspaces.Get(workspace) ?? throw new ArgumentException($"unknown workspace '{workspace}'");
        var only = CliFormat.SplitList(s.Only);

        if (ResolveTarget(s, record, interactive) is not { } target)
        {
            cli.Ansi.MarkupLine("[yellow]cancelled[/]");
            return 0;
        }

        if (s.Json)
        {
            var result = cli.Workspaces.Switch(workspace, target, only, s.Fresh);
            CliOutput.Json(new
            {
                ok = true,
                workspace,
                action = "switch",
                branch = target.Branch,
                record = LiveJson.Record(result.Record),
                previous = result.Previous.Repos.Where(r => r.HasAny).Select(r => new { repo = r.Repo, unpushed = r.UnpushedCommits }),
            });
            return 0;
        }

        var console = Term.Create();
        var previousBranch = record.Branch;
        console.MarkupLine($"[bold]Switching[/] [green]{Markup.Escape(workspace)}[/]…");
        SwitchResult done = null!;
        Checklist.Run(console, cli.Workspaces.PlanSwitch(record, target, only, s.Fresh), progress =>
            done = cli.Workspaces.Switch(workspace, target, only, s.Fresh, progress));

        var where = target.Branch is { } b ? $"branch [green]{Markup.Escape(b)}[/]" : $"detached at [green]{Markup.Escape(s.From ?? "base")}[/]";
        console.MarkupLine($"[green]{Glyph.Check(console)}[/] switched [bold]{Markup.Escape(workspace)}[/] to {where}");
        if (done.Previous.Repos.Any(r => r.UnpushedCommits > 0))
        {
            console.MarkupLine($"[yellow]note:[/] {(previousBranch is null ? "the previous HEAD" : $"[green]{Markup.Escape(previousBranch)}[/]")} " +
                "still has unpushed commits (kept, not touched):");
            foreach (var r in done.Previous.Repos.Where(r => r.UnpushedCommits > 0))
                console.MarkupLine($"  [dim]{Markup.Escape(r.Repo)}[/]  {r.UnpushedCommits} unpushed commit{(r.UnpushedCommits == 1 ? "" : "s")}");
        }
        if (done.Record.SetupFailed)
            console.MarkupLine("[yellow]note:[/] a setup command failed — finish setup manually in the worktree.");
        return 0;
    }

    // Exactly one of --new / --branch / --detach. At a terminal with none, pick an existing branch.
    SwitchTarget? ResolveTarget(Settings s, InstanceRecord record, bool interactive)
    {
        var modes = new[] { s.New is not null, s.Branch is not null, s.Detach }.Count(m => m);
        if (modes > 1) throw new ArgumentException("choose only one of --new / --branch / --detach");
        if (s.From is not null && s.Branch is not null)
            throw new ArgumentException("--from applies to --new and --detach, not --branch");

        if (s.New is { } n) return SwitchTarget.NewBranch(n.Trim(), s.From);
        if (s.Branch is { } b) return SwitchTarget.ExistingBranch(b.Trim());
        if (s.Detach) return SwitchTarget.Detach(s.From);
        if (!interactive) throw new ArgumentException("switch needs one of --new <branch>, --branch <branch> or --detach");

        var path = record.Repos.FirstOrDefault()?.WorktreePath
            ?? throw new ArgumentException($"workspace '{record.Workspace}' has no repos");
        var options = cli.Workspaces.StartPoints(record.Repos.Select(r => r.WorktreePath).ToList(), fetch: true);
        var pick = Term.SelectOne(Term.Create(), "Check out which [green]branch[/]? [grey](esc cancels)[/]",
            options.Candidates.Select(c => c.Ref));
        if (pick is null) return null;
        // A remote-tracking pick ("origin/feat-x") names the branch "feat-x"; a local one is used as-is.
        var branch = cli.Git.BranchExists(path, pick) || !pick.Contains('/') ? pick : pick[(pick.IndexOf('/') + 1)..];
        return SwitchTarget.ExistingBranch(branch);
    }
}
