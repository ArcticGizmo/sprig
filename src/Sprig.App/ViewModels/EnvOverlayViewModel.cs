using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Config;
using Sprig.Core.Env;

namespace Sprig.App.ViewModels;

/// <summary>
/// The interactive env overlay: renders a <c>.env</c> file as the merged aggregate of the target
/// file and its template/example companions, with every key's value shown as a clickable token.
/// Clicking a value opens an editor to override it with a <c>${sprig.*}</c> template (or a literal),
/// resolved into the worktree's copy at creation time. Unset values read a dimmed <c>override</c>;
/// once set they render like an applied override (accent colour). State is a map of KEY → template;
/// seeds from the repo's existing <see cref="Sprig.Core.Config.EnvOverride.Set"/> and projects back
/// via <see cref="ToSet"/>. The compose analogue is <see cref="ComposeOverlayViewModel"/>.
/// </summary>
public partial class EnvOverlayViewModel : ObservableObject
{
    // KEY -> override template. Ordinal because env keys are case-sensitive identifiers.
    private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<string> _fileKeys;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<EnvExample>> _examples;

    // Keys the user added by hand that aren't declared in the file or a template (env legitimately
    // needs to set vars that no example lists). Kept so their rows show even before a value is applied.
    private readonly List<string> _customKeys = new();

    public ObservableCollection<EnvKeyViewModel> Keys { get; } = new();
    public ObservableCollection<EnvOverrideViewModel> Overrides { get; } = new();

    /// <summary>The <c>${sprig.*}</c> variable names available to token editors (workspace + inputs +
    /// self-provided <c>&lt;cap&gt;.&lt;out&gt;</c>). A live collection owned by the repo editor, bound straight
    /// into each token's completion box.</summary>
    public IEnumerable<string> Variables { get; }

    /// <summary>Open capability heads (needs/aliases) — a dotted reference under one of these is valid whatever
    /// its output (that output lives in another repo). A live collection owned by the repo editor.</summary>
    public IEnumerable<string> OpenCapabilities { get; }

    /// <summary>The key name being added via the "+ Add key" box (two-way bound).</summary>
    [ObservableProperty] private string _newKey = "";

    public int OverrideCount => Overrides.Count;
    public bool HasOverrides => Overrides.Count > 0;
    public bool HasKeys => Keys.Count > 0;

    /// <summary>Raised whenever the applied overrides change — lets the repo editor recompute which
    /// <c>${sprig.*}</c> inputs a value references (and surface any that aren't declared yet).</summary>
    public event EventHandler? OverridesChanged;

    public EnvOverlayViewModel(
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, IReadOnlyList<EnvExample>> examples,
        IReadOnlyDictionary<string, string>? seed = null,
        IEnumerable<string>? variables = null,
        IEnumerable<string>? openCapabilities = null)
    {
        _fileKeys = keys;
        _examples = examples;
        Variables = variables ?? [];
        OpenCapabilities = openCapabilities ?? [];

        if (seed is not null)
            foreach (var (k, v) in seed)
                _overrides[k] = v;

        // A seeded override for a key no template declares still needs a row so it stays visible/editable.
        foreach (var k in _overrides.Keys)
            if (!_fileKeys.Contains(k))
                _customKeys.Add(k);

        // Declaring/removing an input, provide, or need changes which references are valid — recolour on any.
        if (Variables is INotifyCollectionChanged liveVars)
            liveVars.CollectionChanged += (_, _) => Rebuild();
        if (OpenCapabilities is INotifyCollectionChanged liveCaps)
            liveCaps.CollectionChanged += (_, _) => Rebuild();

        Rebuild();
    }

    // True when the override template names a ${sprig.*} reference the repo doesn't declare — the editor
    // renders these red (in the file view and the replacements list) instead of accent. Capability-aware:
    // a needed capability's output (in another repo) is accepted by its head, matching the config validator.
    private bool ReferencesUnknownInput(string template)
    {
        var known = new HashSet<string>(Variables, StringComparer.Ordinal);
        var open = new HashSet<string>(OpenCapabilities, StringComparer.Ordinal);
        return ConfigReferences.ReferencedNames(template).Any(n => !ConfigReferences.IsReferenceKnown(n, known, open));
    }

