using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>One workspace in the list/detail. Wraps an <see cref="InstanceRecord"/>; drift is filled by reconcile.</summary>
public partial class WorkspaceItemViewModel : ViewModelBase
{
    public InstanceRecord Record { get; }

    public WorkspaceItemViewModel(InstanceRecord record)
    {
        Record = record;
        Repos = record.Repos.Select(r => new RepoLineViewModel(r)).ToList();
        _status = record.LastStatus ?? "created";
    }

    public string Name => Record.Workspace;
    public string Stack => Record.Stack ?? "(ad-hoc)";
    public string ReposSummary => string.Join(", ", Record.Repos.Select(r => r.Name));

    /// <summary>Allocated ports as a compact ":5173 :5080" summary (empty when none).</summary>
    public string PortsSummary => string.Join(" ", Record.Ports.OrderBy(p => p.Key).Select(p => ":" + p.Value));
    public IReadOnlyList<RepoLineViewModel> Repos { get; }

    /// <summary>True when any repo declares docker infrastructure. Mirrors the core's
    /// RequireWithInfra gate so Up/Down/Reset only show when they can actually run.</summary>
    public bool HasInfra => Record.Repos.Any(r => r.GeneratedComposePath is not null);

    [ObservableProperty] private string _status;
    [ObservableProperty] private string? _drift;
}

/// <summary>One repo row inside a workspace's detail.</summary>
public partial class RepoLineViewModel : ViewModelBase
{
    public RepoLineViewModel(InstanceRepo repo)
    {
        Name = repo.Name;
        Branch = repo.Branch ?? "";
        WorktreePath = repo.WorktreePath;
        Inputs = repo.Inputs.Count == 0
            ? "-"
            : string.Join("  ", repo.Inputs.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));
        HasInfra = repo.GeneratedComposePath is not null;
    }

    public string Name { get; }
    public string Branch { get; }
    public string WorktreePath { get; }
    public string Inputs { get; }
    public bool HasInfra { get; }

    /// <summary>Per-repo worktree state from the last reconcile; null until one runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(StateKnown), nameof(StateHealthy), nameof(StateProblem))]
    private WorktreeState? _driftState;

    public bool StateKnown => DriftState is not null;
    public bool StateHealthy => DriftState == WorktreeState.Healthy;
    public bool StateProblem => StateKnown && !StateHealthy;

    /// <summary>Plain-language description of <see cref="DriftState"/> (what it means + the fix).</summary>
    public string StateLabel => DriftState switch
    {
        WorktreeState.Healthy => "✓ in sync",
        WorktreeState.MissingFolder => "worktree folder missing — run Repair to prune it",
        WorktreeState.Orphaned => "orphaned folder (git lost track) — run Repair to remove it",
        WorktreeState.Gone => "gone — no worktree on disk or in git",
        _ => "",
    };
}
