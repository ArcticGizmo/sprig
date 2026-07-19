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
    public IReadOnlyList<RepoLineViewModel> Repos { get; }

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

    [ObservableProperty] private string _state = "";
}
