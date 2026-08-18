using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Git;

namespace Sprig.App.ViewModels;

/// <summary>One editable <c>setup</c> command — a free-form line run at the worktree root.</summary>
public partial class SetupCommandRow : ObservableObject
{
    readonly Action<SetupCommandRow> _remove;
    public SetupCommandRow(Action<SetupCommandRow> remove) => _remove = remove;

    [ObservableProperty] private string _command = "";

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>The single, permanent PORT of a provided capability — the one real resource a repo owns, an
/// auto-allocated host port optionally pinned to an <see cref="Allowed"/> set. Every capability exports
/// exactly one, always named <c>port</c>; it can't be added, removed or renamed, so it's the fixed anchor
/// the whole <c>${sprig.&lt;cap&gt;.…}</c> hierarchy hangs off. <see cref="Ref"/> renders the exact token.</summary>
public partial class PortEditRow : ObservableObject
{
    /// <summary>Fixed at <c>port</c> — never editable.</summary>
    public string Name => "port";

    /// <summary>Optional restriction spec (e.g. <c>8100-8103</c>); blank = the whole settings range.</summary>
    [ObservableProperty] private string _allowed = "";

    /// <summary>The owning capability name, pushed down by the parent so the row can render its full token.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Ref))]
    private string _head = "";

    /// <summary>The reference a consumer types for this port: <c>${sprig.&lt;head&gt;.port}</c>.</summary>
    public string Ref => ProvideEditRow.Token(Head, Name);
}

/// <summary>One editable derived SHAPE of a provided capability (map model): a string <see cref="Template"/>
/// built over the capability's ports (a url, a connString). <see cref="Ref"/> renders the exact
/// <c>${sprig.&lt;head&gt;.&lt;name&gt;}</c> a consumer types.</summary>
public partial class ShapeEditRow : ObservableObject
{
    static readonly IReadOnlyList<string> NoOpenCapabilities = [];

    readonly Action<ShapeEditRow> _remove;

    public ShapeEditRow(Action<ShapeEditRow> remove) => _remove = remove;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Ref))]
    private string _name = "";

    /// <summary>The template (e.g. <c>http://localhost:${sprig.vite-server.port}</c>).</summary>
    [ObservableProperty] private string _template = "";

    /// <summary>The owning capability name, pushed down by the parent (see <see cref="PortEditRow.Head"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Ref))]
    private string _head = "";

    /// <summary>The reference a consumer types for this shape: <c>${sprig.&lt;head&gt;.&lt;name&gt;}</c>.</summary>
    public string Ref => ProvideEditRow.Token(Head, Name);

    /// <summary>The names this shape's template may reference — its capability's <c>port</c>, its SIBLING
    /// shapes (never itself), and <c>workspace</c>. Owned here and kept in step by the parent, so the
    /// template field's autocomplete offers exactly the legal references and can't suggest a self-reference.</summary>
    public ObservableCollection<string> Variables { get; } = [];

    /// <summary>A shape references only its own capability, so there are no open (cross-repo) heads.</summary>
    public IReadOnlyList<string> OpenCapabilities => NoOpenCapabilities;

    /// <summary>Set by the parent when this shape's template self-references, references out of scope, or is
    /// part of a cycle — shown inline under the field. Null when the template is fine.</summary>
    [ObservableProperty] private string? _referenceError;

    /// <summary>Replace <see cref="Variables"/> in place (equality-guarded) so bound token boxes update live.</summary>
    public void SetScope(IReadOnlyList<string> names)
    {
        if (Variables.SequenceEqual(names, StringComparer.Ordinal)) return;
        Variables.Clear();
        foreach (var n in names) Variables.Add(n);
    }

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>One editable provided capability, shaped for the "one name, many shapes" editor: a contract
/// <see cref="Capability"/> name (the head of every <c>${sprig.&lt;name&gt;.…}</c> reference), its single
/// permanent <see cref="Port"/> (the anchor — the real resource, always exported), and its derived
/// <see cref="Shapes"/> (formulas over that port).</summary>
public partial class ProvideEditRow : ObservableObject
{
    readonly Action<ProvideEditRow> _remove;

    public ProvideEditRow(Action<ProvideEditRow> remove)
    {
        _remove = remove;
        Shapes.CollectionChanged += OnShapesChanged;
        Refresh();
    }

    /// <summary>The service name — name the service (<c>vite-server</c>), not one of its shapes.</summary>
    [ObservableProperty] private string _capability = "";

    /// <summary>The one, always-present port anchor (fixed name <c>port</c>).</summary>
    public PortEditRow Port { get; } = new();

    public ObservableCollection<ShapeEditRow> Shapes { get; } = [];

    /// <summary>Keep the port token, every shape's token, each shape's autocomplete scope, and the live
    /// reference errors in step as the capability name is typed.</summary>
    partial void OnCapabilityChanged(string value)
    {
        Port.Head = value;
        foreach (var s in Shapes) s.Head = value;
        Refresh();
    }

    /// <summary>A fresh derived-shape row wired to this capability. Its autocomplete scope + errors are filled
    /// in by <see cref="Refresh"/>.</summary>
    public ShapeEditRow NewShape() => new(r => Shapes.Remove(r)) { Head = Capability };

    [RelayCommand] private void AddShape() => Shapes.Add(NewShape());

    [RelayCommand] private void Remove() => _remove(this);

