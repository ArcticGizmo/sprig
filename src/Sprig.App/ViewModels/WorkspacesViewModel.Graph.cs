using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Compose;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Processes;
using Sprig.Engine.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>
/// The selected workspace as a graph: every app, job and container, linked by the values one takes from another, each
/// showing its state, with its log and start/stop a click away. Apps' states come from the host's poll; containers'
/// from docker, asked every few seconds while the graph (or a container's log) is showing.
/// </summary>
public partial class WorkspacesViewModel
{
    static readonly TimeSpan InfraPollEvery = TimeSpan.FromSeconds(3);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraph), nameof(GraphEmpty))]
    private WorkspaceGraph? _graph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraphPicked))]
    private GraphNode? _graphPicked;

    public bool HasGraph => Graph is { Nodes.Count: > 0 };
    public bool GraphEmpty => !HasGraph;
    public bool HasGraphPicked => GraphPicked is not null;
    public bool OnGraphTab => Tab == WorkspaceTab.Graph;

    IReadOnlyList<InfraTarget> _targets = [];
    readonly Dictionary<string, ContainerLogFollower> _followers = new(StringComparer.Ordinal);
    DateTime _lastInfraPoll = DateTime.MinValue;
    bool _infraPolling;
    bool _infraHeld;

    /// <summary>The graph for <paramref name="record"/>'s workspace; called before its logs are set, since containers'
    /// logs are followed through what this makes.</summary>
    /// <param name="same">The same workspace read afresh, which keeps its containers' logs and the picked node.</param>
    void BuildGraph(WorkspaceRecord? record, Plan? plan, bool same)
    {
        var picked = same ? GraphPicked?.Key : null;
        if (Graph is { } old)
        {
            old.Picked -= OnGraphPicked;
            old.LogRequested -= OnGraphLog;
            old.PinsChanged -= OnGraphPinned;
        }
        if (record is null || plan is null)
        {
            _targets = [];
            CloseFollowers();
            Graph = null;
            GraphPicked = null;
            return;
        }

        _targets = _services.Workspaces.InfraTargets(record, plan);
        var keep = same ? _targets.Where(t => _followers.TryGetValue(t.Key, out var f) && f.File == Follower(t).File).Select(t => t.Key).ToHashSet() : [];
        foreach (var gone in _followers.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            _followers[gone].Dispose();
            _followers.Remove(gone);
        }
        foreach (var target in _targets.Where(t => !_followers.ContainsKey(t.Key))) _followers[target.Key] = Follower(target);

        var topology = WorkspaceTopology.Of(plan);
        var nodes = topology.Nodes.Select(n => new GraphNode(n, Apps.FirstOrDefault(a => a.Key == n.Key), _targets.FirstOrDefault(t => t.Key == n.Key))).ToArray();
        var graph = new WorkspaceGraph(nodes, topology.Links, _services.Ui.GraphPlaces(record.Orchestration), layered: !GraphForce);
        graph.Picked += OnGraphPicked;
        graph.LogRequested += OnGraphLog;
        graph.PinsChanged += OnGraphPinned;
        Graph = graph;
        // Another workspace starts with nothing looked for; the same one, read afresh, keeps the search.
        if (!same) GraphSearch = "";
        graph.SetSearch(GraphSearch);
        _graphMatch = -1;
        CaptionSearch();
        if (!same)
        {
            _lastInfra = null;
            _lastInfraPoll = DateTime.MinValue;
        }
        else if (_lastInfra is { } states) ApplyInfraStates(states);
        GraphPicked = null;
        if (graph.Find(picked) is { } again) graph.Select(again.Key);
    }

    static ContainerLogFollower Follower(InfraTarget target) => new(target.Project.Name, target.Service);

    void CloseFollowers()
    {
        foreach (var follower in _followers.Values) follower.Dispose();
        _followers.Clear();
    }

    /// <summary>The containers' logs, for the log pane.</summary>
    IReadOnlyList<(string App, ILogSource Source)> InfraLogSources() => _followers.Select(f => (f.Key, (ILogSource)f.Value)).ToArray();

    void OnGraphPicked(GraphNode? node) => GraphPicked = node;

    void OnGraphLog(GraphNode node) => OpenNodeLog(node);

    void OnGraphPinned()
    {
        if (Graph is { } graph && Selected?.Record is { } record) _services.Ui.SetGraphPlaces(record.Orchestration, graph.Pins());
    }

    /// <summary>After the host's answer, the app nodes say the same as their rows.</summary>
    void RefreshGraph()
    {
        if (Graph is not { } graph) return;
        foreach (var node in graph.Nodes.Where(n => n.IsRunner)) node.Refresh();
        graph.Invalidate();
    }

    IReadOnlyDictionary<string, ComposeService>? _lastInfra;

    /// <summary>A container's log is the one showing, so its state is worth keeping current too.</summary>
    bool InfraLogOpen => !Logs.IsAll && _followers.ContainsKey(Logs.Key);

    /// <summary>Ask docker how the containers are doing, when the graph or a container's log needs it and it's been a
    /// few seconds; a follow that ended because its container stopped starts again once the container runs.</summary>
    async Task PollInfraAsync(bool now = false)
    {
        if (_infraHeld || _infraPolling || _targets.Count == 0 || !(OnGraphTab || InfraLogOpen)) return;
        if (!now && DateTime.UtcNow - _lastInfraPoll < InfraPollEvery) return;
        _infraPolling = true;
        try
        {
            var targets = _targets;
            var states = await AppServices.RunAsync(() => _services.Workspaces.InfraStates(targets));
            _lastInfraPoll = DateTime.UtcNow;
            if (!ReferenceEquals(targets, _targets)) return;
            ApplyInfraStates(states);
        }
        finally
        {
            _infraPolling = false;
        }
    }

    /// <summary>Show containers' states; null when docker didn't answer.</summary>
    internal void ApplyInfraStates(IReadOnlyDictionary<string, ComposeService>? states)
    {
        _lastInfra = states;
        if (Graph is { } graph)
        {
            foreach (var node in graph.Nodes.Where(n => n.IsInfra)) node.Apply(states?.GetValueOrDefault(node.Key), states is not null);
            graph.Invalidate();
        }
        if (InfraLogOpen && states?.GetValueOrDefault(Logs.Key) is { State: "running" or "restarting" }) _followers[Logs.Key].EnsureRunning();
        OnPropertyChanged(nameof(LogSub));
    }

    /// <summary>For a rendered sample: these states stand, and docker isn't asked.</summary>
    internal void HoldInfraStates(IReadOnlyDictionary<string, ComposeService> states)
    {
        _infraHeld = true;
        ApplyInfraStates(states);
    }

    [RelayCommand]
    private async Task ShowGraphTab()
    {
        Tab = WorkspaceTab.Graph;
        await PollInfraAsync(now: true);
    }

    /// <summary>What to look for in the graph: a name, repo, port or image, or infra used.</summary>
    [ObservableProperty] private string _graphSearch = "";

    /// <summary>How many it found, and which one Enter last went to.</summary>
    [ObservableProperty] private string _graphSearchCaption = "";

    int _graphMatch = -1;

    partial void OnGraphSearchChanged(string value)
    {
        _graphMatch = -1;
        Graph?.SetSearch(value);
        CaptionSearch();
    }

    void CaptionSearch()
    {
        var found = Graph?.Matches.Count ?? 0;
        GraphSearchCaption = GraphSearch.Trim().Length == 0 ? ""
            : found == 0 ? "none found"
            : _graphMatch < 0 ? (found == 1 ? "1 found · Enter to go to it" : $"{found} found · Enter to go through them")
            : $"{_graphMatch + 1} of {found}";
    }

    /// <summary>Go to the next node the search found, picking it and bringing it into view; Shift goes back.</summary>
    [RelayCommand]
    private void NextGraphMatch() => StepGraphMatch(1);

    [RelayCommand]
    private void PreviousGraphMatch() => StepGraphMatch(-1);

    void StepGraphMatch(int by)
    {
        if (Graph is not { Matches.Count: > 0 } graph) return;
        _graphMatch = _graphMatch < 0 ? (by > 0 ? 0 : graph.Matches.Count - 1) : (_graphMatch + by + graph.Matches.Count) % graph.Matches.Count;
        graph.Reveal(graph.Matches[_graphMatch]);
        CaptionSearch();
    }

    [RelayCommand]
    private void ClearGraphSearch() => GraphSearch = "";

    /// <summary>Laid out by forces, where nodes can be dragged; otherwise in rows, what uses something above it.</summary>
    public bool GraphForce
    {
        get => _services.Ui.GraphForce;
        set
        {
            if (value == _services.Ui.GraphForce) return;
            _services.Ui.GraphForce = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GraphLayered));
            if (Selected?.Record is { } record && _plan is not null) BuildGraph(record, _plan, same: true);
        }
    }

    public bool GraphLayered => !GraphForce;

    [RelayCommand]
    private void ShowLayeredGraph() => GraphForce = false;

    [RelayCommand]
    private void ShowForceGraph() => GraphForce = true;

    [RelayCommand]
    private void FitGraph() => Graph?.Fit();

    /// <summary>Forget where nodes were dragged, for this orchestration, and lay it out afresh.</summary>
    [RelayCommand]
    private void ResetGraphLayout()
    {
        if (Selected?.Record is not { } record || _plan is null) return;
        _services.Ui.SetGraphPlaces(record.Orchestration, new Dictionary<string, (double, double)>());
        BuildGraph(record, _plan, same: true);
    }

    [RelayCommand]
    private void CloseGraphCard() => Graph?.Select(null);

    [RelayCommand]
    private void GraphLog(GraphNode node) => OpenNodeLog(node);

    /// <summary>Its log, on the Apps tab: an app's from the host, a container's from docker.</summary>
    public void OpenNodeLog(GraphNode node)
    {
        if (node.App is { } row)
        {
            OpenApp(row);
            return;
        }
        if (!_followers.TryGetValue(node.Key, out var follower)) return;
        follower.EnsureRunning();
        Tab = WorkspaceTab.Apps;
        Logs.Show(node.Key);
        MarkSelected();
    }

    [RelayCommand]
    private void GraphOpen(GraphNode node)
    {
        if (node.HasUrl) Browser.Open(node.Url);
    }

    /// <summary>Start it, or restart it when it's going. An app starts after what it depends on; a container that
    /// doesn't exist yet comes with the rest of the workspace's infra, by bringing it up.</summary>
    [RelayCommand]
    private async Task GraphStart(GraphNode node)
    {
        if (node.App is { } row)
        {
            await StartAsync([row.Key]);
            return;
        }
        if (node.Target is not { } target || Selected?.Record is not { } record) return;
        if (record.State != WorkspaceState.Up || node.State == "not created")
        {
            await UpAsync(retry: () => GraphStart(node));
            return;
        }
        var restart = node.IsAlive;
        node.Pending(restart ? "restarting" : "starting");
        Graph?.Invalidate();
        try
        {
            await AppServices.RunAsync(() =>
            {
                if (restart) _services.Workspaces.StopContainer(target);
                _services.Workspaces.StartContainer(target);
            });
            SayApps($"{(restart ? "Restarted" : "Started")} {node.Name}.", bad: false);
        }
        catch (ProcessException e)
        {
            SayApps($"{node.Name} didn't {(restart ? "restart" : "start")}: {WorkspacesViewModel.Headline(e.Message)}", bad: true);
        }
        await PollInfraAsync(now: true);
    }

    /// <summary>Stop it. A container keeps its data; what uses it will notice it's gone.</summary>
    [RelayCommand]
    private async Task GraphStop(GraphNode node)
    {
        if (node.App is { } row)
        {
            await StopAsync([row]);
            return;
        }
        if (node.Target is not { } target) return;
        node.Pending("stopping");
        Graph?.Invalidate();
        try
        {
            await AppServices.RunAsync(() => _services.Workspaces.StopContainer(target));
            SayApps($"{node.Name} has stopped. Its data is kept; Start brings it back.", bad: false);
        }
        catch (ProcessException e)
        {
            SayApps($"{node.Name} didn't stop: {WorkspacesViewModel.Headline(e.Message)}", bad: true);
        }
        await PollInfraAsync(now: true);
    }
}
