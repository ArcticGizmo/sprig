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
using Sprig.Core.Init;

namespace Sprig.App.ViewModels;

/// <summary>One editable <c>${sprig.&lt;name&gt;}</c> input declaration.</summary>
public partial class InputEditRow : ObservableObject
{
    readonly Action<InputEditRow> _remove;
    public InputEditRow(Action<InputEditRow> remove) => _remove = remove;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _example = "";
    [ObservableProperty] private string _description = "";

    /// <summary>Optional port restriction spec (e.g. <c>8100-8103</c>) — see <c>PortSetSpec</c>.</summary>
    [ObservableProperty] private string _allowedPorts = "";

    /// <summary>
    /// Whether this row's port-restriction editor is showing.
    ///
    /// Restricting ports is an advanced case (the classic one is Auth0 callback URLs that must be
    /// pre-registered per port); nearly every input leaves it blank. So the field stays behind a link until
    /// it's wanted, rather than sitting at the same visual weight as the input's name.
    /// </summary>
    [ObservableProperty] private bool _restricting;

    /// <summary>Offer the restriction editor — nothing set, and not asked for yet.</summary>
    public bool ShowRestrictLink => !Restricting && string.IsNullOrWhiteSpace(AllowedPorts);

    /// <summary>Show the editor once asked for, or whenever a restriction already exists.</summary>
    public bool ShowRestrictBox => !ShowRestrictLink;

    partial void OnRestrictingChanged(bool value) => RaiseRestrictVisibility();
    partial void OnAllowedPortsChanged(string value) => RaiseRestrictVisibility();

    void RaiseRestrictVisibility()
    {
        OnPropertyChanged(nameof(ShowRestrictLink));
        OnPropertyChanged(nameof(ShowRestrictBox));
    }

    [RelayCommand] private void Restrict() => Restricting = true;

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>One editable <c>setup</c> command — a free-form line run at the worktree root.</summary>
public partial class SetupCommandRow : ObservableObject
{
    readonly Action<SetupCommandRow> _remove;
    public SetupCommandRow(Action<SetupCommandRow> remove) => _remove = remove;

    [ObservableProperty] private string _command = "";

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>One editable output of a provided capability (map model): either an allocated <b>port</b>
/// (optionally pinned to an <see cref="Allowed"/> set) or a <b>derived</b> string <see cref="Template"/>.</summary>
public partial class OutputEditRow : ObservableObject
{
    readonly Action<OutputEditRow> _remove;
    public OutputEditRow(Action<OutputEditRow> remove) => _remove = remove;

    [ObservableProperty] private string _name = "";

    /// <summary>True = an auto-allocated port; false = a derived string template.</summary>
    [ObservableProperty] private bool _isPort = true;

    /// <summary>For a port output: an optional restriction spec (e.g. <c>8100-8103</c>).</summary>
    [ObservableProperty] private string _allowed = "";

    /// <summary>For a derived output: the template (e.g. <c>http://localhost:${sprig.api.port}</c>).</summary>
    [ObservableProperty] private string _template = "";

    public bool IsDerived => !IsPort;
    partial void OnIsPortChanged(bool value) => OnPropertyChanged(nameof(IsDerived));

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>One editable provided capability (map model): a contract name, an optional type hint, and its
/// named outputs.</summary>
public partial class ProvideEditRow : ObservableObject
{
    readonly Action<ProvideEditRow> _remove;
    public ProvideEditRow(Action<ProvideEditRow> remove) => _remove = remove;

    [ObservableProperty] private string _capability = "";
    [ObservableProperty] private string _type = "";

    public ObservableCollection<OutputEditRow> Outputs { get; } = [];

    [RelayCommand] private void AddOutput() =>
        Outputs.Add(new OutputEditRow(r => Outputs.Remove(r)) { Name = "port", IsPort = true });