    void OnShapesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (ShapeEditRow s in e.OldItems) s.PropertyChanged -= OnShapeFieldChanged;
        if (e.NewItems is not null) foreach (ShapeEditRow s in e.NewItems) s.PropertyChanged += OnShapeFieldChanged;
        Refresh();
    }

    void OnShapeFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A rename moves the reference surface (autocomplete scopes) AND can change errors; a template edit
        // only changes errors.
        if (e.PropertyName == nameof(ShapeEditRow.Name)) Refresh();
        else if (e.PropertyName == nameof(ShapeEditRow.Template)) RefreshShapeErrors();
    }

    void Refresh()
    {
        RefreshShapeScopes();
        RefreshShapeErrors();
    }

    /// <summary>Give each shape its own autocomplete scope: <c>workspace</c>, this capability's <c>port</c>,
    /// and its SIBLING shapes — deliberately excluding the shape itself, so it can never autocomplete to a
    /// self-reference.</summary>
    void RefreshShapeScopes()
    {
        var cap = Capability.Trim();
        foreach (var shape in Shapes)
        {
            var wanted = new List<string> { "workspace" };
            if (cap.Length > 0)
            {
                wanted.Add($"{cap}.port");
                foreach (var sibling in Shapes)
                {
                    if (ReferenceEquals(sibling, shape)) continue;   // never offer this shape its own name
                    var n = sibling.Name.Trim();
                    if (n.Length > 0) wanted.Add($"{cap}.{n}");
                }
            }
            shape.SetScope(wanted);
        }
    }

    /// <summary>Flag each shape whose template self-references, references out of scope, or is part of a cycle
    /// — the same rule Save enforces (<see cref="Sprig.Core.Config.ConfigReferences.ShapeReferenceIssues"/>),
    /// surfaced live under the offending field.</summary>
    void RefreshShapeErrors()
    {
        var cap = Capability.Trim();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (cap.Length > 0)
        {
            var shapes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in Shapes)
            {
                var n = s.Name.Trim();
                if (n.Length > 0) shapes[n] = s.Template ?? "";
            }
            foreach (var (shape, message) in Sprig.Core.Config.ConfigReferences.ShapeReferenceIssues(cap, shapes))
                errors.TryAdd(shape, message);   // first problem per shape
        }
        foreach (var s in Shapes)
            s.ReferenceError = errors.GetValueOrDefault(s.Name.Trim());
    }

    /// <summary>The reference token for an output <paramref name="name"/> under <paramref name="head"/>:
    /// <c>${sprig.head.name}</c>, with a <c>…</c> placeholder while either half is still blank.</summary>
    public static string Token(string? head, string? name)
    {
        var h = string.IsNullOrWhiteSpace(head) ? "…" : head!.Trim();
        var n = string.IsNullOrWhiteSpace(name) ? "…" : name!.Trim();
        return $"${{sprig.{h}.{n}}}";
    }
}

/// <summary>One editable needed capability (map model): the contract name and an optional local alias.</summary>
public partial class NeedEditRow : ObservableObject
{
    readonly Action<NeedEditRow> _remove;
    public NeedEditRow(Action<NeedEditRow> remove) => _remove = remove;

    [ObservableProperty] private string _capability = "";
    [ObservableProperty] private string _as = "";

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>One editable template file path an env override seeds from.</summary>
public partial class TemplateFileRow : ObservableObject
{
    readonly Action<TemplateFileRow> _remove;
    readonly Func<string, bool> _exists;

    public TemplateFileRow(Action<TemplateFileRow> remove, Func<string, bool> exists)
    {
        _remove = remove;
        _exists = exists;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFound), nameof(ShowMissing))]
    private string _path = "";

    bool Entered => !string.IsNullOrWhiteSpace(Path);

    /// <summary>Green ✓: the template exists in the repo.</summary>
    public bool ShowFound => Entered && _exists(Path);
    /// <summary>Amber ⚠: a path was entered but no such file exists (it'll be skipped when seeding).</summary>
    public bool ShowMissing => Entered && !_exists(Path);

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>How a targeted env file relates to git — drives the override-safety subtext.</summary>
public enum EnvFileStatus
{
    /// <summary>Nothing entered yet.</summary>
    Empty,
    /// <summary>Committed to git. Overriding it would leave the worktree dirty — blocked on save.</summary>
    Tracked,
    /// <summary>Matched by .gitignore, so invisible to git — the safe target.</summary>
    Ignored,
    /// <summary>Exists but isn't ignored — overriding it would surface as a worktree change.</summary>
    NotIgnored,
    /// <summary>No such file and not ignored — sprig would create a worktree-visible untracked file.</summary>
    NotIgnoredNew,
}

/// <summary>One editable <c>.env.*</c> override: the target file, its seed templates, and an
/// interactive merged-env overlay for choosing which keys to clobber (the env analogue of
/// <see cref="ComposeFileEditRow"/>).</summary>
public partial class EnvFileEditRow : ObservableObject
{
    readonly Action<EnvFileEditRow> _remove;
    readonly Func<string, CancellationToken, Task<EnvFileStatus>> _classify;
    readonly Func<string, IReadOnlyList<string>> _keysFor;
    readonly Func<string, IReadOnlyDictionary<string, IReadOnlyList<EnvExample>>> _examplesFor;
    readonly Func<string, bool> _exists;
    readonly IEnumerable<string> _variables;
    readonly IEnumerable<string> _openCapabilities;
    CancellationTokenSource? _cts;

    /// <summary>The saved overrides (KEY→template) this row was loaded with — the overlay's fallback
    /// seed until it (re)builds, and what <see cref="CurrentSet"/> returns before the overlay exists.</summary>
    IReadOnlyDictionary<string, string> _seedSet = new Dictionary<string, string>();

    public EnvFileEditRow(
        Action<EnvFileEditRow> remove,
        Func<string, CancellationToken, Task<EnvFileStatus>> classify,
        Func<string, IReadOnlyList<string>> keysFor,
        Func<string, IReadOnlyDictionary<string, IReadOnlyList<EnvExample>>> examplesFor,
        Func<string, bool> exists,
        IEnumerable<string> variables,
        IEnumerable<string> openCapabilities)
    {
        _remove = remove;
        _classify = classify;
        _keysFor = keysFor;
        _examplesFor = examplesFor;
        _exists = exists;
        _variables = variables;
        _openCapabilities = openCapabilities;
        // Seed templates contribute their own keys to the merged view, so re-gather when they change.
        Templates.CollectionChanged += OnTemplatesChanged;
    }

    [ObservableProperty] private string _file = "";

    /// <summary>The interactive merged-env editor for this file (null until a file path is entered).</summary>
    [ObservableProperty] private EnvOverlayViewModel? _overlay;

    /// <summary>Template files this override seeds the worktree's copy from (optional, ordered).</summary>
    public ObservableCollection<TemplateFileRow> Templates { get; } = [];

    /// <summary>Whether an overlay exists to show (only once a file path is entered).</summary>
    public bool HasOverlay => Overlay is not null;

    /// <summary>Raised when this file's applied overrides change (bubbled from the overlay, and re-fired
    /// when the overlay itself is rebuilt) — the repo editor listens to keep the quick-add list current.</summary>
    public event EventHandler? OverridesChanged;

    /// <summary>The overrides to persist: whatever the live overlay holds, else the loaded seed.</summary>
    public IReadOnlyDictionary<string, string> CurrentSet => Overlay?.ToSet() ?? _seedSet;

    /// <summary>Populate the row from a loaded config entry (sets the seed, then the file path).</summary>
    public void Seed(string file, IReadOnlyDictionary<string, string> set)
    {
        _seedSet = set;
        if (File == file) Reclassify();   // no OnFileChanged to trigger it
        else File = file;
    }

