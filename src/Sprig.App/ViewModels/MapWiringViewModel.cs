using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Maps;
using Sprig.Core.Maps;

namespace Sprig.App.ViewModels;

/// <summary>
/// The wiring section of the Maps page: how a checkout of the selected map would meet each need, drawn as a
/// graph or listed as a ledger, then one list of the map's variables. Every variable is the same kind of
/// thing however it gets its value: wired from a provider, a constant set on the map, a value fixed in env,
/// or nothing yet. Selecting a node narrows the list to it; expanding a row shows its fixes. Every fix is
/// saved straight away and the view refreshes from the store.
/// </summary>
public sealed partial class MapWiringViewModel : ObservableObject
{
    readonly AppServices _services;
    string? _mapName;
    readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    public MapWiringViewModel(AppServices services) => _services = services;

    [ObservableProperty] private MapInspection? _inspection;
    [ObservableProperty] private MapGraphLayout? _layout;
    [ObservableProperty] private string? _selectedNodeId;
    [ObservableProperty] private bool _showLedger;
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private string? _error;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string _counts = "";

    [ObservableProperty] private string _variablesTitle = "";
    [ObservableProperty] private string? _selectionSubtitle;
    [ObservableProperty] private string? _selectionNote;

    public ObservableCollection<string> Blockers { get; } = [];
    public ObservableCollection<WiringLedgerRow> Ledger { get; } = [];
    public ObservableCollection<WiringProvideRow> SelectedProvides { get; } = [];

    /// <summary>The variables in view: <see cref="WiringNeedRow"/>s and <see cref="FixedValueRow"/>s, problems first.</summary>
    public ObservableCollection<WiringVariableRow> Variables { get; } = [];

    public string? MapName => _mapName;
    public bool HasMap => Inspection is not null;
    public bool HasNodes => Layout is { Nodes.Count: > 0 };
    public bool HasBlockers => Blockers.Count > 0;
    public bool HasSelectedProvides => SelectedProvides.Count > 0;
    public bool HasVariables => Variables.Count > 0;
    public bool IsFiltered => SelectedNodeId is not null && Layout?.Nodes.Any(n => n.Id == SelectedNodeId) == true;
    public bool ShowModuleColumn => !IsFiltered;
    public bool ShowGraph => !ShowLedger;

    public string ZoomText => $"{Zoom * 100:0}%";

    partial void OnZoomChanged(double value) => OnPropertyChanged(nameof(ZoomText));
    partial void OnShowLedgerChanged(bool value) => OnPropertyChanged(nameof(ShowGraph));
    partial void OnSelectedNodeIdChanged(string? value) => BuildVariables();

    [RelayCommand] private void ShowGraphView() => ShowLedger = false;
    [RelayCommand] private void ShowLedgerView() => ShowLedger = true;
    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(MapWiringCanvasLimits.Max, Math.Round(Zoom + 0.1, 2));
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(MapWiringCanvasLimits.Min, Math.Round(Zoom - 0.1, 2));
    [RelayCommand] private void ZoomReset() => Zoom = 1;
    [RelayCommand] private void SelectNode(string? id) => SelectedNodeId = id;
    [RelayCommand] private void ClearSelection() => SelectedNodeId = null;

    /// <summary>Show <paramref name="mapName"/>'s wiring, picking the first thing that needs attention.</summary>
    public void Load(string? mapName)
    {
        _mapName = mapName;
        _expanded.Clear();
        Error = null;
        OnPropertyChanged(nameof(MapName));
        Refresh(keepSelection: false);
    }

