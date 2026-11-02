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
/// graph or listed as a ledger, with a detail panel for the selected node and the fixes it offers (set a
/// constant, wire to a similar name, pick between providers, turn a fixed env value into a need). Every fix
/// is saved straight away and the view refreshes from the store.
/// </summary>
public sealed partial class MapWiringViewModel : ObservableObject
{
    readonly AppServices _services;
    readonly Action? _changed;
    string? _mapName;

    public MapWiringViewModel(AppServices services, Action? changed = null)
    {
        _services = services;
        _changed = changed;
    }

    [ObservableProperty] private MapInspection? _inspection;
    [ObservableProperty] private MapGraphLayout? _layout;
    [ObservableProperty] private string? _selectedNodeId;
    [ObservableProperty] private bool _showLedger;
    [ObservableProperty] private double _zoom = 1;
    [ObservableProperty] private string? _error;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private bool _isReady;
    [ObservableProperty] private string _counts = "";

    [ObservableProperty] private string? _selectionKind;
    [ObservableProperty] private string? _selectionTitle;
    [ObservableProperty] private string? _selectionSubtitle;
    [ObservableProperty] private string? _selectionNote;

    public ObservableCollection<string> Blockers { get; } = [];
    public ObservableCollection<WiringLedgerRow> Ledger { get; } = [];
    public ObservableCollection<FixedValueRow> FixedValues { get; } = [];
    public ObservableCollection<WiringProvideRow> SelectedProvides { get; } = [];
    public ObservableCollection<WiringNeedRow> SelectedNeeds { get; } = [];

    public bool HasMap => Inspection is not null;
    public bool HasNodes => Layout is { Nodes.Count: > 0 };
    public bool HasBlockers => Blockers.Count > 0;
    public bool HasSelection => SelectionTitle is not null;
    public bool HasSelectedProvides => SelectedProvides.Count > 0;
    public bool HasSelectedNeeds => SelectedNeeds.Count > 0;
    public bool HasFixedValues => FixedValues.Count > 0;
    public bool ShowGraph => !ShowLedger;

    partial void OnShowLedgerChanged(bool value) => OnPropertyChanged(nameof(ShowGraph));
    partial void OnSelectedNodeIdChanged(string? value) => BuildSelection();

    [RelayCommand] private void ShowGraphView() => ShowLedger = false;
    [RelayCommand] private void ShowLedgerView() => ShowLedger = true;
    [RelayCommand] private void ZoomIn() => Zoom = Math.Min(2, Math.Round(Zoom + 0.1, 2));
    [RelayCommand] private void ZoomOut() => Zoom = Math.Max(0.3, Math.Round(Zoom - 0.1, 2));
    [RelayCommand] private void ZoomReset() => Zoom = 1;

    [RelayCommand]
    private void SelectNode(string? id)
    {
        SelectedNodeId = id;
    }

    /// <summary>Show <paramref name="mapName"/>'s wiring, picking the first thing that needs attention.</summary>
    public void Load(string? mapName)
    {
        _mapName = mapName;
        Error = null;
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
        SelectedNodeId = Pick(select)
            ?? (keepSelection ? Pick(previous) ?? Pick(Counterpart(previous)) : null)
            ?? (keepSelection ? null : FirstAttention());
        BuildSelection();
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
        FixedValues.Clear();
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
            Count(i.Gaps.Count, "unmet", "unmet");
            Count(i.Ambiguities.Count, "ambiguous", "ambiguous");
            Count(i.FixedValues.Count, "fixed value", "fixed values");
            if (i.Hubs.FirstOrDefault() is { } hub) parts.Add($"most relied on: {hub.Repo} › {hub.Module} ({hub.ReliedOnBy} modules)");
            Counts = string.Join(" · ", parts);

            foreach (var row in WiringLedgerRow.Build(i)) Ledger.Add(row);
            foreach (var f in i.FixedValues) FixedValues.Add(new FixedValueRow(this, f));
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
        OnPropertyChanged(nameof(HasFixedValues));
    }

