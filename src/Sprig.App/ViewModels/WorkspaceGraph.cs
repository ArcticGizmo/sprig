using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Sprig.Engine.Compose;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>How a node is doing, as one colour: <see cref="Unknown"/> for what sprig doesn't run. <see cref="Waiting"/>
/// is held back by what it depends on, apart from <see cref="Busy"/> building or starting, as the apps list shows them.</summary>
public enum NodeHealth { Good, Busy, Waiting, Bad, Off, Unknown }

/// <summary>One app, job, container or outside value in the workspace graph, with its live state.</summary>
/// <param name="app">The app's row, whose state the host keeps current; null for anything else, or an app the
/// workspace hasn't brought up yet.</param>
/// <param name="target">Where a container runs; null for anything else, or one docker can't be asked about.</param>
public sealed partial class GraphNode(TopologyNode node, WorkspaceAppRow? app, InfraTarget? target) : ObservableObject
{
    public TopologyNode Node => node;
    public string Key => node.Key;
    public string Name => node.Name;
    public string Owner => node.Owner;
    public TopologyKind Kind => node.Kind;
    public string Detail => node.Detail;
    public WorkspaceAppRow? App => app;
    public InfraTarget? Target => target;

    public bool IsRunner => node.IsRunner;
    public bool IsInfra => node.IsInfra;
    public bool IsOutside => node.Kind is TopologyKind.Outside or TopologyKind.Missing;

    /// <summary>What it is (<see cref="NodeRoles"/>): given by its definition or worked out; null when neither says.</summary>
    public string? Role => node.Role;

