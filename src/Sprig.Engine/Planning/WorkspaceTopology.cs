using System.Text.RegularExpressions;

namespace Sprig.Engine.Planning;

/// <summary><see cref="Outside"/>: a value given from outside the workspace that points somewhere, such as a shared
/// database's url. <see cref="Missing"/>: a setting nothing gives a value yet.</summary>
public enum TopologyKind { App, Job, Container, ComposeService, Outside, Missing }

/// <param name="Key"><c>owner/name</c> for what a repo or the orchestration runs, as plans and the host name it.</param>
/// <param name="Detail">Its port, image or value, for a second line under its name.</param>
/// <param name="Role">What it is, as <see cref="NodeRoles"/> names it: given by its definition or worked out; null when
/// neither says.</param>
public sealed record TopologyNode(string Key, string Owner, string Name, TopologyKind Kind, string Detail, string? Role = null)
{
    public bool IsInfra => Kind is TopologyKind.Container or TopologyKind.ComposeService;
    public bool IsRunner => Kind is TopologyKind.App or TopologyKind.Job;
}

/// <summary>One node using another: <see cref="From"/> takes values from <see cref="To"/>, or waits for it.</summary>
/// <param name="Values">The values it takes, by the setting they cross or else their key; empty when it only waits.</param>
/// <param name="Waits">It also waits for <see cref="To"/> before it starts.</param>
public sealed record TopologyLink(string From, string To, IReadOnlyList<string> Values, bool Waits)
{
    public bool OrderOnly => Values.Count == 0;
}

/// <summary>
/// A workspace as nodes and the links between them: what each app and container reaches through its values, wherever
/// that is (its own repo's database counts, unlike <see cref="Plan.Wires"/>), and what it only waits for.
/// </summary>
public sealed partial record WorkspaceTopology(IReadOnlyList<TopologyNode> Nodes, IReadOnlyList<TopologyLink> Links)
{
    public const string OutsideOwner = "outside";

    public static WorkspaceTopology Of(Plan plan)
    {
        var nodes = new Dictionary<string, TopologyNode>(StringComparer.Ordinal);
        void Add(TopologyNode node) => nodes.TryAdd(node.Key, node);

        var ports = plan.Ports.GroupBy(p => (p.Owner, p.Resource)).ToDictionary(g => g.Key, g => g.First().Port);
        foreach (var app in plan.Apps)
            Add(new TopologyNode(app.Key, app.Owner, app.Name, app.Job ? TopologyKind.Job : TopologyKind.App,
                app.Job ? "job" : ports.GetValueOrDefault((app.Owner, app.Name)) is { } port ? $":{port}" : "",
                app.Job ? null : NodeRoles.OfApp(app.Role, app.Name, app.Kind)));
        foreach (var c in plan.Containers)
            Add(new TopologyNode($"{c.Owner}/{c.Name}", c.Owner, c.Name, TopologyKind.Container, c.Image, NodeRoles.OfInfra(c.Role, c.Name, c.Image)));
        foreach (var compose in plan.Compose)
            foreach (var s in compose.Services)
                Add(new TopologyNode($"{compose.Owner}/{s.Name}", compose.Owner, s.Name, TopologyKind.ComposeService,
                    s.Mappings.FirstOrDefault(m => m.Host is not null) is { } m ? $":{m.Host}" : compose.File, NodeRoles.OfInfra(s.Role, s.Name)));

        var values = new Dictionary<(string From, string To), List<string>>();
        void Link(string from, string to, string value)
        {
            if (from == to) return;
            if (!values.TryGetValue((from, to), out var list)) values[(from, to)] = list = [];
            if (!list.Contains(value)) list.Add(value);
        }

        var users = plan.Apps.Select(a => (Key: a.Key, a.Env))
            .Concat(plan.Containers.Select(c => (Key: $"{c.Owner}/{c.Name}", c.Env)));
        foreach (var (from, env) in users)
            foreach (var e in env)
                foreach (var source in e.Value.Sources ?? [])
                {
                    var name = source.Setting ?? e.Key;
                    if (source.Owner is null)
                    {
                        Add(new TopologyNode($"{OutsideOwner}/{name}", OutsideOwner, name, TopologyKind.Missing, "no value yet",
                            NodeRoles.OfInfra(plan.RoleOfSetting(name), name)));
                        Link(from, $"{OutsideOwner}/{name}", name);
                    }
                    else if (source.Resource is { } resource)
                    {
                        if (nodes.ContainsKey($"{source.Owner}/{resource}")) Link(from, $"{source.Owner}/{resource}", name);
                    }
                    else if (source.Setting is not null && e.Value.Value is { } value
                             && (LooksLikeAddress(value) || NodeRoles.IsInfra(plan.RoleOfSetting(source.Setting))))
                    {
                        Add(new TopologyNode($"{OutsideOwner}/{name}", OutsideOwner, name, TopologyKind.Outside, ValueMask.Apply(value),
                            NodeRoles.OfInfra(plan.RoleOfSetting(name), name, value: value)));
                        Link(from, $"{OutsideOwner}/{name}", name);
                    }
                }

        // A main checkout's apps have no values, only what they'd have pointed at.
        foreach (var app in plan.Apps)
            foreach (var used in app.Uses ?? [])
                if (nodes.ContainsKey($"{used.Owner}/{used.Resource}")) Link(app.Key, $"{used.Owner}/{used.Resource}", used.Resource!);

        var waits = new HashSet<(string From, string To)>();
        foreach (var app in plan.Apps)
            foreach (var target in app.WaitFor.Select(w => w.Contains('/') ? w : $"{app.Owner}/{w}").Concat(app.DependsOn ?? []))
                if (target != app.Key && nodes.ContainsKey(target)) waits.Add((app.Key, target));

        var links = values.Select(v => new TopologyLink(v.Key.From, v.Key.To, v.Value, waits.Contains(v.Key)))
            .Concat(waits.Where(w => !values.ContainsKey(w)).Select(w => new TopologyLink(w.From, w.To, [], true)))
            .OrderBy(l => l.From, StringComparer.Ordinal).ThenBy(l => l.To, StringComparer.Ordinal)
            .ToArray();
        return new WorkspaceTopology(nodes.Values.ToArray(), links);
    }

    /// <summary>Only a value that names somewhere to connect to earns a node of its own; a flag or a name doesn't.</summary>
    internal static bool LooksLikeAddress(string value)
        => value.Contains("://", StringComparison.Ordinal) || AddressLike().IsMatch(value);

    [GeneratedRegex(@"(^|;)\s*(Host|Server|Data Source|Address|Addr)\s*=|^[A-Za-z0-9.-]+:\d{2,5}$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressLike();
}
