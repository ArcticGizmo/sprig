using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Sprig.Engine.Planning;

namespace Sprig.App.ViewModels;

/// <summary>An app, job, container or service inside a box. Infra in a repo's box shares one line, so only apps,
/// jobs and what the orchestration adds get a line of their own, and a place for wires to attach.</summary>
/// <param name="Key">"owner/name", as plans and groups name it.</param>
public partial class GraphItem(string key, string owner, string name, string detail, bool isJob, double y) : ObservableObject
{
    public string Key { get; } = key;
    public string Owner { get; } = owner;
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public bool IsJob { get; } = isJob;

    /// <summary>Where its line sits, from the top of its box.</summary>
    public double Y { get; } = y;

    [ObservableProperty] private bool _isDimmed;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>A repo, or what the orchestration adds, at its place in the picture.</summary>
/// <param name="Kept">"in repo" or "in sprig" for a repo; null for the orchestration's box.</param>
public sealed record GraphBox(string Owner, string Title, string? Kept, bool IsOrchestration, double X, double Y, double Height,
    IReadOnlyList<GraphItem> Items, string Infra)
{
    public bool InSprig => Kept == "in sprig";
    public bool HasKept => Kept is not null;
    public bool HasInfra => Infra.Length > 0;
    public double Width => OrchestrationGraph.BoxWidth;
}

/// <summary>A choice above the picture: everything, or one group, whose apps stay lit while the rest dims.</summary>
/// <param name="Name">The group; null for everything.</param>
public sealed record GroupChip(string? Name, string Label, bool IsOn);

/// <summary>A value reaching an app from somewhere else, as a line to draw.</summary>
/// <param name="Label">The import it comes through, when it crosses one.</param>
/// <param name="Filled">False for a need nothing fills: drawn as a stub that leads nowhere.</param>
public sealed record GraphWire(double X1, double Y1, double X2, double Y2, string? Label, bool Filled, bool Highlighted, bool Dimmed,
    string ConsumerKey, string? ProviderKey);

/// <summary>
/// The orchestration as a picture: a box per repo holding its apps, a box for what the orchestration adds, and a wire
/// for each value that reaches an app from elsewhere. What the orchestration adds takes the first column, then repos sit
/// in columns by how far they are from what they need, so values flow left to right; within a column, a repo sits near
/// the ones it takes from.
/// </summary>
public sealed class OrchestrationGraph
{
    public const double BoxWidth = 210;
    public const double ColumnGap = 130;
    public const double RowGap = 28;
    public const double Header = 44;
    public const double ItemHeight = 30;
    public const double ItemGap = 6;
    public const double InfraHeight = 30;
    public const double Padding = 12;
    // Room on the left for a need nothing fills, which has no box to come from: its stub and its label.
    const double Margin = 230;
    const double Stub = 50;

    public IReadOnlyList<GraphBox> Boxes { get; }
    public IReadOnlyList<PlannedWire> Wires { get; }
    public double Width { get; }
    public double Height { get; }

    readonly Dictionary<string, (GraphBox box, GraphItem item)> _items;
    readonly Dictionary<string, GraphBox> _boxByOwner;

    OrchestrationGraph(IReadOnlyList<GraphBox> boxes, IReadOnlyList<PlannedWire> wires)
    {
        Boxes = boxes;
        Wires = wires;
        _boxByOwner = boxes.ToDictionary(b => b.Owner);
        _items = boxes.SelectMany(b => b.Items.Select(i => (b, i))).ToDictionary(x => x.i.Key, x => (x.b, x.i));
        Width = boxes.Count == 0 ? 0 : boxes.Max(b => b.X + b.Width) + 20;
        Height = boxes.Count == 0 ? 0 : boxes.Max(b => b.Y + b.Height) + 20;
    }