    public string KindLabel => (Role is { } role ? role + " · " : "") + node.Kind switch
    {
        TopologyKind.App => "app",
        TopologyKind.Job => "job",
        TopologyKind.Container => $"container · {node.Detail}",
        TopologyKind.ComposeService => "compose service",
        TopologyKind.Outside => "outside the workspace",
        _ => "an input with no value",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLine))]
    private string _state = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLine))]
    private string _stateDetail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HealthBrush))]
    private NodeHealth _health = NodeHealth.Unknown;

    public Avalonia.Media.IBrush HealthBrush => Controls.WorkspaceGraphCanvas.HealthBrush(Health);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartLabel), nameof(CanStop))]
    private bool _isAlive;

    public string StateLine => StateDetail.Length == 0 ? State : $"{State} · {StateDetail}";

    /// <summary>A container's usage, from docker, for the picked node's card. An app's is in the details under it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsageText), nameof(HasUsage))]
    private Sprig.Engine.Hosting.ResourceUsage? _infraUsage;

    public string UsageText => InfraUsage?.Line ?? "";
    public bool HasUsage => InfraUsage is not null;

    /// <summary>What it uses now, app or container, for drawing over the node.</summary>
    public Sprig.Engine.Hosting.ResourceUsage? Usage => app?.Usage ?? InfraUsage;

    public bool CanStart => app is not null || target is not null;
    public bool CanStop => CanStart && IsAlive;
    public string StartLabel => IsAlive ? "Restart" : "Start";
    public bool HasLog => app is not null || target is not null;
    public string Url => app?.Url ?? "";
    public bool HasUrl => Url.Length > 0;

    /// <summary>What it uses and what uses it, in words, for the picked node's card.</summary>
    public IReadOnlyList<string> Uses { get; set; } = [];
    public IReadOnlyList<string> UsedBy { get; set; } = [];
    public bool HasUses => Uses.Count > 0;
    public bool HasUsedBy => UsedBy.Count > 0;

    /// <summary>The infra and outside values it uses, shown as tags under it rather than as lines: nearly every service
    /// uses the database, so those lines would say little and hide the rest.</summary>
    public IReadOnlyList<GraphNode> Tags { get; set; } = [];

    /// <summary>Taken with others as a tag: drawn without its links unless it, or what uses it, is in focus.</summary>
    public bool IsTag => IsInfra || IsOutside;

    /// <summary>Its name as a tag, without its repo's name in front (<c>shop-db</c> in <c>shop</c> is <c>db</c>).</summary>
    public string TagName => Name.StartsWith(Owner + "-", StringComparison.OrdinalIgnoreCase) && Name.Length > Owner.Length + 1
        ? Name[(Owner.Length + 1)..] : Name;

    /// <summary>Show what the host last said about the app.</summary>
    public void Refresh()
    {
        if (node.Kind == TopologyKind.Outside) (State, StateDetail, Health) = ("outside", ValueOrNothing(node.Detail), NodeHealth.Unknown);
        else if (node.Kind == TopologyKind.Missing) (State, StateDetail, Health) = ("no value yet", "give it one in the workspace's Inputs", NodeHealth.Bad);
        else if (IsInfra && State.Length == 0) (State, Health) = ("checking…", NodeHealth.Unknown);
        if (!IsRunner) return;
        if (app is null)
        {
            (State, StateDetail, Health, IsAlive) = ("not in this workspace yet", "start it, or make it ready again, to add it", NodeHealth.Off, false);
            return;
        }
        State = app.State;
        StateDetail =app.Detail.StartsWith(app.State, StringComparison.OrdinalIgnoreCase) ? app.Detail[app.State.Length..].TrimStart(' ', '·') : app.Detail;
        IsAlive = app.IsAlive;
        Health = app.IsBad || app.State == "restarting" ? NodeHealth.Bad
            : app.IsGood ? NodeHealth.Good
            : app.IsWaiting ? NodeHealth.Waiting
            : app.IsBusy ? NodeHealth.Busy
            : NodeHealth.Off;
    }

    static string ValueOrNothing(string value) => value.Length > 60 ? value[..57] + "…" : value;

    /// <summary>Show what docker said about the container; <paramref name="known"/> false when docker didn't answer.</summary>
    public void Apply(ComposeService? service, bool known)
    {
        if (!IsInfra) return;
        if (!known)
        {
            (State, StateDetail, Health, IsAlive) = ("unknown", "docker isn't answering", NodeHealth.Unknown, false);
            return;
        }
        if (service is null)
        {
            (State, StateDetail, Health, IsAlive) = ("not started", "", NodeHealth.Off, false);
            return;
        }
        IsAlive = service.State is "running" or "restarting";
        (State, StateDetail, Health) = (service.State, service.Health) switch
        {
            ("running", "healthy") => ("healthy", "", NodeHealth.Good),
            ("running", "unhealthy") => ("unhealthy", "its health check keeps failing", NodeHealth.Bad),
            ("running", "starting") => ("starting", "waiting for its health check", NodeHealth.Busy),
            ("running", _) => ("running", "", NodeHealth.Good),
            ("restarting", _) => ("restarting", $"last exit {service.ExitCode}", NodeHealth.Bad),
            ("exited" or "dead", _) when service.ExitCode is not (0 or 137 or 143) => ("exited", $"code {service.ExitCode}", NodeHealth.Bad),
            ("exited", _) => ("stopped", "", NodeHealth.Off),
            _ => (service.State, "", NodeHealth.Off),
        };
    }

    /// <summary>Show a main checkout's infra, which the user runs, by whether it's listening; null when the plan doesn't
    /// have it.</summary>
    public void ApplyOwn(OwnInfra? own)
    {
        if (!IsInfra) return;
        IsAlive = own?.Answering == true;
        (State, StateDetail, Health) = own switch
        {
            { Answering: true } => ("yours · listening", $"on {string.Join(", ", own.Ports)}", NodeHealth.Good),
            { Answering: false } => ("yours · not listening", $"nothing on {string.Join(", ", own.Ports)}", NodeHealth.Bad),
            _ => ("yours", "no port to check", NodeHealth.Unknown),
        };
    }

    /// <summary>Shown at once on a click, until the next answer says how it went.</summary>
    public void Pending(string state)
    {
        (State, StateDetail, Health) = (state, "", NodeHealth.Busy);
    }
}

/// <summary>What the graph's boxes gather: each repo's nodes, each start group's, or each kind of thing.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<GraphGrouping>))]
public enum GraphGrouping { Repo, StartGroup, Role }

/// <summary>
/// The workspace graph: its nodes, the links between them, where each sits, and which one is picked. The canvas draws
/// it and redraws on <see cref="Changed"/>; the page acts on what the canvas asks for.
/// </summary>
public sealed class WorkspaceGraph
{
    public const double NodeRadius = 22;

    /// <summary>The group drawn without a box: values from outside the workspace, and infra no one group has to itself.</summary>
    public const string Loose = WorkspaceTopology.OutsideOwner;

    public IReadOnlyList<GraphNode> Nodes { get; }
    public IReadOnlyList<TopologyLink> Links { get; }
    public IGraphLayout Layout { get; }

    /// <summary>Each node's box, by key: its title, or <see cref="Loose"/> for none.</summary>
    public IReadOnlyDictionary<string, string> Groups { get; }

    /// <summary>At most this many tags show under a node; the rest are counted.</summary>
    public const int MaxTags = 3;

    readonly Dictionary<string, GraphNode> _byKey;

