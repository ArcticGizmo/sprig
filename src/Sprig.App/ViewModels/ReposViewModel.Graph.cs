using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Planning;

namespace Sprig.App.ViewModels;

/// <summary>
/// The selected repo as the graph its workspaces would show, planned from its definition alone, so how its apps, infra
/// and inputs relate can be read before anything runs. Ports are left out: they're only handed out when a workspace is made.
/// </summary>
public partial class ReposViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraph))]
    private WorkspaceGraph? _graph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraphPicked))]
    private GraphNode? _graphPicked;

    /// <summary>Why it couldn't be drawn, when its definition runs but doesn't plan on its own.</summary>
    [ObservableProperty] private string _graphNote = "";

    const string NothingToDraw = "It couldn't be planned on its own, so there's nothing to draw.";

    public bool HasGraph => Graph is { Nodes.Count: > 0 };
    public bool HasGraphPicked => GraphPicked is not null;
    public bool HasGraphNote => GraphNote.Length > 0;

    partial void OnGraphNoteChanged(string value) => OnPropertyChanged(nameof(HasGraphNote));

    /// <summary>The plan the graph was drawn from, kept so a change of layout or grouping draws it again without planning.</summary>
    Plan? _graphPlan;

    async Task PreviewAsync(RepoItem repo)
    {
        // The entry's own file, which the loader reads as the repo run on its own.
        if (_services.Orchestrations.Resolve(repo.Id) is not { } entry)
        {
            GraphNote = NothingToDraw;
            return;
        }
        var settings = _services.Settings.Get();
        var (plan, note) = await AppServices.RunAsync(() =>
        {
            var loaded = _services.Loader.Load(entry);
            if (loaded.Orchestration is null || loaded.Untrusted.Count > 0) return ((Plan?)null, NothingToDraw);
            var problems = new List<PlanProblem>(loaded.Problems);
            var built = Planner.Build(new PlanInput(loaded.Orchestration, loaded.Repos, "preview", settings, new HashSet<int>(),
                ComposeFiles: loaded.ComposeFiles(problems)));
            var error = problems.Concat(built.Problems).FirstOrDefault(p => p.Severity == ProblemSeverity.Error);
            return (built, error is null ? "" : $"It doesn't plan on its own yet, so the graph may be missing parts: {error.Message}.");
        });
        if (Selected?.Id != repo.Id) return;
        _graphPlan = plan;
        GraphNote = note;
        DrawGraph(repo.Id);
    }

    void ClearGraph()
    {
        _graphPlan = null;
        GraphNote = "";
        SwapGraph(null);
    }

    void DrawGraph(string id)
    {
        if (_graphPlan is not { } plan)
        {
            SwapGraph(null);
            return;
        }
        var topology = WorkspaceTopology.Of(plan);
        var nodes = topology.Nodes.Select(n => new GraphNode(Unported(n), null, null) { Preview = true }).ToArray();
        _startGroups = plan.Groups;
        OnPropertyChanged(nameof(CanGroupByStartGroup));
        NotifyGraphGrouping();
        // Pins are kept by the ID its workspaces run under, so a node dragged here sits in the same place there.
        SwapGraph(new WorkspaceGraph(nodes, topology.Links, _services.Ui.GraphPlaces(id), layered: !GraphForce,
            grouping: GraphGroupBy, startGroups: plan.Groups));
    }

    /// <summary>A port the plan made up only to have one: a workspace is given its own when it's made.</summary>
    static TopologyNode Unported(TopologyNode node)
        => node.IsRunner || (node.Kind == TopologyKind.ComposeService && node.Detail.StartsWith(':')) ? node with { Detail = "" } : node;

    void SwapGraph(WorkspaceGraph? graph)
    {
        if (Graph is { } old)
        {
            old.Picked -= OnGraphPicked;
            old.PinsChanged -= OnGraphPinned;
        }
        GraphPicked = null;
        if (graph is not null)
        {
            graph.Picked += OnGraphPicked;
            graph.PinsChanged += OnGraphPinned;
        }
        Graph = graph;
    }

    void OnGraphPicked(GraphNode? node) => GraphPicked = node;

    void OnGraphPinned()
    {
        if (Graph is { } graph && Selected is { } repo) _services.Ui.SetGraphPlaces(repo.Id, graph.Pins());
    }

    void Redraw()
    {
        if (Selected is { } repo) DrawGraph(repo.Id);
    }

    IReadOnlyList<PlannedGroup> _startGroups = [];

    public bool CanGroupByStartGroup => _startGroups.Count > 0;

    /// <summary>Laid out by forces, where nodes can be dragged; otherwise in rows. Shared with the workspace graph.</summary>
    public bool GraphForce
    {
        get => _services.Ui.GraphForce;
        set
        {
            if (value == _services.Ui.GraphForce) return;
            _services.Ui.GraphForce = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GraphLayered));
            Redraw();
        }
    }

    public bool GraphLayered => !GraphForce;

    /// <summary>What the boxes gather, shared with the workspace graph. Start group falls back to repo for a definition
    /// that names none.</summary>
    public GraphGrouping GraphGroupBy
    {
        get => _services.Ui.GraphGroupBy is GraphGrouping.StartGroup && !CanGroupByStartGroup ? GraphGrouping.Repo : _services.Ui.GraphGroupBy;
        set
        {
            if (value == GraphGroupBy) return;
            _services.Ui.GraphGroupBy = value;
            NotifyGraphGrouping();
            Redraw();
        }
    }

    public bool GraphByRepo => GraphGroupBy == GraphGrouping.Repo;
    public bool GraphByStartGroup => GraphGroupBy == GraphGrouping.StartGroup;
    public bool GraphByRole => GraphGroupBy == GraphGrouping.Role;

    void NotifyGraphGrouping()
    {
        OnPropertyChanged(nameof(GraphGroupBy));
        OnPropertyChanged(nameof(GraphByRepo));
        OnPropertyChanged(nameof(GraphByStartGroup));
        OnPropertyChanged(nameof(GraphByRole));
    }

    [RelayCommand]
    private void GroupGraphByRepo() => GraphGroupBy = GraphGrouping.Repo;

    [RelayCommand]
    private void GroupGraphByStartGroup() => GraphGroupBy = GraphGrouping.StartGroup;

    [RelayCommand]
    private void GroupGraphByRole() => GraphGroupBy = GraphGrouping.Role;

    [RelayCommand]
    private void ShowLayeredGraph() => GraphForce = false;

    [RelayCommand]
    private void ShowForceGraph() => GraphForce = true;

    [RelayCommand]
    private void FitGraph() => Graph?.Fit();

    /// <summary>Forget where nodes were dragged, here and in this repo's workspaces, and lay it out afresh.</summary>
    [RelayCommand]
    private void ResetGraphLayout()
    {
        if (Selected is not { } repo) return;
        _services.Ui.SetGraphPlaces(repo.Id, new Dictionary<string, (double, double)>());
        DrawGraph(repo.Id);
    }
}