    public static OrchestrationGraph Build(Plan plan, IReadOnlyDictionary<string, string>? kept = null)
    {
        var wires = plan.Wires();
        var ports = plan.Ports.GroupBy(p => (p.Owner, p.Resource)).ToDictionary(g => g.Key, g => g.First().Port);
        var owners = plan.Apps.Select(a => a.Owner).Distinct().ToList();
        var orchestrationItems = plan.Containers.Where(c => c.Owner == plan.Orchestration).ToList();

        // Columns: a repo sits one to the right of the furthest-right repo it takes values from.
        var takesFrom = owners.ToDictionary(o => o, o => wires
            .Where(w => w.ConsumerOwner == o && w.ProviderOwner is { } p && p != o && p != plan.Orchestration)
            .Select(w => w.ProviderOwner!).Distinct().ToList());
        var column = new Dictionary<string, int>();
        int ColumnOf(string owner, HashSet<string> visiting)
        {
            if (column.TryGetValue(owner, out var known)) return known;
            if (!visiting.Add(owner)) return 0; // repos that take from each other share a column
            var c = takesFrom[owner].Count == 0 ? 0 : takesFrom[owner].Max(p => ColumnOf(p, visiting)) + 1;
            visiting.Remove(owner);
            return column[owner] = c;
        }
        foreach (var owner in owners) ColumnOf(owner, []);

        // What the orchestration adds only ever offers, so it takes the first column and the repos move one along.
        var shift = orchestrationItems.Count > 0 ? 1 : 0;

        // What each box would hold, before it has a place.
        var plans = new List<(string owner, int col, List<(string key, string name, string detail, bool job)> items, string infra, string? kept, bool orchestration)>();
        foreach (var owner in owners)
        {
            var items = plan.Apps.Where(a => a.Owner == owner)
                .Select(a => ($"{owner}/{a.Name}", a.Name, a.Job ? "job" : ports.GetValueOrDefault((owner, a.Name)) is { } port ? $":{port}" : "", a.Job))
                .ToList();
            var infra = plan.Containers.Where(c => c.Owner == owner).Select(c => c.Name)
                .Concat(plan.Compose.Where(c => c.Owner == owner).SelectMany(c => c.Services.Select(s => s.Name)))
                .ToArray();
            plans.Add((owner, column[owner] + shift, items, string.Join(" · ", infra), kept?.GetValueOrDefault(owner), false));
        }
        if (orchestrationItems.Count > 0)
            plans.Add((plan.Orchestration, 0,
                orchestrationItems.Select(c => ($"{plan.Orchestration}/{c.Name}", c.Name, ports.GetValueOrDefault((plan.Orchestration, c.Name)) is { } port ? $":{port}" : "", false)).ToList(),
                "", null, true));

        // Place each column top to bottom; a repo goes near the middle of the repos it takes from.
        var margin = wires.Any(w => !w.Filled) ? Margin : 20;
        var placed = new Dictionary<string, (double x, double y, double h, int row)>();
        var boxes = new List<GraphBox>();
        foreach (var col in plans.Select(p => p.col).Distinct().Order())
        {
            var inColumn = plans.Where(p => p.col == col)
                .OrderBy(p => p.orchestration ? 1 : 0)
                .ThenBy(p => p.orchestration ? 0 : takesFrom[p.owner].Select(t => placed.TryGetValue(t, out var at) ? at.row : 0).DefaultIfEmpty(0).Average())
                .ThenBy(p => p.owner, StringComparer.Ordinal)
                .ToList();
            var y = 0.0;
            var row = 0;
            foreach (var p in inColumn)
            {
                var height = Padding + Header + p.items.Count * (ItemHeight + ItemGap) + (p.infra.Length > 0 ? InfraHeight : 0) + Padding;
                var x = margin + col * (BoxWidth + ColumnGap);
                var items = p.items.Select((it, i) => new GraphItem(it.key, p.owner, it.name, it.detail, it.job, Padding + Header + i * (ItemHeight + ItemGap))).ToList();
                boxes.Add(new GraphBox(p.owner, p.orchestration ? "added by the orchestration" : p.owner, p.kept, p.orchestration, x, y, height, items, p.infra));
                placed[p.owner] = (x, y, height, row++);
                y += height + RowGap;
            }
        }
        return new OrchestrationGraph(boxes, wires);
    }

    /// <summary>Where a wire leaves a provider: its line, or for infra in a repo's box, the box's infra line.</summary>
    (double x, double y)? From(PlannedWire wire)
    {
        if (wire.ProviderOwner is not { } owner || !_boxByOwner.TryGetValue(owner, out var box)) return null;
        if (wire.Provider is { } resource && _items.TryGetValue($"{owner}/{resource}", out var at))
            return (box.X + box.Width, box.Y + at.item.Y + ItemHeight / 2);
        return (box.X + box.Width, box.Y + (box.HasInfra ? box.Height - Padding - InfraHeight / 2 : Header / 2));
    }

    /// <summary>The lines to draw, for an app picked (its wires stand out) and a group shown (the rest dims).</summary>
    public IReadOnlyList<GraphWire> Lines(string? selectedKey, IReadOnlySet<string>? group)
    {
        foreach (var (_, (_, item)) in _items)
        {
            item.IsSelected = item.Key == selectedKey;
            // What the orchestration adds starts with any group, so it never dims.
            item.IsDimmed = group is not null && !_boxByOwner[item.Owner].IsOrchestration && !group.Contains(item.Key);
        }

        var lines = new List<GraphWire>();
        foreach (var wire in Wires)
        {
            var consumerKey = $"{wire.ConsumerOwner}/{wire.Consumer}";
            if (!_items.TryGetValue(consumerKey, out var consumer)) continue;
            var toX = consumer.box.X;
            var toY = consumer.box.Y + consumer.item.Y + ItemHeight / 2;
            var providerKey = wire.Provider is { } r && wire.ProviderOwner is { } o ? $"{o}/{r}" : null;
            var dimmed = consumer.item.IsDimmed || (providerKey is not null && _items.TryGetValue(providerKey, out var p) && p.item.IsDimmed);
            var highlighted = selectedKey is not null && (consumerKey == selectedKey || providerKey == selectedKey);
            if (From(wire) is { } from)
                lines.Add(new GraphWire(from.x, from.y, toX, toY, wire.Import, true, highlighted, dimmed && !highlighted, consumerKey, providerKey));
            else if (!wire.Filled)
                lines.Add(new GraphWire(toX - Stub, toY, toX, toY, $"nothing fills {wire.Import}", false, highlighted, dimmed && !highlighted, consumerKey, null));
        }
        // The same provider can feed one app through several values; one line says it.
        return lines.GroupBy(l => (l.ConsumerKey, l.ProviderKey, l.Label, l.X1, l.Y1)).Select(g => g.First()).ToArray();
    }
}