    /// <summary>Git relationship of <see cref="File"/>; recomputed off the UI thread as it changes
    /// (the gitignore probe shells out to git, so it can't run inline on the keystroke).</summary>
    [ObservableProperty] private EnvFileStatus _status;

    /// <summary>The in-flight classification, exposed so tests can await it deterministically.</summary>
    public Task StatusReady { get; private set; } = Task.CompletedTask;

    /// <summary>Red: tracked, so overriding is disallowed.</summary>
    public bool ShowTrackedWarning => Status == EnvFileStatus.Tracked;

    /// <summary>Green: gitignored, the safe case.</summary>
    public bool ShowIgnoredOk => Status == EnvFileStatus.Ignored;

    /// <summary>Amber: not tracked but not ignored either — allowed, but would dirty the worktree.</summary>
    public bool ShowNotIgnoredWarning => Status is EnvFileStatus.NotIgnored or EnvFileStatus.NotIgnoredNew;

    /// <summary>The amber warning text — distinguishes an existing file from a path with no match.</summary>
    public string NotIgnoredMessage => Status switch
    {
        EnvFileStatus.NotIgnored =>
            "⚠ Not gitignored — overriding this file would show up as a change in every worktree.",
        EnvFileStatus.NotIgnoredNew =>
            "⚠ No matching file, and this path isn't gitignored — sprig would create an untracked file that dirties the worktree.",
        _ => "",
    };

    partial void OnFileChanged(string value) => Reclassify();

    /// <summary>Re-evaluate git status + overlay — used when the owning module's path changes, since the
    /// file resolves under it (the classify/keys callbacks read the module path live).</summary>
    public void Refresh() => Reclassify();

    /// <summary>Recompute the git status and rebuild the merged-env overlay off the UI thread. Runs on
    /// a <see cref="File"/> edit and whenever the seed templates change (they add their own keys).</summary>
    void Reclassify()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        StatusReady = ClassifyAsync(File, cts.Token);
    }

    void OnTemplatesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TemplateFileRow r in e.OldItems) r.PropertyChanged -= OnTemplateRowChanged;
        if (e.NewItems is not null)
            foreach (TemplateFileRow r in e.NewItems) r.PropertyChanged += OnTemplateRowChanged;
        Reclassify();
    }

    void OnTemplateRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TemplateFileRow.Path)) Reclassify();
    }

    async Task ClassifyAsync(string file, CancellationToken ct)
    {
        EnvFileStatus result;
        IReadOnlyList<string> keys;
        IReadOnlyDictionary<string, IReadOnlyList<EnvExample>> examples;
        try
        {
            result = await _classify(file, ct);
            var templatePaths = Templates.Select(t => t.Path).ToList();  // snapshot on the UI thread
            (keys, examples) = await Task.Run(
                () => (GatherKeys(file, templatePaths), GatherExamples(file, templatePaths)), ct);
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        Status = result;

        // (Re)build the overlay from the freshly gathered keys/examples, carrying forward whatever
        // overrides the live overlay already holds (else the loaded seed) — so editing the file path
        // or templates never drops in-progress overrides.
        var seed = Overlay?.ToSet() ?? _seedSet;
        Overlay = new EnvOverlayViewModel(keys, examples, seed, _variables, _openCapabilities);
    }

    /// <summary>The keys shown in the merged env view: the union of those declared in the target file
    /// and in each configured seed template, in first-seen order.</summary>
    IReadOnlyList<string> GatherKeys(string file, IReadOnlyList<string> templatePaths)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(IEnumerable<string> ks) { foreach (var k in ks) if (seen.Add(k)) keys.Add(k); }

        Add(_keysFor(file));
        foreach (var t in templatePaths)
            if (!string.IsNullOrWhiteSpace(t)) Add(_keysFor(t));
        return keys;
    }

    /// <summary>Example values to show per key: the union of those declared in the target file and
    /// each configured seed template (first source wins per key, one example per source file).</summary>
    IReadOnlyDictionary<string, IReadOnlyList<EnvExample>> GatherExamples(string file, IReadOnlyList<string> templatePaths)
    {
        var map = new Dictionary<string, List<EnvExample>>(StringComparer.Ordinal);
        void Merge(IReadOnlyDictionary<string, IReadOnlyList<EnvExample>> src)
        {
            foreach (var (key, examples) in src)
            {
                if (!map.TryGetValue(key, out var list))
                    map[key] = list = [];
                foreach (var ex in examples)
                    if (!list.Any(e => e.Source == ex.Source)) list.Add(ex);
            }
        }

        Merge(_examplesFor(file));
        foreach (var t in templatePaths)
            if (!string.IsNullOrWhiteSpace(t)) Merge(_examplesFor(t));
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EnvExample>)kv.Value, StringComparer.Ordinal);
    }

    partial void OnStatusChanged(EnvFileStatus value)
    {
        OnPropertyChanged(nameof(ShowTrackedWarning));
        OnPropertyChanged(nameof(ShowIgnoredOk));
        OnPropertyChanged(nameof(ShowNotIgnoredWarning));
        OnPropertyChanged(nameof(NotIgnoredMessage));
    }

    partial void OnOverlayChanged(EnvOverlayViewModel? oldValue, EnvOverlayViewModel? newValue)
    {
        OnPropertyChanged(nameof(HasOverlay));
        if (oldValue is not null) oldValue.OverridesChanged -= BubbleOverridesChanged;
        if (newValue is not null) newValue.OverridesChanged += BubbleOverridesChanged;
        // A rebuilt overlay may reference different inputs, so re-announce.
        OverridesChanged?.Invoke(this, EventArgs.Empty);
    }

    void BubbleOverridesChanged(object? sender, EventArgs e) => OverridesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>A fresh template row wired to this env row's module-aware existence check, so its ✓/⚠
    /// hint resolves the path under the owning module (not the repo root). Used both when adding a row
    /// and when hydrating saved templates on load.</summary>
    public TemplateFileRow NewTemplateRow() => new(r => Templates.Remove(r), _exists);

    [RelayCommand] private void Remove() => _remove(this);
    [RelayCommand] private void AddTemplate() => Templates.Add(NewTemplateRow());
}

/// <summary>One editable docker-compose override: the target file plus its own interactive overlay.</summary>
public partial class ComposeFileEditRow : ObservableObject
{
    readonly Action<ComposeFileEditRow> _remove;
    readonly Func<string, bool> _exists;
    readonly Func<string, string> _readText;
    readonly Func<string, string, IReadOnlyList<ComposeOverride>> _detect;
    readonly IEnumerable<string> _variables;
    readonly IEnumerable<string> _openCapabilities;