    /// <param name="layered">In rows, what uses something above it; otherwise by forces, where dragged nodes keep the
    /// places in <paramref name="pinned"/>.</param>
    /// <param name="startGroups">The groups Start can start, for <see cref="GraphGrouping.StartGroup"/>.</param>
    public WorkspaceGraph(IReadOnlyList<GraphNode> nodes, IReadOnlyList<TopologyLink> links, IReadOnlyDictionary<string, (double X, double Y)>? pinned,
        bool layered = false, GraphGrouping grouping = GraphGrouping.Repo, IReadOnlyList<PlannedGroup>? startGroups = null)
    {
        Nodes = nodes;
        Links = links;
        _byKey = nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            node.Uses = links.Where(l => l.From == node.Key && _byKey.ContainsKey(l.To))
                .Select(l => Describe(node, _byKey[l.To], l)).ToArray();
            node.UsedBy = links.Where(l => l.To == node.Key && _byKey.ContainsKey(l.From))
                .Select(l => Describe(node, _byKey[l.From], l)).ToArray();
            node.Tags = node.IsTag ? [] : links.Where(l => l.From == node.Key && _byKey.TryGetValue(l.To, out var to) && to.IsTag)
                .Select(l => _byKey[l.To]).OrderBy(t => t.IsOutside).ThenBy(t => t.TagName, StringComparer.Ordinal).ToArray();
            node.Refresh();
        }
        var groups = GroupsOf(nodes, links, grouping, startGroups);
        Groups = groups;
        if (layered)
            Layout = new LayeredLayout(nodes.Select(n => (n.Key, groups[n.Key], Room(n), n.IsTag)), links.Select(l => (l.From, l.To)), Loose);
        else
        {
            var force = new ForceLayout(nodes.Select(n => (n.Key, groups[n.Key], Room(n))), links.Select(l => (l.From, l.To)), pinned, Loose);
            force.Settle();
            Layout = force;
        }
    }

    public const string NoStartGroup = "in no group";
    public const string Jobs = "jobs";
    public const string OtherApps = "other apps";
    public const string OtherInfra = "other infra";

    /// <summary>
    /// The box each node goes in. By start group, an app goes in the first group that names it, and infra goes with the
    /// apps that use it when they're all in one group; infra shared across groups is left loose, its tags saying who uses
    /// it. By role, each kind of thing has its own box: the web front ends, the APIs, the databases.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GroupsOf(IReadOnlyList<GraphNode> nodes, IReadOnlyList<TopologyLink> links,
        GraphGrouping grouping, IReadOnlyList<PlannedGroup>? startGroups)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (grouping)
        {
            case GraphGrouping.StartGroup when startGroups is { Count: > 0 }:
                foreach (var node in nodes.Where(n => !n.IsTag))
                    result[node.Key] = startGroups.FirstOrDefault(g => g.Apps.Contains(node.Key, StringComparer.Ordinal))?.Name is { } name
                        ? (name == Loose ? $"{name} (group)" : name) : NoStartGroup;
                foreach (var node in nodes.Where(n => n.IsTag))
                {
                    var users = links.Where(l => l.To == node.Key && result.ContainsKey(l.From)).Select(l => result[l.From]).Distinct().ToArray();
                    result[node.Key] = users is [var only] ? only : Loose;
                }
                break;
            case GraphGrouping.Role:
                foreach (var node in nodes)
                    result[node.Key] = node.Kind == TopologyKind.Job ? Jobs
                        : node.Role is { } role ? RoleTitle(role)
                        : node.IsRunner ? OtherApps
                        : node.IsInfra ? OtherInfra
                        : Loose;
                break;
            default:
                foreach (var node in nodes) result[node.Key] = node.IsOutside ? Loose : node.Owner;
                break;
        }
        return result;
    }

    /// <summary>A role's box title, for the many of it.</summary>
    static string RoleTitle(string role) => role switch
    {
        NodeRoles.Api => "APIs",
        NodeRoles.Web => "web",
        NodeRoles.Database => "databases",
        NodeRoles.Cache => "caches",
        NodeRoles.Queue => "queues",
        NodeRoles.Worker => "workers",
        NodeRoles.Proxy => "proxies",
        _ => role,
    };

    /// <summary>The room a node needs either side of its centre: its name and its tags are usually wider than its circle.</summary>
    static double Room(GraphNode node)
    {
        var tags = node.Tags.Take(MaxTags).Sum(t => TagWidth(t.TagName)) + (node.Tags.Count > MaxTags ? TagWidth("+9") : 0)
            + Math.Max(0, Math.Min(node.Tags.Count, MaxTags + 1) - 1) * TagGap;
        return Math.Max(Math.Max(NodeRadius + 14, node.Name.Length * 3.6 + 12), tags / 2 + 4);
    }

    public const double TagGap = 4;

    /// <summary>A tag's width at full size: a dot, its text, and padding.</summary>
    public static double TagWidth(string text) => text.Length * 5.6 + 22;

    /// <summary>Its line is left out unless one of its ends is in focus: a link to a node shown as a tag.</summary>
    public bool IsTagLink(TopologyLink link) => Find(link.To) is { IsTag: true } && Find(link.From) is { IsTag: false };

    static string Describe(GraphNode from, GraphNode other, TopologyLink link)
    {
        var name = other.Owner == from.Owner || other.IsOutside ? other.Name : $"{other.Owner} / {other.Name}";
        return link.OrderOnly ? $"{name} (starts after it)" : $"{name} · {string.Join(", ", link.Values)}";
    }

    public GraphNode? Find(string? key) => key is not null ? _byKey.GetValueOrDefault(key) : null;

    public string? Selected { get; private set; }
    public string? Hovered { get; private set; }

    /// <summary>Redraw: a state, the selection or a place changed.</summary>
    public event Action? Changed;

    /// <summary>The picked node changed (null for none).</summary>
    public event Action<GraphNode?>? Picked;

    /// <summary>Open a node's log.</summary>
    public event Action<GraphNode>? LogRequested;

    /// <summary>A node was double-clicked: show its details, which are easily left folded away.</summary>
    public event Action<GraphNode>? DetailsRequested;

    /// <summary>A node was dragged to a place of its own, to be kept.</summary>
    public event Action? PinsChanged;

    /// <summary>Fit the whole graph in view again.</summary>
    public event Action? FitRequested;

    public void Select(string? key)
    {
        if (key == Selected) return;
        Selected = key;
        Picked?.Invoke(Find(key));
        Changed?.Invoke();
    }

    public void Hover(string? key)
    {
        if (key == Hovered) return;
        Hovered = key;
        Changed?.Invoke();
    }

    /// <summary>What's being looked for; empty for nothing.</summary>
    public string Search { get; private set; } = "";

    /// <summary>The nodes <see cref="Search"/> finds, in reading order (top to bottom, then left to right).</summary>
    public IReadOnlyList<string> Matches { get; private set; } = [];

    public bool Searching => Search.Length > 0;
    public bool IsMatch(string key) => Searching && Matches.Contains(key);

    /// <summary>Bring a node into the middle of the view.</summary>
    public event Action<string>? CenterRequested;

    /// <summary>
    /// Look for <paramref name="text"/> in each node's name, repo, port, image or role, and the tags under it, so "redis"
    /// finds redis and everything that uses it, and "queue" every queue.
    /// </summary>
    public void SetSearch(string text)
    {
        Search = text.Trim();
        bool Has(string s) => s.Contains(Search, StringComparison.OrdinalIgnoreCase);
        Matches = !Searching ? []
            : Nodes.Where(n => Has(n.Name) || Has(n.Owner) || Has(n.Detail) || (n.Role is { } role && Has(role))
                    || n.Tags.Any(t => Has(t.Name)))
                .Select(n => Layout.Find(n.Key))
                .OfType<LayoutBody>()
                .OrderBy(b => Math.Round(b.Y / 40)).ThenBy(b => b.X)
                .Select(b => b.Key)
                .ToArray();
        Changed?.Invoke();
    }

    /// <summary>Pick a node and bring it into view.</summary>
    public void Reveal(string key)
    {
        Select(key);
        CenterRequested?.Invoke(key);
    }

    public void Invalidate() => Changed?.Invoke();
    public void RequestLog(GraphNode node) => LogRequested?.Invoke(node);
    public void RequestDetails(GraphNode node) => DetailsRequested?.Invoke(node);
    public void Pinned() => PinsChanged?.Invoke();
    public void Fit() => FitRequested?.Invoke();

    /// <summary>The nodes placed by hand, by key.</summary>
    public IReadOnlyDictionary<string, (double X, double Y)> Pins()
        => Layout.Bodies.Where(b => b.Pinned).ToDictionary(b => b.Key, b => (Math.Round(b.X, 1), Math.Round(b.Y, 1)));

    /// <summary>Whether <paramref name="key"/> is the picked node or linked to it, so it stays lit while the rest dim.</summary>
    public bool IsNear(string key, string focus) => key == focus || Links.Any(l => (l.From == focus && l.To == key) || (l.To == focus && l.From == key));
}