    /// <summary>The current overrides, ready to persist to <see cref="Sprig.Core.Config.EnvOverride.Set"/>.
    /// Blank keys are dropped (a half-typed custom key never reaches the config).</summary>
    public IReadOnlyDictionary<string, string> ToSet()
        => _overrides
            .Where(kv => kv.Key.Trim().Length > 0)
            .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value, StringComparer.Ordinal);

    // -- commands -------------------------------------------------------------

    [RelayCommand]
    private void Apply(EnvKeyViewModel? row)
    {
        if (row is null)
            return;
        var key = row.Key.Trim();
        if (key.Length == 0)
            return;
        var template = (row.Draft ?? string.Empty).Trim();
        if (template.Length == 0)
            _overrides.Remove(key);   // cleared → no override
        else
            _overrides[key] = template;
        Rebuild();
    }

    [RelayCommand]
    private void Remove(EnvKeyViewModel? row)
    {
        if (row is null)
            return;
        var key = row.Key.Trim();
        _overrides.Remove(key);
        // A hand-added key with nothing declaring it disappears once its override is gone.
        if (!_fileKeys.Contains(key))
            _customKeys.Remove(key);
        Rebuild();
    }

    [RelayCommand]
    private void AddKey()
    {
        var key = NewKey.Trim();
        NewKey = "";
        if (key.Length == 0 || _fileKeys.Contains(key) || _customKeys.Contains(key))
            return;   // blank or already listed — nothing to add
        _customKeys.Add(key);
        Rebuild();
    }

    // -- projection -----------------------------------------------------------

    private void Rebuild()
    {
        Keys.Clear();
        var rowByKey = new Dictionary<string, EnvKeyViewModel>(StringComparer.Ordinal);
        foreach (var key in OrderedKeys())
        {
            var applied = _overrides.TryGetValue(key, out var template);
            var row = new EnvKeyViewModel
            {
                Key = key,
                Examples = GroupExamples(ExamplesFor(key)),
                IsApplied = applied,
                Display = applied ? template! : "override",
                // Unset keys open on a guess when an example hard-codes a local port (else empty).
                Draft = applied ? template! : (GuessDraft(key) ?? string.Empty),
                ReferencesUnknownInput = applied && ReferencesUnknownInput(template!),
            };
            Keys.Add(row);
            rowByKey[key] = row;
        }

        Overrides.Clear();
        foreach (var key in OrderedKeys().Where(_overrides.ContainsKey))
            Overrides.Add(new EnvOverrideViewModel
            {
                Key = key,
                Rewrite = _overrides[key],
                ReferencesUnknownInput = ReferencesUnknownInput(_overrides[key]),
                Row = rowByKey.GetValueOrDefault(key),
            });

        OnPropertyChanged(nameof(OverrideCount));
        OnPropertyChanged(nameof(HasOverrides));
        OnPropertyChanged(nameof(HasKeys));
        OverridesChanged?.Invoke(this, EventArgs.Empty);
    }

    // File/template keys first (declaration order), then hand-added keys, de-duplicated.
    private IEnumerable<string> OrderedKeys()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in _fileKeys)
            if (seen.Add(k)) yield return k;
        foreach (var k in _customKeys)
            if (seen.Add(k)) yield return k;
    }

    private IReadOnlyList<EnvExample> ExamplesFor(string key)
        => _examples.TryGetValue(key, out var list) ? list : [];

    // If an example value for this key hard-codes a local port (a localhost URL or connection
    // string), pre-fill the editor with that port templated to a declared input — one-click templating
    // for the common case. Null when nothing local is found (or the input to use is ambiguous).
    private string? GuessDraft(string key)
    {
        var inputs = Variables.ToList();
        foreach (var example in ExamplesFor(key))
            if (LocalPortGuess.Rewrite(example.Value, inputs) is { } guess)
                return guess;
        return null;
    }

    // Collapse example values that repeat across files into one entry, listing the sharing files under
    // a single header (e.g. ".env.local, .env.example" → "8080") so the same value isn't shown twice.
    private static IReadOnlyList<EnvExampleGroup> GroupExamples(IReadOnlyList<EnvExample> examples)
    {
        var order = new List<string>();
        var sourcesByValue = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in examples)
        {
            if (!sourcesByValue.TryGetValue(e.Value, out var sources))
            {
                sourcesByValue[e.Value] = sources = [];
                order.Add(e.Value);
            }
            if (!sources.Contains(e.Source)) sources.Add(e.Source);
        }
        return order.Select(v => new EnvExampleGroup(string.Join(", ", sourcesByValue[v]), v)).ToList();
    }
}

/// <summary>An example value plus the file(s) it appears in — one row in a key's example list, with
/// files that share a value combined under a single header.</summary>
public sealed record EnvExampleGroup(string Sources, string Value);

/// <summary>One key in the merged env view: its example values and any applied override.</summary>
public partial class EnvKeyViewModel : ObservableObject
{
    public string Key { get; init; } = string.Empty;

    /// <summary>The values this key already has in the target file and its templates (files that share
    /// a value are combined into one entry).</summary>
    public IReadOnlyList<EnvExampleGroup> Examples { get; init; } = [];
    public bool HasExamples => Examples.Count > 0;

    public bool IsApplied { get; init; }

    /// <summary>True when the applied template names a <c>${sprig.*}</c> input that isn't declared —
    /// the value renders red instead of accent to match the token editor's invalid highlight.</summary>
    public bool ReferencesUnknownInput { get; init; }

    /// <summary>What the value shows in the file view: the override template, or a dimmed <c>override</c>.</summary>
    public string Display { get; init; } = "override";

    /// <summary>The editor's working value, two-way bound to the token box (empty until first set).</summary>
    [ObservableProperty] private string _draft = string.Empty;

    /// <summary>Placeholder for the value editor: the first example value found, else the generic hint.</summary>
    public string ValueWatermark => Examples.Count > 0 ? Examples[0].Value : "${sprig.input}";

    public string ExamplesHeader => $"Example values for {Key}";
}

/// <summary>An applied env override, shown in the REPLACEMENTS inspector.</summary>
public sealed class EnvOverrideViewModel
{
    public string Key { get; init; } = string.Empty;
    public string Rewrite { get; init; } = string.Empty;

    /// <summary>True when the template names an undeclared <c>${sprig.*}</c> input — rendered red.</summary>
    public bool ReferencesUnknownInput { get; init; }

    public EnvKeyViewModel? Row { get; init; }
}