    /// <summary>Overrides loaded from disk — seeds the overlay's first build so a missing/blank path
    /// doesn't drop them before the file is (re)supplied. Null <em>only</em> for a hand-added row (never
    /// hydrated from a config), which is exactly the row that gets add-time auto-detection.</summary>
    IReadOnlyList<ComposeOverride>? _seed;

    /// <summary>Compose file paths we've already auto-detected, so re-running <see cref="Rebuild"/> (e.g.
    /// when the module path changes) never re-proposes overrides — least of all over ones since edited.</summary>
    readonly HashSet<string> _autoDetected = new(StringComparer.OrdinalIgnoreCase);

    public ComposeFileEditRow(
        Action<ComposeFileEditRow> remove,
        Func<string, bool> exists,
        Func<string, string> readText,
        Func<string, string, IReadOnlyList<ComposeOverride>> detect,
        IEnumerable<string> variables,
        IEnumerable<string> openCapabilities)
    {
        _remove = remove;
        _exists = exists;
        _readText = readText;
        _detect = detect;
        _variables = variables;
        _openCapabilities = openCapabilities;
    }

    [ObservableProperty] private string _file = "";

    /// <summary>True when the named compose file exists in the repo — the override target must exist.</summary>
    [ObservableProperty] private bool _found;

    /// <summary>The interactive compose editor for this file — renders the source with clickable tokens.</summary>
    [ObservableProperty] private ComposeOverlayViewModel? _overlay;

    public bool FileEntered => !string.IsNullOrWhiteSpace(File);
    public bool ShowFound => FileEntered && Found;
    public bool ShowMissing => FileEntered && !Found;

    /// <summary>Whether an overlay exists to show (only once a file path is entered).</summary>
    public bool HasOverlay => Overlay is not null;

    /// <summary>Raised when this file's applied overrides change (bubbled from the overlay, and re-fired
    /// when the overlay itself is rebuilt) — the repo editor listens to keep the quick-add list current.</summary>
    public event EventHandler? OverridesChanged;

    /// <summary>The overrides to persist: whatever the live overlay holds, else the disk seed.</summary>
    public IReadOnlyList<ComposeOverride> CurrentOverrides => Overlay?.ToOverrides() ?? _seed ?? [];

    partial void OnFileChanged(string value) => Rebuild();

    /// <summary>Re-evaluate existence + overlay — used when the owning module's path changes, since the
    /// compose file resolves under it (the exists/read callbacks read the module path live).</summary>
    public void Refresh() => Rebuild();

    partial void OnFoundChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFound));
        OnPropertyChanged(nameof(ShowMissing));
    }

    partial void OnOverlayChanged(ComposeOverlayViewModel? oldValue, ComposeOverlayViewModel? newValue)
    {
        OnPropertyChanged(nameof(HasOverlay));
        if (oldValue is not null) oldValue.OverridesChanged -= BubbleOverridesChanged;
        if (newValue is not null) newValue.OverridesChanged += BubbleOverridesChanged;
        // A rebuilt overlay may reference different inputs, so re-announce.
        OverridesChanged?.Invoke(this, EventArgs.Empty);
    }

    void BubbleOverridesChanged(object? sender, EventArgs e) => OverridesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Populate the row from a loaded config entry (sets the disk seed, then the file).</summary>
    public void Seed(string file, IReadOnlyList<ComposeOverride> overrides)
    {
        _seed = overrides;
        if (File == file) Rebuild();   // no OnFileChanged to trigger it
        else File = file;
    }

    /// <summary>(Re)build the overlay from the current file, carrying forward any live/seed overrides.</summary>
    void Rebuild()
    {
        var rel = File.Trim();
        try { Found = rel.Length > 0 && _exists(rel); }
        catch { Found = false; }
        OnPropertyChanged(nameof(FileEntered));

        if (rel.Length == 0) { Overlay = null; return; }

        var seed = Overlay?.ToOverrides() ?? _seed;
        var text = "";
        try { text = _readText(rel); }
        catch { /* unreadable → empty overlay; the ✓/⚠ subtext already flags a missing file */ }

        // A hand-added row (never seeded from a config) gets the same auto-detection the initial add runs,
        // the first time it points at a real compose file with nothing overridden yet: propose the
        // container-name/port rewrites and declare their inputs. Only once per path, and never when there
        // are already overrides — so it seeds a blank slate but doesn't clobber the user's edits.
        if (_seed is null && Found && (seed is null || seed.Count == 0) && _autoDetected.Add(rel))
        {
            var detected = _detect(text, rel);
            if (detected.Count > 0) seed = detected;
        }

        Overlay = new ComposeOverlayViewModel(text, seed, _variables, _openCapabilities);
    }

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>
/// One editable module tab: its name, its path (subdirectory), and its own provides / needs / env /
/// compose / setup rows. The env/compose rows reuse the owner's git-safety + overlay machinery,
/// prepending this module's path (read live) so the checks resolve under it; changing the path
/// re-evaluates every row.
/// </summary>
public partial class ModuleEditTab : ObservableObject
{
    readonly RepoEditViewModel _owner;

    public ModuleEditTab(RepoEditViewModel owner, string name, string path)
    {
        _owner = owner;
        _name = name;
        _path = path;
        Env.CollectionChanged += OnOverrideRowsChanged;
        Compose.CollectionChanged += OnOverrideRowsChanged;
        // Provides/needs edits change the repo's ${sprig.*} reference surface — watch them (rows and their
        // fields) so the owner can keep the token box's variable/capability lists live as you type.
        Provides.CollectionChanged += OnProvidesChanged;
        Needs.CollectionChanged += OnNeedsChanged;
    }

    /// <summary>Module name — the tab label, and the module's identity (unique within the repo).</summary>
    [ObservableProperty] private string _name;

    /// <summary>Optional subdirectory the module lives in (e.g. <c>apps/web</c>); its files resolve under it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPathFound), nameof(ShowPathMissing))]
    private string _path;

    /// <summary>Green ✓: the module's path names a directory that exists in the repo.</summary>
    public bool ShowPathFound => Path.Trim().Length > 0 && _owner.RepoDirExists(Path);

    /// <summary>Amber ⚠: a path was entered but no such directory exists (informational — doesn't block save).</summary>
    public bool ShowPathMissing => Path.Trim().Length > 0 && !_owner.RepoDirExists(Path);

