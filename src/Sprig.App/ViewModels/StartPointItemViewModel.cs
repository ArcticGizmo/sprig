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
    public string When => choice.LastCommit is { } t ? RelativeTime.Format(t) : "";
}