    [RelayCommand] private void Remove() => _remove(this);
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
        IEnumerable<string> variables)
    {
        _remove = remove;
        _classify = classify;
        _keysFor = keysFor;
        _examplesFor = examplesFor;
        _exists = exists;
        _variables = variables;
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
        Overlay = new EnvOverlayViewModel(keys, examples, seed, _variables);
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
        IEnumerable<string> variables)
    {
        _remove = remove;
        _exists = exists;
        _readText = readText;
        _detect = detect;
        _variables = variables;
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

        Overlay = new ComposeOverlayViewModel(text, seed, _variables);
    }

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>
/// One editable module tab: its name, its path (subdirectory), and its own env / compose / setup
/// rows. Inputs are not here — they are shared and edited once above the tabs. The env/compose rows
/// reuse the owner's git-safety + overlay machinery, prepending this module's path (read live) so the
/// checks resolve under it; changing the path re-evaluates every row.
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
        _owner.SprigVariableNames);

    /// <summary>A fresh compose row wired to this module (removal, path-aware exists/read callbacks, and
    /// the add-time auto-detection that pre-fills a hand-added file's overrides).</summary>
    public ComposeFileEditRow NewComposeRow() => new(
        r => Compose.Remove(r),
        f => _owner.RepoFileExists(RepoEditViewModel.JoinPath(Path, f)),
        f => _owner.ReadRepoFile(RepoEditViewModel.JoinPath(Path, f)),
        _owner.DetectComposeOverrides,
        _owner.SprigVariableNames);