    /// <summary>Map model: capabilities this module provides / needs.</summary>
    public ObservableCollection<ProvideEditRow> Provides { get; } = [];
    public ObservableCollection<NeedEditRow> Needs { get; } = [];

    public ObservableCollection<EnvFileEditRow> Env { get; } = [];
    public ObservableCollection<ComposeFileEditRow> Compose { get; } = [];
    public ObservableCollection<SetupCommandRow> Setup { get; } = [];

    /// <summary>Raised whenever this module's env/compose overrides change (rows added/removed/edited), so
    /// the owner can recompute the shared "referenced but not declared" input hint across every module.</summary>
    public event EventHandler? OverridesChanged;

    /// <summary>Raised whenever this module's provides/needs surface changes — a capability/output/alias added,
    /// removed, or renamed. The owner rebuilds the ${sprig.*} reference lists off this. Deliberately separate
    /// from <see cref="OverridesChanged"/>: those are what the overlays react to, and rebuilding the reference
    /// lists from that path would re-enter through the overlays and recurse.</summary>
    public event EventHandler? CapabilitySurfaceChanged;

    // The file resolves under the module path, so moving the module re-runs every row's git-safety and
    // existence check, and its overrides may now name different inputs.
    partial void OnPathChanged(string value)
    {
        foreach (var e in Env) e.Refresh();
        foreach (var c in Compose) c.Refresh();
        OverridesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A fresh env row wired to this module (removal, and path-aware git/keys/exists callbacks).</summary>
    public EnvFileEditRow NewEnvRow() => new(
        r => Env.Remove(r),
        (f, ct) => _owner.ClassifyEnvFileAsync(RepoEditViewModel.JoinPath(Path, f), ct),
        f => _owner.EnvKeysFor(RepoEditViewModel.JoinPath(Path, f)),
        f => _owner.EnvExamplesFor(RepoEditViewModel.JoinPath(Path, f)),
        f => _owner.RepoFileExists(RepoEditViewModel.JoinPath(Path, f)),
        _owner.SprigVariableNames, _owner.SprigNeededCapabilities);

    /// <summary>A fresh compose row wired to this module (removal, path-aware exists/read callbacks, and
    /// the add-time auto-detection that pre-fills a hand-added file's overrides).</summary>
    public ComposeFileEditRow NewComposeRow() => new(
        r => Compose.Remove(r),
        f => _owner.RepoFileExists(RepoEditViewModel.JoinPath(Path, f)),
        f => _owner.ReadRepoFile(RepoEditViewModel.JoinPath(Path, f)),
        _owner.DetectComposeOverrides,
        _owner.SprigVariableNames, _owner.SprigNeededCapabilities);

    // A fresh capability already carries its permanent port anchor — the user only names it and, optionally,
    // adds derived shapes (each scoped to this capability's own outputs).
    [RelayCommand] private void AddProvide() => Provides.Add(new ProvideEditRow(r => Provides.Remove(r)));

    [RelayCommand] private void AddNeed() => Needs.Add(new NeedEditRow(r => Needs.Remove(r)));
    [RelayCommand] private void AddEnvFile() => Env.Add(NewEnvRow());
    [RelayCommand] private void AddComposeFile() => Compose.Add(NewComposeRow());
    [RelayCommand] private void AddSetupCommand() => Setup.Add(new SetupCommandRow(r => Setup.Remove(r)));

    /// <summary>Delete this whole module (allowed down to zero, so a repo can be rebuilt from scratch).</summary>
    [RelayCommand] private void Remove() => _owner.RemoveModule(this);

    void OnOverrideRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (var i in e.OldItems) Unsubscribe(i);
        if (e.NewItems is not null) foreach (var i in e.NewItems) Subscribe(i);
        OverridesChanged?.Invoke(this, EventArgs.Empty);

        void Subscribe(object? i)
        {
            if (i is EnvFileEditRow ef) ef.OverridesChanged += Bubble;
            else if (i is ComposeFileEditRow cf) cf.OverridesChanged += Bubble;
        }
        void Unsubscribe(object? i)
        {
            if (i is EnvFileEditRow ef) ef.OverridesChanged -= Bubble;
            else if (i is ComposeFileEditRow cf) cf.OverridesChanged -= Bubble;
        }
    }

    void Bubble(object? sender, EventArgs e) => OverridesChanged?.Invoke(this, EventArgs.Empty);

    // -- provides/needs surface tracking (for the live ${sprig.*} reference lists) ---------------------

    void OnProvidesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (ProvideEditRow p in e.OldItems) UnsubscribeProvide(p);
        if (e.NewItems is not null) foreach (ProvideEditRow p in e.NewItems) SubscribeProvide(p);
        RaiseCapabilitySurfaceChanged();
    }

    // The port anchor never moves the reference surface (its name is the fixed "port"), so only the
    // capability rename and the derived shapes are watched.
    void SubscribeProvide(ProvideEditRow p)
    {
        p.PropertyChanged += OnProvideFieldChanged;          // Capability rename
        p.Shapes.CollectionChanged += OnOutputsChanged;
        foreach (var s in p.Shapes) s.PropertyChanged += OnOutputFieldChanged;
    }

    void UnsubscribeProvide(ProvideEditRow p)
    {
        p.PropertyChanged -= OnProvideFieldChanged;
        p.Shapes.CollectionChanged -= OnOutputsChanged;
        foreach (var s in p.Shapes) s.PropertyChanged -= OnOutputFieldChanged;
    }

    void OnOutputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (INotifyPropertyChanged o in e.OldItems) o.PropertyChanged -= OnOutputFieldChanged;
        if (e.NewItems is not null) foreach (INotifyPropertyChanged o in e.NewItems) o.PropertyChanged += OnOutputFieldChanged;
        RaiseCapabilitySurfaceChanged();
    }

    void OnProvideFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProvideEditRow.Capability)) RaiseCapabilitySurfaceChanged();
    }

    // Only a rename of a port/shape moves the reference surface — a Head/Allowed/Template edit doesn't.
    void OnOutputFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PortEditRow.Name)) RaiseCapabilitySurfaceChanged();
    }

    void OnNeedsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (NeedEditRow n in e.OldItems) n.PropertyChanged -= OnNeedFieldChanged;
        if (e.NewItems is not null) foreach (NeedEditRow n in e.NewItems) n.PropertyChanged += OnNeedFieldChanged;
        RaiseCapabilitySurfaceChanged();
    }

    void OnNeedFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NeedEditRow.Capability) or nameof(NeedEditRow.As))
            RaiseCapabilitySurfaceChanged();
    }

    void RaiseCapabilitySurfaceChanged() => CapabilitySurfaceChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Editable view over a repo's <c>.sprig.json</c>. The repo <b>name</b> is intentionally not
