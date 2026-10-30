using Sprig.Core.Git;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;
using Spectre.Console;

namespace Sprig.Cli;

/// <summary>Presentation helpers shared across commands: the compact strings the human output has always
/// used, the <c>--bind</c> parsing/merging, and the Spectre table builder that the list commands render
/// through. Kept apart from the command classes so the look stays consistent in one place.</summary>
static class CliFormat
{
    public static string Ports(IReadOnlyDictionary<string, int> ports)
        => ports.Count == 0 ? "-" : string.Join(",", ports.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

    public static string Kv(IReadOnlyDictionary<string, string> kv)
        => kv.Count == 0 ? "-" : string.Join("  ", kv.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

    /// <summary>A plain (borderless-by-caller) table with dim, upper-cased headers — the shared look for
    /// every list command. Callers add columns and rows, then hand it to the console.</summary>
    public static Table Table(params string[] headers)
    {
        var table = new Table().Border(TableBorder.Rounded);
        foreach (var h in headers)
            table.AddColumn(new TableColumn($"[bold]{h}[/]"));
        return table;
    }

    /// <summary>A coloured badge for a worktree's reconciliation state — green when healthy, amber for the
    /// repairable drifts, red when the folder's missing, dim once it's gone. Shared by <c>info</c> and
    /// <c>reconcile</c> so a state reads the same wherever it appears.</summary>
    public static string StateBadge(WorktreeState state) => state switch
    {
        WorktreeState.Healthy => "[green]healthy[/]",
        WorktreeState.MissingFolder => "[red]missing folder[/]",
        WorktreeState.Orphaned => "[yellow]orphaned[/]",
        WorktreeState.Gone => "[dim]gone[/]",
        _ => $"[dim]{state}[/]",
    };

    /// <summary>A coloured badge for a worktree shortcut's state, in the same palette as <see cref="StateBadge"/>.</summary>
    public static string LinkBadge(LinkState state) => state switch
    {
        LinkState.Ok => "[green]ok[/]",
        LinkState.Missing => "[yellow]missing[/]",
        LinkState.WrongTarget => "[yellow]points elsewhere[/]",
        LinkState.Dangling => "[yellow]dangling[/]",
        LinkState.Blocked => "[red]blocked by a real folder[/]",
        _ => "[dim]none[/]",
    };

    /// <summary>A worktree's live HEAD as markup: the branch, <c>detached @ abc1234</c>, or a dim <c>?</c> when
    /// it can't be read.</summary>
    public static string Head(WorktreeHead? head) => head switch
    {
        { Branch: { } b } => Markup.Escape(b),
        { Commit: { } c } => $"[dim]detached @ {Markup.Escape(c[..Math.Min(7, c.Length)])}[/]",
        _ => "[dim]?[/]",
    };

    /// <summary>The live branch across a workspace's repos: one value when they agree (the usual case), else
    /// <c>repo: branch</c> per repo.</summary>
    public static string Heads(IReadOnlyList<(string Repo, WorktreeHead? Head)> heads)
    {
        if (heads.Count == 0) return "[dim]-[/]";
        var distinct = heads.Select(h => Head(h.Head)).Distinct().ToList();
        return distinct.Count == 1
            ? distinct[0]
            : string.Join(", ", heads.Select(h => $"{Markup.Escape(h.Repo)}: {Head(h.Head)}"));
    }

    /// <summary>A workspace's headline: its label, else its live branch (dim) — with the note beneath.</summary>
    public static string Purpose(InstanceRecord record, string liveBranch)
    {
        var headline = string.IsNullOrWhiteSpace(record.Label) ? $"[dim]{liveBranch}[/]" : $"[bold]{Markup.Escape(record.Label)}[/]";
        return string.IsNullOrWhiteSpace(record.Note) ? headline : $"{headline}\n[dim]{Markup.Escape(record.Note)}[/]";
    }

    public static int ParsePort(string value, string flag)
        => int.TryParse(value, out var n) ? n : throw new ArgumentException($"{flag} must be a number, got '{value}'");

    /// <summary>Split repeated <c>--flag a,b --flag c</c> values into a flat list, honouring both the
    /// comma and repeated-flag forms the old parser accepted.</summary>
    public static List<string> SplitList(IEnumerable<string> values)
        => values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
}
