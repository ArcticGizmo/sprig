using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Hosting;
using Sprig.Engine.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>The front page: what's running now, each workspace a click from its apps, and the first steps until a repo
/// is on the Repos list.</summary>
public sealed partial class HomeViewModel : PageViewModel
{
    readonly AppServices _services;

    public override string Title => "Home";

    public HomeViewModel(AppServices services)
    {
        _services = services;
        // Dots follow the window's host poll; the list itself is read again when the page is shown.
        services.HostStatus.Changed += runners =>
        {
            if (IsActive) Running = Build(_services.Workspaces.List(), runners);
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NothingRunning))]
    private IReadOnlyList<RunningWorkspace> _running = [];

    public bool NothingRunning => Running.Count == 0;

    /// <summary>The steps show until there's a repo, which is the first of them.</summary>
    [ObservableProperty] private bool _showSteps = true;

    /// <summary>The Repos page's + Add, wired by the window.</summary>
    public Action? AddRepo { get; set; }

    /// <summary>A workspace on the Workspaces page, by key, wired by the window.</summary>
    public Action<string>? OpenWorkspace { get; set; }

    [RelayCommand]
    private void StartAddRepo() => AddRepo?.Invoke();

    [RelayCommand]
    private void Open(RunningWorkspace workspace) => OpenWorkspace?.Invoke(workspace.Key);

    protected override void OnActivated() => Refresh();

    protected override void OnWindowFocused() => Refresh();

    public void Refresh()
    {
        Running = Build(_services.Workspaces.List(), _services.HostStatus.Runners);
        ShowSteps = _services.Stored.Repos().Count == 0;
    }

    /// <summary>Workspaces that are up, or have an app going, with their apps (jobs left out) and each one's state.</summary>
    IReadOnlyList<RunningWorkspace> Build(IReadOnlyList<WorkspaceRecord> records, IReadOnlyList<RunnerStatus>? runners)
    {
        var byWorkspace = (runners ?? []).GroupBy(r => r.Workspace).ToDictionary(g => g.Key, g => g.ToArray());
        var running = new List<RunningWorkspace>();
        foreach (var record in records.OrderBy(r => r.Orchestration, StringComparer.Ordinal).ThenBy(r => r.IsMain ? 0 : 1).ThenBy(r => r.SlotName, StringComparer.Ordinal))
        {
            var mine = byWorkspace.GetValueOrDefault(record.Key) ?? [];
            if (record.State != WorkspaceState.Up && !mine.Any(r => r.State.IsAlive())) continue;
            var jobs = _services.Workspaces.LastPlan(record.Orchestration, record.Name)?.Apps.Where(a => a.Job).Select(a => a.Key).ToHashSet() ?? [];
            var apps = mine.Where(r => !jobs.Contains(r.Name)).OrderBy(r => r.Name, StringComparer.Ordinal)
                .Select(r => new RunningApp(r.Name[(r.Name.IndexOf('/') + 1)..], r.State)).ToArray();
            running.Add(new RunningWorkspace(record.Key, WorkspaceItem.Display(record), WorkspaceItem.BranchOf(record), apps));
        }
        return running;
    }
}

public sealed record RunningWorkspace(string Key, string Title, string Branch, IReadOnlyList<RunningApp> Apps)
{
    public bool HasApps => Apps.Count > 0;
}

/// <summary>An app and what its dot says: going, on its way, or in trouble; anything else (stopped) is plain.</summary>
public sealed record RunningApp(string Name, RunnerState State)
{
    public bool IsOk => State is RunnerState.Ready or RunnerState.Running or RunnerState.Completed;
    public bool IsBusy => State is RunnerState.Building or RunnerState.Waiting or RunnerState.Starting or RunnerState.Restarting or RunnerState.Stopping;
    public bool IsBad => State is RunnerState.Failed or RunnerState.Exited;
    public string Tip => State.ToString().ToLowerInvariant();
}