/// editable here — it is the registry key, so renaming is a separate operation. Each <b>module</b> is a
/// tab with its own provides / needs / env / compose / setup.
/// </summary>
public partial class RepoEditViewModel : ObservableObject
{
    int _schema = SprigConfigLoader.SupportedSchema;

    /// <summary>Repo-relative paths (forward-slash) of every git-tracked file, for a cheap
    /// per-keystroke lookup. Populated once at <see cref="Load"/> from a single git call.</summary>
    readonly HashSet<string> _tracked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Git, kept for the gitignore probe used while classifying env files. Null in tests
    /// that don't pass one — then a file is only ever "tracked" (set-based) or "not ignored".</summary>
    IGitService? _git;

    RepoEditViewModel(string repoPath)
    {
        RepoPath = repoPath;
        // A module's provides/needs are the ${sprig.*} reference surface — track each module so the token
        // box's variable + capability lists stay current across every module as they're edited.
        Modules.CollectionChanged += OnModulesChanged;
        RefreshSprigVariableNames();
    }

    public string RepoPath { get; }

    /// <summary>The variables a repo template may reference verbatim — <c>workspace</c> and each self-provided
    /// <c>&lt;capability&gt;.&lt;output&gt;</c>. Drives <c>${sprig.*}</c> autocomplete and the invalid-reference
    /// highlight; kept in sync as provides are edited.</summary>
    public ObservableCollection<string> SprigVariableNames { get; } = [];

    /// <summary>Open capability heads (needs + aliases): a dotted <c>${sprig.&lt;head&gt;.&lt;output&gt;}</c> is
    /// valid whatever the output, because that output lives in another repo (resolved at map time). Feeds the
    /// token box's capability-aware highlight so a valid need-reference isn't flagged; kept live as needs are
    /// edited.</summary>
    public ObservableCollection<string> SprigNeededCapabilities { get; } = [];

    /// <summary>The logical repo name — shown for context, not editable (it keys the registry).</summary>
    public string Name { get; private set; } = "";

    /// <summary>The repo's module tabs — each with its own env / compose / setup. Add/remove down to zero.</summary>
    public ObservableCollection<ModuleEditTab> Modules { get; } = [];

    /// <summary>The module tab currently shown below the strip (null when there are none).</summary>
    [ObservableProperty] private ModuleEditTab? _selectedModule;

    /// <summary>True when at least one module exists — gates the module detail vs. the empty state.</summary>
    public bool HasModules => Modules.Count > 0;

    [ObservableProperty] private string? _error;

    public static RepoEditViewModel Load(string repoPath, IGitService? git = null)
    {
        var vm = new RepoEditViewModel(repoPath);
        var c = SprigConfigLoader.LoadFromFile(Path.Combine(repoPath, ".sprig.json"));

        vm._schema = c.Schema;
        vm.Name = c.Name;
        vm._git = git;

        if (git is not null)
            foreach (var f in git.ListTrackedFiles(repoPath))
                vm._tracked.Add(Normalize(f));

        // One tab per module; each hydrates its own env/compose/setup rows (path-aware via the tab).
        foreach (var m in c.EffectiveModules)
        {
            var tab = new ModuleEditTab(vm, m.Name, m.Path);
            foreach (var p in m.Provides)
            {
                var pr = new ProvideEditRow(r => tab.Provides.Remove(r)) { Capability = p.Capability };
                // One permanent port; adopt the allowed set from whatever port the config declared (if any).
                if (p.Ports.Count > 0) pr.Port.Allowed = p.Ports.First().Value.Allowed ?? "";
                foreach (var (shapeName, template) in p.Shapes)
                {
                    var sr = pr.NewShape();
                    sr.Name = shapeName;
                    sr.Template = template;
                    pr.Shapes.Add(sr);
                }
                tab.Provides.Add(pr);
            }
            foreach (var n in m.Needs)
                tab.Needs.Add(new NeedEditRow(r => tab.Needs.Remove(r)) { Capability = n.Capability, As = n.As ?? "" });
            foreach (var e in m.Env)
            {
                var row = tab.NewEnvRow();
                foreach (var t in e.Templates ?? [])
                {
                    // Use the row's module-aware existence check so a template under the module path
                    // (e.g. apps/web/.env.template) resolves correctly, not against the repo root.
                    var tr = row.NewTemplateRow();
                    tr.Path = t;
                    row.Templates.Add(tr);
                }
                row.Seed(e.File, e.Set);   // sets the seed overrides, then the file (which builds the overlay)
                tab.Env.Add(row);
            }
            foreach (var comp in m.Compose)
            {
                var row = tab.NewComposeRow();
                row.Seed(comp.File, comp.Overrides);
                tab.Compose.Add(row);
            }
            foreach (var cmd in m.Setup)
                tab.Setup.Add(new SetupCommandRow(r => tab.Setup.Remove(r)) { Command = cmd });
            vm.Modules.Add(tab);
        }
        vm.SelectedModule = vm.Modules.FirstOrDefault();

        vm.RefreshSprigVariableNames();   // pick up self-provided outputs now that modules are loaded
        return vm;
    }

    // -- modules ---------------------------------------------------------------

