using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Sprig.Core.Docker;
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

    /// <summary>True when this workspace holds a subset of its stack's repos — drives the list badge.</summary>
    public bool IsPartial => Record.IsPartial;

    /// <summary>What a partial workspace left out, and which stack ports that meant skipping (empty
    /// string for a full workspace, so the label can be bound unconditionally).</summary>
    public string PartialSummary => !Record.IsPartial ? "" :
        $"without {string.Join(", ", Record.ExcludedRepos)}" +
        (Record.SkippedPorts.Count > 0
            ? $" · ports not provisioned: {string.Join(", ", Record.SkippedPorts)}"
            : "");

    /// <summary>Allocated ports as a compact ":5173 :5080" summary (empty when none).</summary>
    public string PortsSummary => string.Join(" ", Record.Ports.OrderBy(p => p.Key).Select(p => ":" + p.Value));
    public IReadOnlyList<RepoLineViewModel> Repos { get; }

    /// <summary>True when any repo declares docker infrastructure. Mirrors the core's
    /// RequireWithInfra gate so Up/Down/Reset only show when they can actually run.</summary>
    public bool HasInfra => Record.Repos.Any(r => r.ComposePaths.Count > 0);

    [ObservableProperty] private string _status;
    [ObservableProperty] private string? _drift;

    // -- docker container status (filled by the VM's status probe) -------------

    /// <summary>Live containers for this workspace's compose project, from the last probe.</summary>
    public ObservableCollection<ContainerLineViewModel> Containers { get; } = [];

    /// <summary>True once a docker probe has completed (so the section knows what to show).</summary>
    [ObservableProperty] private bool _dockerChecked;

    /// <summary>Whether docker compose was reachable on the last probe.</summary>
    [ObservableProperty] private bool _dockerAvailable;

    public bool HasContainers => Containers.Count > 0;

    /// <summary>Show the container list.</summary>
    public bool ShowContainers => HasInfra && DockerChecked && DockerAvailable && HasContainers;
    /// <summary>Docker is up but nothing is running (infra is down).</summary>
    public bool ShowNoContainers => HasInfra && DockerChecked && DockerAvailable && !HasContainers;
    /// <summary>Docker itself couldn't be reached.</summary>
    public bool ShowDockerUnavailable => HasInfra && DockerChecked && !DockerAvailable;

    /// <summary>Replace the container list from a probe result and flip the display flags.</summary>
    public void SetContainers(IReadOnlyList<ContainerStatus> statuses, bool dockerAvailable)
    {
        Containers.Clear();
        foreach (var s in statuses.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
            Containers.Add(new ContainerLineViewModel(s.Name, s.State));
        DockerAvailable = dockerAvailable;
        DockerChecked = true;
        OnPropertyChanged(nameof(HasContainers));
        OnPropertyChanged(nameof(ShowContainers));
        OnPropertyChanged(nameof(ShowNoContainers));
        OnPropertyChanged(nameof(ShowDockerUnavailable));
    }
}

/// <summary>One container row: its name and state, with a running/stopped flag for colouring.</summary>
public sealed class ContainerLineViewModel(string name, string state)
{
    public string Name { get; } = name;
    public string State { get; } = state;

    /// <summary>True when the container is up — drives the green vs. muted state colour.</summary>
    public bool Running => State.StartsWith("running", StringComparison.OrdinalIgnoreCase)
                        || State.StartsWith("up", StringComparison.OrdinalIgnoreCase);
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
        HasInfra = repo.ComposePaths.Count > 0;
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