    [RelayCommand] private void AddProvide()
    {
        var p = new ProvideEditRow(r => Provides.Remove(r));
        p.Outputs.Add(new OutputEditRow(r => p.Outputs.Remove(r)) { Name = "port", IsPort = true });
        Provides.Add(p);
    }

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
}

/// <summary>
/// Editable view over a repo's <c>.sprig.json</c>. The repo <b>name</b> is intentionally not
/// editable here — it is the registry/stack key, so renaming is a separate operation. Inputs are
/// declared once (shared), then each <b>module</b> is a tab with its own env / compose / setup.
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
        // Keep the ${sprig.*} variable list live: it feeds token autocomplete + validity, and inputs
        // can be added/renamed in this same form.
        Inputs.CollectionChanged += OnInputsChanged;
        // A module's overrides are where ${sprig.*} inputs get referenced — track each module so the
        // shared "referenced but not declared" quick-add list stays current across every module.
        Modules.CollectionChanged += OnModulesChanged;
        RefreshSprigVariableNames();
    }

    public string RepoPath { get; }

    /// <summary>The variables a repo template may reference — <c>workspace</c> plus each declared
    /// input name. Drives <c>${sprig.*}</c> autocomplete and the invalid-reference highlight; kept in
    /// sync as inputs are edited above.</summary>
    public ObservableCollection<string> SprigVariableNames { get; } = [];

    /// <summary>The logical repo name — shown for context, not editable (it keys the registry/stacks).</summary>
    public string Name { get; private set; } = "";

    public ObservableCollection<InputEditRow> Inputs { get; } = [];

    /// <summary>The repo's module tabs — each with its own env / compose / setup. Add/remove down to zero.</summary>
    public ObservableCollection<ModuleEditTab> Modules { get; } = [];

    /// <summary>The module tab currently shown below the strip (null when there are none).</summary>
    [ObservableProperty] private ModuleEditTab? _selectedModule;

    /// <summary>True when at least one module exists — gates the module detail vs. the empty state.</summary>
    public bool HasModules => Modules.Count > 0;

    /// <summary>True when at least one input is declared — gates the inputs column header.</summary>
    public bool HasInputs => Inputs.Count > 0;

    /// <summary><c>${sprig.*}</c> names referenced by an env/compose override that aren't declared as
    /// inputs yet — offered as one-click "quick add" chips so you can reference inputs as you go and
    /// declare them after. These are exactly what blocks a save (see <see cref="Save"/>), so the list
    /// empties to nothing before the config is valid.</summary>
    public ObservableCollection<string> MissingInputRefs { get; } = [];

    /// <summary>True when at least one referenced input is undeclared — gates the quick-add strip.</summary>
    public bool HasMissingInputRefs => MissingInputRefs.Count > 0;

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

        foreach (var i in c.Inputs)
            vm.Inputs.Add(new InputEditRow(vm.RemoveInputRow)
            {
                Name = i.Name,
                Example = i.Example ?? "",
                Description = i.Description ?? "",
                AllowedPorts = i.AllowedPorts ?? "",
            });

        // One tab per module; each hydrates its own env/compose/setup rows (path-aware via the tab).
        foreach (var m in c.EffectiveModules)
        {
            var tab = new ModuleEditTab(vm, m.Name, m.Path);
            foreach (var p in m.Provides)
            {
                var pr = new ProvideEditRow(r => tab.Provides.Remove(r)) { Capability = p.Capability, Type = p.Type ?? "" };
                foreach (var (outName, spec) in p.Outputs)
                    pr.Outputs.Add(new OutputEditRow(r => pr.Outputs.Remove(r))
                    {
                        Name = outName,
                        IsPort = spec.IsPort,
                        Allowed = spec.Allowed ?? "",
                        Template = spec.Template ?? "",
                    });
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
        vm.RefreshMissingInputRefs();
        return vm;
    }

    // -- modules ---------------------------------------------------------------

    void OnModulesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ModuleEditTab t in e.OldItems) t.OverridesChanged -= OnModuleOverridesChanged;
        if (e.NewItems is not null)
            foreach (ModuleEditTab t in e.NewItems) t.OverridesChanged += OnModuleOverridesChanged;
        OnPropertyChanged(nameof(HasModules));
        RefreshMissingInputRefs();
    }

    // NOTE: don't refresh SprigVariableNames here. This fires on every override change, and rebuilding the
    // shared variable collection makes each env/compose overlay react and re-raise OverridesChanged, which
    // re-enters this handler — an infinite cycle. The self-provided ${sprig.<cap>.<out>} names are refreshed
    // on load and on input edits; live-updating them as provides are typed is a deferred polish.
    void OnModuleOverridesChanged(object? sender, EventArgs e) => RefreshMissingInputRefs();

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

    void RemoveInputRow(InputEditRow r) => Inputs.Remove(r);

    // -- ${sprig.*} variables (workspace + declared inputs) --------------------

    void OnInputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (InputEditRow r in e.OldItems) r.PropertyChanged -= OnInputRowChanged;
        if (e.NewItems is not null)
            foreach (InputEditRow r in e.NewItems) r.PropertyChanged += OnInputRowChanged;
        RefreshSprigVariableNames();
        RefreshMissingInputRefs();   // declaring/removing an input changes what's still "missing"
        OnPropertyChanged(nameof(HasInputs));
    }

    void OnInputRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InputEditRow.Name)) return;
        RefreshSprigVariableNames();
        RefreshMissingInputRefs();
    }

    // -- referenced-but-undeclared inputs (quick add) --------------------------

    /// <summary>Recompute which referenced <c>${sprig.*}</c> inputs aren't declared yet, off the live
    /// edit state. Cheap and best-effort — a refresh must never interrupt editing.</summary>
    void RefreshMissingInputRefs()
    {
        List<string> missing;
        try { missing = ConfigReferences.UndeclaredReferences(Build()).ToList(); }
        catch { return; }
        if (MissingInputRefs.SequenceEqual(missing, StringComparer.Ordinal)) return;
        MissingInputRefs.Clear();
        foreach (var m in missing) MissingInputRefs.Add(m);
        OnPropertyChanged(nameof(HasMissingInputRefs));
    }

    /// <summary>Declare a referenced-but-missing input from its quick-add chip (name pre-filled; fill
    /// example/description later). No-op if it's already declared.</summary>
    [RelayCommand]
    private void QuickAddInput(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0 || Inputs.Any(i => string.Equals(i.Name.Trim(), n, StringComparison.Ordinal)))
            return;
        Inputs.Add(new InputEditRow(RemoveInputRow) { Name = n });   // OnInputsChanged refreshes the rest
    }

    /// <summary>Rebuild the variable list in place (so bound editors update) from the current inputs.</summary>
    void RefreshSprigVariableNames()
    {
        var names = new List<string> { "workspace" };
        foreach (var i in Inputs)
        {
            var n = i.Name.Trim();
            if (n.Length > 0 && !names.Contains(n)) names.Add(n);
        }
        // Map model: self-provided outputs are referenceable as ${sprig.<capability>.<output>}. (A need's
        // outputs live in another repo, so they can't be enumerated here — the validator accepts them by
        // the capability head; the token box just can't green-light them yet.)
        foreach (var tab in Modules)
            foreach (var p in tab.Provides)
            {
                var cap = p.Capability.Trim();
                if (cap.Length == 0) continue;
                foreach (var o in p.Outputs)
                {
                    var full = $"{cap}.{o.Name.Trim()}";
                    if (o.Name.Trim().Length > 0 && !names.Contains(full)) names.Add(full);
                }
            }
        // Only churn the collection when it actually changed — a Clear+Add of identical content still
        // raises a reset that overlays react to, which (via OverridesChanged) would re-enter this method
        // and recurse forever. The equality guard makes the notification cycle converge.
        if (SprigVariableNames.SequenceEqual(names, StringComparer.Ordinal)) return;
        SprigVariableNames.Clear();
        foreach (var n in names) SprigVariableNames.Add(n);
    }

    // -- file/git helpers (shared by every module's rows) ----------------------

    [RelayCommand] private void AddInput() => Inputs.Add(new InputEditRow(RemoveInputRow));

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
    /// Run add-time compose detection over a hand-added compose file (the same detection the initial add
    /// runs), so manually adding another compose file isn't a blank slate. Declares any port inputs it
    /// proposes — skipping names already declared — and returns the value overrides for the row to seed
    /// its overlay with. New inputs land in the shared <see cref="Inputs"/> list, immediately becoming
    /// known <c>${sprig.*}</c> variables so the seeded overrides don't render as undeclared.
    /// </summary>
    public IReadOnlyList<ComposeOverride> DetectComposeOverrides(string composeText, string fileLabel)
    {
        var detection = InitInspector.DetectComposeInText(composeText, fileLabel, SprigVariableNames);
        foreach (var input in detection.Inputs)
        {
            if (Inputs.Any(i => string.Equals(i.Name.Trim(), input.Name, StringComparison.Ordinal)))
                continue;
            Inputs.Add(new InputEditRow(RemoveInputRow)
            {
                Name = input.Name,
                Example = input.Example ?? "",
                Description = input.Description ?? "",
                AllowedPorts = input.AllowedPorts ?? "",
            });
        }
        return detection.Overrides;
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

    /// <summary>Reconstruct a full config from the edited fields — inputs (shared) plus one module per
    /// tab, each with its env / compose / setup. Zero tabs → a config that declares no modules.</summary>
    public SprigRepoConfig Build() => new()
    {
        Schema = _schema,
        Name = Name,
        Inputs = Inputs.Select(i => new InputDeclaration
        {
            Name = i.Name.Trim(),
            Example = Blank(i.Example),
            Description = Blank(i.Description),
            AllowedPorts = Blank(i.AllowedPorts),
        }).ToList(),
        Modules = Modules.Select(t => new ModuleDeclaration
        {
            Name = t.Name.Trim(),
            Path = t.Path.Trim(),
            Provides = t.Provides.Select(p => new ProvidedCapability
            {
                Capability = p.Capability.Trim(),
                Type = Blank(p.Type),
                Outputs = p.Outputs
                    .Where(o => o.Name.Trim().Length > 0)
                    .ToDictionary(
                        o => o.Name.Trim(),
                        o => o.IsPort ? OutputSpec.Port(Blank(o.Allowed)) : OutputSpec.Derived(o.Template.Trim()),
                        StringComparer.Ordinal),
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