    void OnModulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ModuleEditTab t in e.OldItems)
                t.CapabilitySurfaceChanged -= OnModuleCapabilitySurfaceChanged;
        if (e.NewItems is not null)
            foreach (ModuleEditTab t in e.NewItems)
                t.CapabilitySurfaceChanged += OnModuleCapabilitySurfaceChanged;
        OnPropertyChanged(nameof(HasModules));
        RefreshSprigVariableNames();   // a module added/removed changes the provides/needs surface
    }

    // A provide/need/output was added, removed, or renamed: rebuild the ${sprig.*} reference lists live so
    // the token box greens a freshly-typed capability reference. Driven by the provides/needs rows, not by
    // the overlays — RefreshSprigVariableNames only mutates the reference collections (guarded by equality),
    // so the overlays' reaction to that never loops back here.
    void OnModuleCapabilitySurfaceChanged(object? sender, EventArgs e) => RefreshSprigVariableNames();

    /// <summary>Set by <see cref="AddModule"/> so the view knows to focus the name box of the module it
    /// just added (and leaves it blank for the user to type). The view reads this once, as the new tab's
    /// editor loads, then clears it — switching between existing tabs must not steal focus.</summary>
    public bool FocusNewModuleRequested { get; set; }

    /// <summary>Add a new module and select it. The name is left blank on purpose — the user names it — and
    /// the view autofocuses the name box (see <see cref="FocusNewModuleRequested"/>).</summary>
    [RelayCommand]
    private void AddModule()
    {
        var tab = new ModuleEditTab(this, "", "");
        Modules.Add(tab);
        SelectedModule = tab;
        FocusNewModuleRequested = true;
    }

    /// <summary>Remove a module tab (allowed down to zero); keep a sensible tab selected.</summary>
    public void RemoveModule(ModuleEditTab tab)
    {
        var idx = Modules.IndexOf(tab);
        if (idx < 0) return;
        Modules.Remove(tab);
        if (SelectedModule is null || ReferenceEquals(SelectedModule, tab))
            SelectedModule = Modules.Count == 0 ? null : Modules[Math.Min(idx, Modules.Count - 1)];
    }

    // -- ${sprig.*} reference surface (workspace + provides/needs) --------------

    /// <summary>Rebuild the reference surfaces in place (so bound editors update) from the current provides
    /// and needs — the exact <see cref="SprigVariableNames"/> and the open
    /// <see cref="SprigNeededCapabilities"/> heads.</summary>
    void RefreshSprigVariableNames()
    {
        var names = new List<string> { "workspace" };
        // Map model: self-provided outputs are referenceable as ${sprig.<capability>.<output>} — greened
        // exactly. A need's outputs live in another repo, so they can't be enumerated: its capability/alias
        // head goes into the open set instead, and the token box accepts any output under it.
        var open = new List<string>();
        foreach (var tab in Modules)
        {
            foreach (var p in tab.Provides)
            {
                var cap = p.Capability.Trim();
                if (cap.Length == 0) continue;
                var port = $"{cap}.port";                    // the always-present anchor
                if (!names.Contains(port)) names.Add(port);
                foreach (var s in p.Shapes)
                {
                    var nm = s.Name.Trim();
                    if (nm.Length == 0) continue;
                    var full = $"{cap}.{nm}";
                    if (!names.Contains(full)) names.Add(full);
                }
            }
            foreach (var n in tab.Needs)
            {
                var cap = n.Capability.Trim();
                if (cap.Length > 0 && !open.Contains(cap)) open.Add(cap);
                var alias = n.As.Trim();
                if (alias.Length > 0 && !open.Contains(alias)) open.Add(alias);
            }
        }
        // Only churn a collection when it actually changed — a Clear+Add of identical content still raises a
        // reset that overlays react to, which (via OverridesChanged) would re-enter this method and recurse
        // forever. The equality guard makes the notification cycle converge.
        if (!SprigVariableNames.SequenceEqual(names, StringComparer.Ordinal))
        {
            SprigVariableNames.Clear();
            foreach (var n in names) SprigVariableNames.Add(n);
        }
        if (!SprigNeededCapabilities.SequenceEqual(open, StringComparer.Ordinal))
        {
            SprigNeededCapabilities.Clear();
            foreach (var c in open) SprigNeededCapabilities.Add(c);
        }
    }

    // -- file/git helpers (shared by every module's rows) ----------------------

    /// <summary>True if a repo-relative path names a file that exists in the repo (cheap, best-effort).</summary>
    public bool RepoFileExists(string file)
    {
        var rel = (file ?? "").Trim();
        if (rel.Length == 0) return false;
        try { return System.IO.File.Exists(Path.Combine(RepoPath, rel)); }
        catch { return false; }
    }

    /// <summary>True if a repo-relative path names a directory that exists in the repo — drives a module's
    /// path ✓/⚠ hint (informational only; a missing dir doesn't block save).</summary>
    public bool RepoDirExists(string dir)
    {
        var rel = (dir ?? "").Trim();
        if (rel.Length == 0) return false;
        try { return Directory.Exists(Path.Combine(RepoPath, rel)); }
        catch { return false; }
    }

    /// <summary>
    /// Seed a hand-added compose file's overrides. The stack-era auto-detection proposed
    /// <c>${sprig.&lt;input&gt;}</c> port rewrites plus matching input declarations — a shape the map model no
    /// longer has (a provided port capability is what a compose port should become). Until a map-model
    /// in-editor compose auto-detection exists, a hand-added compose file starts as a blank slate; author its
    /// provides/needs (and reference them in the overlay) by hand. Signature kept for the row that calls it.
    /// </summary>
    public IReadOnlyList<ComposeOverride> DetectComposeOverrides(string composeText, string fileLabel)
    {
        _ = (composeText, fileLabel);
        return [];   // map-model compose auto-detection is a follow-up (see docs/graph-turn-followup-plan.md)
    }

    /// <summary>Read a repo-relative file's text, or <c>""</c> if it's missing/unreadable (best-effort).</summary>
    public string ReadRepoFile(string file)
    {
        var rel = (file ?? "").Trim();
        if (rel.Length == 0) return "";
        var abs = Path.Combine(RepoPath, rel);
        try { return System.IO.File.Exists(abs) ? System.IO.File.ReadAllText(abs) : ""; }
        catch { return ""; }
    }

    /// <summary>Variable names available for an env-file field — the file's own keys plus any from a
    /// companion template (<c>.env.template</c> etc.). Best-effort; drives the KEY autosuggest.</summary>
    public IReadOnlyList<string> EnvKeysFor(string file) => EnvKeyReader.KeysForFile(RepoPath, file);

    /// <summary>Example values per key for an env-file field — the values that key already has in the
    /// target file and its companion templates. Best-effort; feeds each key row's info flyout.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<EnvExample>> EnvExamplesFor(string file)
        => EnvKeyReader.ExamplesForFile(RepoPath, file);

    /// <summary>True if the given repo-relative path is tracked in git (and so off-limits to override).</summary>
    public bool IsTracked(string file)
    {
        var norm = Normalize(file);
        return norm.Length > 0 && _tracked.Contains(norm);
    }

    /// <summary>
    /// Classify an env-file path against git for the safety subtext. The tracked check is a cheap
    /// set lookup done inline; the gitignore probe shells out to <c>git check-ignore</c>, so it runs
    /// on a background thread. Answered from rules alone, so it's meaningful for a path that has no
    /// file on disk yet — the case that naive "does it exist" detection got wrong.
    /// </summary>
    public Task<EnvFileStatus> ClassifyEnvFileAsync(string file, CancellationToken ct)
    {
        var norm = Normalize(file);
        if (norm.Length == 0) return Task.FromResult(EnvFileStatus.Empty);
        if (_tracked.Contains(norm)) return Task.FromResult(EnvFileStatus.Tracked);

        return Task.Run(() =>
        {
            if (_git?.IsIgnored(RepoPath, norm) ?? false) return EnvFileStatus.Ignored;
            var exists = System.IO.File.Exists(Path.Combine(RepoPath, norm));
            return exists ? EnvFileStatus.NotIgnored : EnvFileStatus.NotIgnoredNew;
        }, ct);
    }

    /// <summary>
    /// Path suggestions for a file field: file-system entries whose trailing segment matches what's been
    /// typed, returned as forward-slash paths (directories keep a trailing slash so you can drill in).
    /// <paramref name="basePath"/> is the directory the field's values are relative to — the module's
    /// path for its env/compose files, or empty for the module-path picker itself. Enumeration starts
    /// there, but the returned suggestions stay relative to it (matching the stored value). Pure +
    /// best-effort (never throws).
    /// </summary>
    public IReadOnlyList<string> SuggestRepoPaths(string input, string basePath = "")
    {
        input = (input ?? "").Replace('\\', '/').TrimStart('/');
        var baseDir = (basePath ?? "").Replace('\\', '/').Trim().Trim('/');
        try
        {
            var slash = input.LastIndexOf('/');
            var relDir = slash < 0 ? "" : input[..slash];
            var prefix = slash < 0 ? input : input[(slash + 1)..];

            // Enumerate under <repo>/<basePath>/<relDir>; the returned rel is relative to basePath.
            var root = baseDir.Length == 0 ? RepoPath : Path.Combine(RepoPath, baseDir.Replace('/', Path.DirectorySeparatorChar));
            var absDir = relDir.Length == 0 ? root : Path.Combine(root, relDir);
            if (!Directory.Exists(absDir)) return [];

            return Directory.EnumerateFileSystemEntries(absDir)
                .Select(p => (name: Path.GetFileName(p), isDir: Directory.Exists(p)))
                .Where(e => e.name.Length > 0 && !(e.isDir && e.name == ".git"))
                .Where(e => e.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.isDir).ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .Select(e =>
                {
                    var rel = relDir.Length == 0 ? e.name : $"{relDir}/{e.name}";
                    return e.isDir ? rel + "/" : rel;
                })
                .ToList();
        }
        catch { return []; }
    }

    static string Normalize(string file) => (file ?? "").Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>A repo-relative path for a module file: the module's base path joined to the file
    /// (forward-slash), or just the file when the module is at the repo root.</summary>
    internal static string JoinPath(string basePath, string file) =>
        string.IsNullOrEmpty(basePath) ? file : $"{basePath.Replace('\\', '/').TrimEnd('/')}/{file}";

    /// <summary>Reconstruct a full config from the edited fields — one module per tab, each with its
    /// provides / needs / env / compose / setup. Zero tabs → a config that declares no modules.</summary>
    public SprigRepoConfig Build() => new()
    {
        Schema = _schema,
        Name = Name,
        Modules = Modules.Select(t => new ModuleDeclaration
        {
            Name = t.Name.Trim(),
            Path = t.Path.Trim(),
            Provides = t.Provides.Select(p => new ProvidedCapability
            {
                Capability = p.Capability.Trim(),
                // Exactly one port, always named "port" — the permanent anchor.
                Ports = new Dictionary<string, PortSpec>(StringComparer.Ordinal)
                    { ["port"] = PortSpec.Constrained(Blank(p.Port.Allowed)) },
                Shapes = p.Shapes
                    .Where(s => s.Name.Trim().Length > 0)
                    .ToDictionary(s => s.Name.Trim(), s => s.Template.Trim(), StringComparer.Ordinal),
            }).ToList(),
            Needs = t.Needs
                .Where(n => n.Capability.Trim().Length > 0)
                .Select(n => new Need { Capability = n.Capability.Trim(), As = Blank(n.As) }).ToList(),
            Env = t.Env.Select(e =>
            {
                var templates = e.Templates.Select(x => x.Path.Trim()).Where(p => p.Length > 0).ToList();
                return new EnvOverride
                {
                    File = e.File.Trim(),
                    Templates = templates.Count > 0 ? templates : null,
                    Set = new Dictionary<string, string>(e.CurrentSet, StringComparer.Ordinal),
                };
            }).ToList(),
            Compose = t.Compose.Select(c => new ComposeConfig { File = c.File.Trim(), Overrides = c.CurrentOverrides }).ToList(),
            Setup = t.Setup.Select(s => s.Command.Trim()).Where(c => c.Length > 0).ToList(),
        }).ToList(),
    };

    /// <summary>Validate the edited config and, if valid, write it back to <c>.sprig.json</c>.</summary>
    /// <returns>True on success; otherwise <see cref="Error"/> holds the reason.</returns>
    public bool Save()
    {
        var config = Build();

        // Overriding a git-tracked file would leave the worktree permanently dirty, so refuse it —
        // sprig only clobbers untracked (typically gitignored) env files. Files resolve under their module.
        var tracked = config.EffectiveModules
            .SelectMany(m => m.Env.Select(e => JoinPath(m.Path, e.File)))
            .Where(IsTracked)
            .ToList();
        if (tracked.Count > 0)
        {
            Error = $"these env files are tracked by git and can't be overridden: {string.Join(", ", tracked)}";
            return false;
        }

        // Every compose override target must exist in the repo — otherwise generation fails at
        // workspace-creation time with a much less obvious error. (Blank is caught by the validator.)
        var missingCompose = config.EffectiveModules
            .SelectMany(m => m.Compose.Select(cc => JoinPath(m.Path, cc.File)))
            .Where(rel => rel.Length > 0 && !System.IO.File.Exists(Path.Combine(RepoPath, rel)))
            .ToList();
        if (missingCompose.Count > 0)
        {
            Error = $"compose file(s) not found in the repo: {string.Join(", ", missingCompose)}";
            return false;
        }

        var result = SprigConfigValidator.Validate(config);
        if (!result.IsValid)
        {
            Error = string.Join("\n", result.Issues.Select(i => i.ToString()));
            return false;
        }

        try { ConfigJson.Write(config, Path.Combine(RepoPath, ".sprig.json")); }
        catch (Exception ex) { Error = ex.Message; return false; }

        Error = null;
        return true;
    }

    static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
