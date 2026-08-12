using Sprig.Core.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>One row in the "start from" picker: the ref plus the display bits the dropdown shows — a relative
/// "when" for recency, and the two chips (a likely default like main/master, and the repo's current branch).</summary>
public sealed class StartPointItemViewModel(StartPointChoice choice)
{
    public StartPointChoice Choice => choice;
    public string Ref => choice.Ref;

    /// <summary>Show a "default"-style chip (this is main/master — most likely what you want).</summary>
    public bool IsDefault => choice.IsDefaultBranch;

    /// <summary>Show a "current" chip (this is the branch you're on now).</summary>
    public bool IsCurrent => choice.IsCurrent;

    /// <summary>A short relative age of the branch's tip commit, e.g. "3d ago" — empty when unknown.</summary>
    public string When => choice.LastCommit is { } t ? Relative(t) : "";

    static string Relative(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
        return $"{(int)(d.TotalDays / 30)}mo ago";
    }
}