    void Refresh(bool keepSelection, string? select = null)
    {
        var previous = SelectedNodeId;
        if (_mapName is null || _services.Maps.Get(_mapName) is not { } map)
        {
            Inspection = null;
            Layout = null;
            Rebuild();
            SelectedNodeId = null;
            BuildVariables();
            return;
        }

        try
        {
            Inspection = _services.MapInspector.Inspect(map);
            Layout = MapGraphLayout.Compute(Inspection);
        }
        catch (Exception ex)
        {
            Inspection = null;
            Layout = null;
            Error = $"Couldn't read this map's wiring: {ex.Message}";
        }
        Rebuild();

        var ids = Layout?.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal) ?? [];
        string? Pick(string? id) => id is not null && ids.Contains(id) ? id : null;
        var next = Pick(select)
            ?? (keepSelection ? Pick(previous) ?? Pick(Counterpart(previous)) : null)
            ?? (keepSelection ? null : FirstAttention());
        if (next is not null && next.Length > 2 && next[0] is 'G' or 'D')
            _expanded.Add(WiringNeedRow.KeyFor(next[2..]));
        SelectedNodeId = next;
        BuildVariables();
    }

    // An unmet need's placeholder becomes a constant's once a value is set, and back again when removed.
    static string? Counterpart(string? id) => id switch
    {
        ['G', ':', ..] => "D:" + id[2..],
        ['D', ':', ..] => "G:" + id[2..],
        _ => null,
    };

    string? FirstAttention()
    {
        if (Inspection is not { } i) return null;
        if (i.Gaps.FirstOrDefault() is { } gap) return MapGraphLayout.GapId(gap);
        if (i.Ambiguities.FirstOrDefault() is { } amb) return MapGraphLayout.ModuleId(amb.Repo, amb.Module);
        return null;
    }

    void Rebuild()
    {
        Blockers.Clear();
        Ledger.Clear();
        if (Inspection is { } i)
        {
            var blocked = i.Gaps.Count + i.Ambiguities.Count;
            IsReady = i.IsReady;
            Headline = i.IsReady
                ? "Every need is met. A checkout of this map will go through."
                : blocked > 0
                    ? $"{blocked} {(blocked == 1 ? "need" : "needs")} can't be met. Checkout stops until each gets a value or a provider."
                    : $"{i.Problems.Count} {(i.Problems.Count == 1 ? "repo" : "repos")} can't be read, so checkout would stop.";

            foreach (var p in i.Problems) Blockers.Add($"{p.Repo} {p.Message}.");
            foreach (var n in i.Gaps)
                Blockers.Add($"{n.Repo} › {n.Module} needs {n.Capability}, and nothing in this map provides it."
                    + (n.Similar is { } s ? $" {s.Capability} has a similar name." : ""));
            foreach (var n in i.Ambiguities)
                Blockers.Add($"{n.Repo} › {n.Module} needs {n.Capability}, and {string.Join(" and ", n.Providers.Select(p => p.Repo))} both provide it.");

            var parts = new List<string>();
            void Count(int k, string one, string many) { if (k > 0) parts.Add($"{k} {(k == 1 ? one : many)}"); }
            Count(i.Needs.Count(n => n.State == NeedState.Wired), "wired", "wired");
            Count(i.Defaulted.Count, "constant set on the map", "constants set on the map");
            Count(i.FixedValues.Count, "fixed in env", "fixed in env");
            Count(i.Gaps.Count, "unmet", "unmet");
            Count(i.Ambiguities.Count, "ambiguous", "ambiguous");
            if (i.Hubs.FirstOrDefault() is { } hub) parts.Add($"most relied on: {hub.Repo} › {hub.Module} ({hub.ReliedOnBy} modules)");
            Counts = string.Join(" · ", parts);

            foreach (var row in WiringLedgerRow.Build(i)) Ledger.Add(row);
        }
        else
        {
            IsReady = false;
            Headline = "";
            Counts = "";
        }
        OnPropertyChanged(nameof(HasMap));
        OnPropertyChanged(nameof(HasNodes));
        OnPropertyChanged(nameof(HasBlockers));
    }

    void BuildVariables()
    {
        SelectedProvides.Clear();
        Variables.Clear();
        SelectionSubtitle = SelectionNote = null;
        VariablesTitle = "";

        if (Inspection is { } i)
        {
            var node = Layout?.Nodes.FirstOrDefault(n => n.Id == SelectedNodeId);
            IEnumerable<NeedInfo> needs = i.Needs;
            IEnumerable<FixedValue> fixedValues = i.FixedValues;
            if (node?.Module is { } m)
            {
                VariablesTitle = $"{m.Repo} › {m.Module}";
                SelectionSubtitle = m.Path.Length == 0 ? "repo root" : m.Path;
                if (m.ReliedOnBy >= 2) SelectionNote = $"{m.ReliedOnBy} other modules rely on this one.";
                foreach (var p in m.Provides) SelectedProvides.Add(new WiringProvideRow(p));
                needs = m.Needs;
                fixedValues = fixedValues.Where(f => f.Repo == m.Repo && f.Module == m.Module);
            }
            else if (node?.Need is { } need)
            {
                VariablesTitle = need.Capability;
                SelectionSubtitle = $"needed by {need.Repo} › {need.Module}";
                SelectionNote = need.State == NeedState.Default
                    ? $"Nothing in this map provides {need.Capability}, so the map gives this value to every workspace. If a repo that provides it joins the map, that provider wins."
                    : $"Nothing in this map provides {need.Capability} and the map sets no value for it, so checkout stops here.";
                needs = [need];
                fixedValues = [];
            }
            else
            {
                VariablesTitle = "All variables";
                SelectionSubtitle = "Select a module in the graph to narrow this list.";
            }

            var rows = needs.Select(n => (WiringVariableRow)new WiringNeedRow(this, n))
                .Concat(fixedValues.Select(f => new FixedValueRow(this, f)))
                .OrderBy(r => r.Rank).ThenBy(r => r.Module, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal);
            foreach (var row in rows)
            {
                row.IsExpanded = _expanded.Contains(row.Key);
                Variables.Add(row);
            }
        }
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(ShowModuleColumn));
        OnPropertyChanged(nameof(HasSelectedProvides));
        OnPropertyChanged(nameof(HasVariables));
    }

    internal void Expanded(WiringVariableRow row, bool expanded)
    {
        if (expanded) _expanded.Add(row.Key);
        else _expanded.Remove(row.Key);
    }

    // --- fixes ---

    void Apply(Action edit, string? select, string? expand)
    {
        Error = null;
        try
        {
            edit();
            if (expand is not null) _expanded.Add(expand);
            _services.NotifyStoreChanged();
            Refresh(keepSelection: true, select);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    // A fix keeps the list where it was: narrowed to a module stays on that module, a placeholder follows its
    // need to its new placeholder, and the whole-map list stays whole.
    string? Follow(NeedInfo need, string placeholder) => SelectedNodeId switch
    {
        null => null,
        ['M', ':', ..] => SelectedNodeId,
        _ => placeholder,
    };

    internal void SetConstant(NeedInfo need, IReadOnlyDictionary<string, string> values)
        => Apply(() => _services.MapEditor.SetDefault(_mapName!, need.Repo, need.Capability, values),
            Follow(need, MapGraphLayout.DefaultId(need)), WiringNeedRow.KeyFor(need));

    internal void RemoveConstant(NeedInfo need)
        => Apply(() => _services.MapEditor.RemoveDefault(_mapName!, need.Repo, need.Capability),
            Follow(need, MapGraphLayout.GapId(need)), WiringNeedRow.KeyFor(need));

    internal void Wire(NeedInfo need, string target)
        => Apply(() => _services.MapEditor.SetWiring(_mapName!, need.Repo, need.Capability, target),
            Follow(need, MapGraphLayout.ModuleId(need.Repo, need.Module)), WiringNeedRow.KeyFor(need));

    internal void Unwire(NeedInfo need)
        => Apply(() => _services.MapEditor.RemoveWiring(_mapName!, need.Repo, need.Capability),
            Follow(need, MapGraphLayout.ModuleId(need.Repo, need.Module)), WiringNeedRow.KeyFor(need));

    internal void Convert(FixedValue fixedValue, string capability, string output)
    {
        var cap = capability.Trim();
        Apply(() => _services.MapEditor.ConvertFixedValue(_mapName!, fixedValue, cap, output.Trim()),
            SelectedNodeId, WiringNeedRow.KeyFor($"{fixedValue.Repo}/{fixedValue.Module}/{cap}"));
    }
}

/// <summary>The zoom range the toolbar steps through, matching what the canvas allows.</summary>
public static class MapWiringCanvasLimits
{
    public const double Min = Controls.MapWiringCanvas.MinZoom;
    public const double Max = Controls.MapWiringCanvas.MaxZoom;
}

/// <summary>One capability across the map: who provides it, who needs it, and its worst status.</summary>
public sealed record WiringLedgerRow(string Capability, string ProvidedBy, string NeededBy, string Status, string StatusKind, string NodeId)
{
    public bool IsGap => StatusKind == "gap";
    public bool IsAmbiguous => StatusKind == "ambiguous";
    public bool IsConstant => StatusKind == "constant";
    public bool IsQuiet => StatusKind is "own" or "unused";

    public static IEnumerable<WiringLedgerRow> Build(MapInspection i)
    {
        static int Rank(string kind) => kind switch { "gap" => 0, "ambiguous" => 1, "constant" => 2, "wired" => 3, "own" => 4, _ => 5 };
        var caps = i.Provides.Select(p => p.Capability).Concat(i.Needs.Select(n => n.Capability)).Distinct(StringComparer.Ordinal);
        return caps.Select(cap =>
        {
            var provides = i.Provides.Where(p => p.Capability == cap).ToList();
            var needs = i.Needs.Where(n => n.Capability == cap).ToList();
            var kinds = needs.Select(n => WiringNeedRow.Kind(n.State))
                .Concat(needs.Count == 0 ? provides.Select(p => p.Use == ProvideUse.OwnUse ? "own" : "unused") : [])
                .OrderBy(Rank).ToList();
            var kind = kinds.FirstOrDefault() ?? "unused";
            var gap = needs.FirstOrDefault(n => n.State == NeedState.Gap);
            var constant = needs.FirstOrDefault(n => n.State == NeedState.Default);
            var node = gap is not null ? MapGraphLayout.GapId(gap)
                : constant is not null ? MapGraphLayout.DefaultId(constant)
                : provides.FirstOrDefault() is { } p ? MapGraphLayout.ModuleId(p.Repo, p.Module)
                : MapGraphLayout.ModuleId(needs[0].Repo, needs[0].Module);
            string Who(IEnumerable<(string Repo, string Module)> xs) => string.Join("\n", xs.Select(x => $"{x.Repo} › {x.Module}").DefaultIfEmpty("—"));
            return new WiringLedgerRow(cap,
                Who(provides.Select(p => (p.Repo, p.Module))),
                Who(needs.Select(n => (n.Repo, n.Module))),
                kind switch { "gap" => "needs a value", "ambiguous" => "pick one", "constant" => "constant", "wired" => "wired", "own" => "own use", _ => "unused" },
                kind, node);
        }).OrderBy(r => Rank(r.StatusKind)).ThenBy(r => r.Capability, StringComparer.Ordinal);
    }
}

/// <summary>A provide on the selected module: who relies on it, or that only its own repo does.</summary>
public sealed class WiringProvideRow(ProvideInfo provide)
{
    public string Capability => provide.Capability;
    public bool IsQuiet => provide.Use != ProvideUse.Shared;
    public bool IsConstant => provide.Constants is not null;
    public string UseLabel => provide.Constants is { } c
        ? "= " + string.Join(", ", c.Values)
        : provide.Use switch
        {
            ProvideUse.Shared => $"needed by {provide.Consumers.Count}",
            ProvideUse.OwnUse => "own use",
            _ => "unused",
        };
    public string Tip => provide.Use switch
    {
        ProvideUse.Shared => "Needed by " + string.Join(", ", provide.Consumers.Select(c => $"{c.Repo} › {c.Module}")),
        ProvideUse.OwnUse => "Only its own repo uses it: " + string.Join(", ", provide.OwnUses.Select(u => $"{u.Key} in {u.File}")),
        _ => "Nothing references it, which can mean a need somewhere spells it differently.",
    };
}

/// <summary>
/// A row in the variables list. Whatever gives it its value, it reads the same: a name, the module it
/// belongs to, a status, and the value; expanding it shows where it's used and the fixes that suit it.
/// </summary>
public abstract partial class WiringVariableRow(MapWiringViewModel owner) : ObservableObject
{
    protected MapWiringViewModel Owner { get; } = owner;

    public abstract string Key { get; }
    public abstract string Name { get; }
    public abstract string Module { get; }
    public abstract string Status { get; }
    public abstract string Value { get; }
    public abstract string UsedIn { get; }
    /// <summary>Sort order: what stops a checkout first, then what deserves a look, then what's fine.</summary>
    public abstract int Rank { get; }

    public virtual bool IsGap => false;
    public virtual bool IsAmbiguous => false;
    public virtual bool IsConstant => false;
    public virtual bool IsFixed => false;

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value) => Owner.Expanded(this, value);

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>A declared need, with the fixes that suit its state.</summary>
public sealed partial class WiringNeedRow : WiringVariableRow
{
    public NeedInfo Need { get; }

    public WiringNeedRow(MapWiringViewModel owner, NeedInfo need) : base(owner)
    {
        Need = need;
        var hint = need.Hints.FirstOrDefault();
        _newOutput = hint?.Output ?? "url";
        _newValue = hint?.Value ?? "";
        foreach (var (k, v) in need.DefaultValues ?? new Dictionary<string, string>())
            Constants.Add(new ConstantValueRow(k, v));
        Providers = need.State == NeedState.Ambiguous
            ? need.Providers.Select(p => new ProviderChoice(this, p)).ToList()
            : [];
    }

    public static string KeyFor(NeedInfo n) => KeyFor($"{n.Repo}/{n.Module}/{n.Capability}");
    public static string KeyFor(string needPath) => "N:" + needPath;

    public static string Kind(NeedState state) => state switch
    {
        NeedState.Gap => "gap", NeedState.Ambiguous => "ambiguous", NeedState.Default => "constant", _ => "wired",
    };

    public override string Key => KeyFor(Need);
    public override string Name => Need.Capability;
    public override string Module => $"{Need.Repo} › {Need.Module}";
    public override int Rank => Need.State switch { NeedState.Gap => 0, NeedState.Ambiguous => 1, NeedState.Default => 3, _ => 4 };
    public override bool IsGap => Need.State == NeedState.Gap;
    public override bool IsAmbiguous => Need.State == NeedState.Ambiguous;
    public override bool IsConstant => Need.State == NeedState.Default;

    public override string Status => Need.State switch
    {
        NeedState.Gap => "needs a value", NeedState.Ambiguous => "pick one", NeedState.Default => "constant", _ => "wired",
    };

    public override string Value => Need.State switch
    {
        NeedState.Wired => $"from {Need.Providers[0].Repo} › {Need.Providers[0].Module}"
            + (Need.Target != Need.Capability ? $" as {Need.Target}" : ""),
        NeedState.Default => string.Join(", ", Need.DefaultValues!.Select(v => $"{v.Key} = {v.Value}")),
        NeedState.Ambiguous => string.Join(" or ", Need.Providers.Select(p => p.Repo)),
        _ => "nothing provides it" + (Need.Similar is { } s ? $" · similar: {s.Capability}" : ""),
    };

    public override string UsedIn => Need.Uses.Count == 0 ? "Not referenced in env or compose."
        : "Used in " + string.Join(", ", Need.Uses.Select(u => $"{u.Key} ({u.File})")) + ".";

    public bool IsWiredByEntry => Need.State == NeedState.Wired && Need.Target != Need.Capability;

    public bool HasSimilar => Need.Similar is not null;
    public string? SimilarText => Need.Similar is { } s
        ? $"{s.Repo} › {s.Module} provides {s.Capability}. Wiring this need to it adds a map entry; no repo changes."
        : null;
    public string? WireSimilarLabel => Need.Similar is { } s ? $"Wire it to {s.Capability}" : null;

    public string? HintSource => Need.Hints.FirstOrDefault() is { } h ? $"Suggested from {h.Source}." : null;
    public bool HasHint => Need.Hints.Count > 0;

    /// <summary>The output and value typed in to set a constant for an unmet need.</summary>
    [ObservableProperty] private string _newOutput;
    [ObservableProperty] private string _newValue;

    public ObservableCollection<ConstantValueRow> Constants { get; } = [];
    public IReadOnlyList<ProviderChoice> Providers { get; }

    [RelayCommand]
    private void SetConstant() => Owner.SetConstant(Need, new Dictionary<string, string> { [NewOutput.Trim()] = NewValue.Trim() });

    [RelayCommand]
    private void WireSimilar()
    {
        if (Need.Similar is { } s) Owner.Wire(Need, s.Capability);
    }

    [RelayCommand]
    private void SaveConstants() => Owner.SetConstant(Need, Constants.ToDictionary(c => c.Output.Trim(), c => c.Value.Trim()));

    [RelayCommand]
    private void RemoveConstant() => Owner.RemoveConstant(Need);

    [RelayCommand]
    private void Unwire() => Owner.Unwire(Need);

    internal void Pick(ProviderRef provider) => Owner.Wire(Need, $"{provider.Repo}/{provider.Capability}");
}

public sealed partial class ConstantValueRow(string output, string value) : ObservableObject
{
    [ObservableProperty] private string _output = output;
    [ObservableProperty] private string _value = value;
}

public sealed partial class ProviderChoice(WiringNeedRow need, ProviderRef provider)
{
    public string Label => $"Use {provider.Repo}";

    [RelayCommand]
    private void Pick() => need.Pick(provider);
}

/// <summary>An env setting pinned to a local address or port: a variable whose value is fixed in the
/// repo's config rather than wired, with the need it could become.</summary>
public sealed partial class FixedValueRow : WiringVariableRow
{
    public FixedValue Fixed { get; }

    public FixedValueRow(MapWiringViewModel owner, FixedValue value) : base(owner)
    {
        Fixed = value;
        _capability = SuggestCapability(value.Key);
        _output = value.Kind == FixedKind.Port ? "port" : "address";
    }

    public override string Key => $"F:{Fixed.Repo}/{Fixed.Module}/{Fixed.File}/{Fixed.Key}";
    public override string Name => Fixed.Key;
    public override string Module => $"{Fixed.Repo} › {Fixed.Module}";
    public override string Status => "fixed in env";
    public override string Value => Fixed.DisplayValue;
    public override string UsedIn => $"Set in {Fixed.File}.";
    public override int Rank => 2;
    public override bool IsFixed => true;

    public string Advice => Fixed.Kind == FixedKind.Port
        ? "Every workspace gets this same port, so two workspaces running at once collide. As a need it shows on the graph, and the map can still set today's value."
        : "Every workspace points at this same address. Fine if that's shared on purpose, but nothing on the graph says so. As a need with today's value set on the map, it shows up as a constant.";
    public bool CanConvert => !Fixed.File.Contains("compose", StringComparison.OrdinalIgnoreCase) && Fixed.Key.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    [ObservableProperty] private string _capability;
    [ObservableProperty] private string _output;

    [RelayCommand]
    private void Convert() => Owner.Convert(Fixed, Capability, Output);

    /// <summary><c>MAIL_HOST</c> → <c>mail</c>, <c>ConnectionStrings__Queue</c> → <c>queue</c>.</summary>
    public static string SuggestCapability(string key)
    {
        var name = key.Contains("__", StringComparison.Ordinal) ? key[(key.LastIndexOf("__", StringComparison.Ordinal) + 2)..] : key;
        foreach (var suffix in new[] { "_HOST", "_PORT", "_URL", "_ADDRESS", "_ENDPOINT", "_ADDR" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        return name.Replace('_', '-').ToLowerInvariant();
    }
}
