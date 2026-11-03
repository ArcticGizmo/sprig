using System.Collections.Generic;

namespace Sprig.App.ViewModels;

/// <summary>The front page of the experimental build: what it is and how far the roadmap has got.</summary>
public sealed class HomeViewModel : PageViewModel
{
    public override string Title => "Home";

    /// <summary>Mirrors docs/experimental-roadmap.md. Update the status here when a milestone lands.</summary>
    public IReadOnlyList<MilestoneItem> Milestones { get; } =
    [
        new("M0", "Reset the branch", "A clean skeleton that builds, keeping the app shell.", MilestoneStatus.Done),
        new("M1", "Spike C# authoring", "Prove sprig.cs definitions are secure, stable, EDR-friendly and editable.", MilestoneStatus.Done),
        new("M2", "Test system", "A Vue app, two .NET APIs and an Ionic app, built as real git repos.", MilestoneStatus.Done),
        new("M3", "Primitives", "Variables, sharing and infra links, with sprig plan showing every resolved value.", MilestoneStatus.Done),
        new("M4", "One-step up", "Worktrees, ports, env files and containers from a single command.", MilestoneStatus.Next),
        new("M5", "Runners", "Launch apps in order, with dependencies and a log pane per app.", MilestoneStatus.Planned),
        new("M6", "Review", "Compare with Aspire and decide whether this replaces main.", MilestoneStatus.Planned),
    ];
}

public enum MilestoneStatus { Done, Next, Planned }

public sealed record MilestoneItem(string Id, string Name, string Summary, MilestoneStatus Status)
{
    public bool IsDone => Status == MilestoneStatus.Done;
    public bool IsNext => Status == MilestoneStatus.Next;
    public string StatusLabel => Status switch
    {
        MilestoneStatus.Done => "DONE",
        MilestoneStatus.Next => "NEXT",
        _ => "PLANNED",
    };
}