    void BuildSelection()
    {
        SelectedProvides.Clear();
        SelectedNeeds.Clear();
        SelectionKind = SelectionTitle = SelectionSubtitle = SelectionNote = null;

        var node = Layout?.Nodes.FirstOrDefault(n => n.Id == SelectedNodeId);
        if (node?.Module is { } m)
        {
            SelectionKind = "Module";
            SelectionTitle = $"{m.Repo} › {m.Module}";
            SelectionSubtitle = m.Path.Length == 0 ? "repo root" : m.Path;
            if (m.ReliedOnBy >= 2) SelectionNote = $"{m.ReliedOnBy} other modules rely on this one.";
            foreach (var p in m.Provides) SelectedProvides.Add(new WiringProvideRow(p));
            foreach (var n in m.Needs) SelectedNeeds.Add(new WiringNeedRow(this, n));
        }
        else if (node?.Need is { } need)
        {
            SelectionKind = need.State == NeedState.Default ? "Constant set on the map" : "Unmet need";
            SelectionTitle = need.Capability;
            SelectionSubtitle = $"needed by {need.Repo} › {need.Module}";
            SelectionNote = need.State == NeedState.Default
                ? $"Nothing in this map provides {need.Capability}, so the map gives this value to every workspace. If a repo that provides it joins the map, that provider wins."
                : $"Nothing in this map provides {need.Capability} and the map sets no value for it, so checkout stops here.";
            SelectedNeeds.Add(new WiringNeedRow(this, need));
        }
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasSelectedProvides));
        OnPropertyChanged(nameof(HasSelectedNeeds));
    }

    // --- fixes ---

    void Apply(Action edit, string? select = null)
    {
        Error = null;
        try
        {
            edit();
            _services.NotifyStoreChanged();
            _changed?.Invoke();
            Refresh(keepSelection: true, select);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    internal void SetConstant(NeedInfo need, IReadOnlyDictionary<string, string> values)
        => Apply(() => _services.MapEditor.SetDefault(_mapName!, need.Repo, need.Capability, values), MapGraphLayout.DefaultId(need));

    internal void RemoveConstant(NeedInfo need)
        => Apply(() => _services.MapEditor.RemoveDefault(_mapName!, need.Repo, need.Capability), MapGraphLayout.GapId(need));

    internal void Wire(NeedInfo need, string target)
        => Apply(() => _services.MapEditor.SetWiring(_mapName!, need.Repo, need.Capability, target), MapGraphLayout.ModuleId(need.Repo, need.Module));

    internal void Unwire(NeedInfo need)
        => Apply(() => _services.MapEditor.RemoveWiring(_mapName!, need.Repo, need.Capability), MapGraphLayout.ModuleId(need.Repo, need.Module));

    internal void Convert(FixedValue fixedValue, string capability, string output)
        => Apply(() => _services.MapEditor.ConvertFixedValue(_mapName!, fixedValue, capability.Trim(), output.Trim()),
            $"D:{fixedValue.Repo}/{fixedValue.Module}/{capability.Trim()}");
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
    public string Outputs => string.Join(", ", provide.Outputs);
    public bool IsConstant => provide.Constants is not null;
    public string? Constant => provide.Constants is { } c ? string.Join(", ", c.Select(v => $"{v.Key} = {v.Value}")) + ", the same in every workspace" : null;
    public bool IsQuiet => provide.Use != ProvideUse.Shared;
    public string UseLabel => provide.Use switch
    {
        ProvideUse.Shared => $"needed by {provide.Consumers.Count}",
        ProvideUse.OwnUse => "own use",
        _ => "unused",
    };
    public string UseDetail => provide.Use switch
    {
        ProvideUse.Shared => string.Join(", ", provide.Consumers.Select(c => $"{c.Repo} › {c.Module}")),
        ProvideUse.OwnUse => "Only its own repo uses it: " + string.Join(", ", provide.OwnUses.Select(u => $"{u.Key} in {u.File}")),
        _ => "Nothing references it, which can mean a need somewhere spells it differently.",
    };
}

/// <summary>A need on the selected node, with the fixes that suit its state.</summary>
public sealed partial class WiringNeedRow : ObservableObject
{
    readonly MapWiringViewModel _owner;
    public NeedInfo Need { get; }

    public WiringNeedRow(MapWiringViewModel owner, NeedInfo need)
    {
        _owner = owner;
        Need = need;
        var hint = need.Hints.FirstOrDefault();
        _output = hint?.Output ?? "url";
        _value = hint?.Value ?? "";
        foreach (var (k, v) in need.DefaultValues ?? new Dictionary<string, string>())
            Constants.Add(new ConstantValueRow(k, v));
        Providers = need.State == NeedState.Ambiguous
            ? need.Providers.Select(p => new ProviderChoice(this, p)).ToList()
            : [];
    }

    public static string Kind(NeedState state) => state switch
    {
        NeedState.Gap => "gap", NeedState.Ambiguous => "ambiguous", NeedState.Default => "constant", _ => "wired",
    };

    public string Capability => Need.Capability;
    public string StatusKind => Kind(Need.State);
    public string Status => Need.State switch
    {
        NeedState.Gap => "needs a value", NeedState.Ambiguous => "pick one", NeedState.Default => "constant", _ => "wired",
    };

    public string Detail => Need.State switch
    {
        NeedState.Wired => $"from {Need.Providers[0].Repo} › {Need.Providers[0].Module}"
            + (Need.Target != Need.Capability ? $", as {Need.Target} through a map wiring entry" : ""),
        NeedState.Default => "set on the map: " + string.Join(", ", Need.DefaultValues!.Select(v => $"{v.Key} = {v.Value}")),
        NeedState.Ambiguous => $"{string.Join(" and ", Need.Providers.Select(p => $"{p.Repo} › {p.Module}"))} both provide it",
        _ => "nothing in this map provides it",
    };

    public string UsedIn => Need.Uses.Count == 0 ? "not referenced in env or compose"
        : "used in " + string.Join(", ", Need.Uses.Select(u => $"{u.Key} ({u.File})"));

    public bool IsGap => Need.State == NeedState.Gap;
    public bool IsAmbiguous => Need.State == NeedState.Ambiguous;
    public bool IsConstant => Need.State == NeedState.Default;
    public bool IsWiredByEntry => Need.State == NeedState.Wired && Need.Target != Need.Capability;

    public bool HasSimilar => Need.Similar is not null;
    public string? SimilarText => Need.Similar is { } s
        ? $"{s.Repo} › {s.Module} provides {s.Capability}. Wiring this need to it adds a map entry; no repo changes."
        : null;
    public string? WireSimilarLabel => Need.Similar is { } s ? $"Wire it to {s.Capability}" : null;

    public string? HintSource => Need.Hints.FirstOrDefault() is { } h ? $"Suggested from {h.Source}." : null;
    public bool HasHint => Need.Hints.Count > 0;

    [ObservableProperty] private string _output;
    [ObservableProperty] private string _value;

    public ObservableCollection<ConstantValueRow> Constants { get; } = [];
    public IReadOnlyList<ProviderChoice> Providers { get; }

    [RelayCommand]
    private void SetConstant() => _owner.SetConstant(Need, new Dictionary<string, string> { [Output.Trim()] = Value.Trim() });

    [RelayCommand]
    private void WireSimilar()
    {
        if (Need.Similar is { } s) _owner.Wire(Need, s.Capability);
    }

    [RelayCommand]
    private void SaveConstants() => _owner.SetConstant(Need, Constants.ToDictionary(c => c.Output.Trim(), c => c.Value.Trim()));

    [RelayCommand]
    private void RemoveConstant() => _owner.RemoveConstant(Need);

    [RelayCommand]
    private void Unwire() => _owner.Unwire(Need);

    internal void Pick(ProviderRef provider) => _owner.Wire(Need, $"{provider.Repo}/{provider.Capability}");
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

/// <summary>An env value pinned to a local address or port, with the need it could become.</summary>
public sealed partial class FixedValueRow : ObservableObject
{
    readonly MapWiringViewModel _owner;
    public FixedValue Value { get; }

    public FixedValueRow(MapWiringViewModel owner, FixedValue value)
    {
        _owner = owner;
        Value = value;
        _capability = SuggestCapability(value.Key);
        _output = value.Kind == FixedKind.Port ? "port" : "address";
    }

    public string Setting => $"{Value.Key} = {Value.DisplayValue}";
    public string Where => $"{Value.Repo} › {Value.Module} · {Value.File}";
    public string Advice => Value.Kind == FixedKind.Port
        ? "Every workspace gets this same port, so two workspaces running at once collide. As a need it shows on the graph, and the map can still set today's value."
        : "Every workspace points at this same address. Fine if that's shared on purpose, but nothing on the graph says so. As a need with today's value set on the map, it shows up as a constant.";
    public bool CanConvert => !Value.File.Contains("compose", StringComparison.OrdinalIgnoreCase) && Value.Key.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    [ObservableProperty] private string _capability;
    [ObservableProperty] private string _output;

    [RelayCommand]
    private void Convert() => _owner.Convert(Value, Capability, Output);

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
