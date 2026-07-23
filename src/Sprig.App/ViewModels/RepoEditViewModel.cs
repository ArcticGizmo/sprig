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

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>One editable <c>KEY = value</c> pair inside an env file.</summary>
public partial class KvEditRow : ObservableObject
{
    readonly Action<KvEditRow> _remove;
    public KvEditRow(Action<KvEditRow> remove) => _remove = remove;

    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";

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

/// <summary>One editable <c>.env.*</c> file plus the keys it clobbers.</summary>
public partial class EnvFileEditRow : ObservableObject
{
    readonly Action<EnvFileEditRow> _remove;
    readonly Func<string, CancellationToken, Task<EnvFileStatus>> _classify;
    readonly Func<string, IReadOnlyList<string>> _keysFor;
    readonly Func<string, bool> _exists;
    CancellationTokenSource? _cts;

    public EnvFileEditRow(
        Action<EnvFileEditRow> remove,
        Func<string, CancellationToken, Task<EnvFileStatus>> classify,
        Func<string, IReadOnlyList<string>> keysFor,
        Func<string, bool> exists)
    {
        _remove = remove;
        _classify = classify;
        _keysFor = keysFor;
        _exists = exists;
        // Seed templates contribute their own keys to the autosuggest, so re-gather when they change.
        Templates.CollectionChanged += OnTemplatesChanged;
    }

    [ObservableProperty] private string _file = "";
    public ObservableCollection<KvEditRow> Set { get; } = [];

    /// <summary>Template files this override seeds the worktree's copy from (optional, ordered).</summary>
    public ObservableCollection<TemplateFileRow> Templates { get; } = [];

    /// <summary>Variable names found in the target file (and its template companions) — feeds the
    /// KEY field's autosuggest. Refreshed off the UI thread whenever <see cref="File"/> changes.</summary>
    public ObservableCollection<string> AvailableKeys { get; } = [];

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

    /// <summary>Recompute the git status and the key suggestions off the UI thread. Runs on a
    /// <see cref="File"/> edit and whenever the seed templates change (they add their own keys).</summary>
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
        try
        {
            result = await _classify(file, ct);
            var templatePaths = Templates.Select(t => t.Path).ToList();  // snapshot on the UI thread
            keys = await Task.Run(() => GatherKeys(file, templatePaths), ct);
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        Status = result;
        AvailableKeys.Clear();
        foreach (var k in keys) AvailableKeys.Add(k);
    }

    /// <summary>Variable names to suggest for this override's KEY field: the union of those declared
    /// in the target file and in each configured seed template, in first-seen order.</summary>
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

    partial void OnStatusChanged(EnvFileStatus value)
    {
        OnPropertyChanged(nameof(ShowTrackedWarning));
        OnPropertyChanged(nameof(ShowIgnoredOk));
        OnPropertyChanged(nameof(ShowNotIgnoredWarning));
        OnPropertyChanged(nameof(NotIgnoredMessage));
    }

    [RelayCommand] private void Remove() => _remove(this);
    [RelayCommand] private void AddKey() => Set.Add(new KvEditRow(r => Set.Remove(r)));
    [RelayCommand] private void AddTemplate() => Templates.Add(new TemplateFileRow(r => Templates.Remove(r), _exists));
}

/// <summary>One editable docker-compose override: the target file plus its own interactive overlay.</summary>
public partial class ComposeFileEditRow : ObservableObject
{
    readonly Action<ComposeFileEditRow> _remove;
    readonly Func<string, bool> _exists;
    readonly Func<string, string> _readText;
    readonly IEnumerable<string> _variables;

    /// <summary>Overrides loaded from disk — seeds the overlay's first build so a missing/blank path
    /// doesn't drop them before the file is (re)supplied.</summary>
    IReadOnlyList<ComposeOverride>? _seed;

    public ComposeFileEditRow(
        Action<ComposeFileEditRow> remove,
        Func<string, bool> exists,
        Func<string, string> readText,
        IEnumerable<string> variables)
    {
        _remove = remove;
        _exists = exists;
        _readText = readText;
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

    /// <summary>The overrides to persist: whatever the live overlay holds, else the disk seed.</summary>
    public IReadOnlyList<ComposeOverride> CurrentOverrides => Overlay?.ToOverrides() ?? _seed ?? [];

    partial void OnFileChanged(string value) => Rebuild();

    partial void OnFoundChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFound));
        OnPropertyChanged(nameof(ShowMissing));
    }

    partial void OnOverlayChanged(ComposeOverlayViewModel? value) => OnPropertyChanged(nameof(HasOverlay));

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
        Overlay = new ComposeOverlayViewModel(text, seed, _variables);
    }

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>
/// Editable view over a repo's <c>.sprig.json</c>. The repo <b>name</b> is intentionally not
/// editable here — it is the registry/stack key, so renaming is a separate operation — but every
/// value it declares (inputs, env overrides, compose overrides) can be changed and saved back.
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
    public ObservableCollection<EnvFileEditRow> Env { get; } = [];

    /// <summary>The compose files this repo overrides — one editable card each (add/remove).</summary>
    public ObservableCollection<ComposeFileEditRow> Compose { get; } = [];

    /// <summary>True when at least one input is declared — gates the inputs column header.</summary>
    public bool HasInputs => Inputs.Count > 0;

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

        foreach (var e in c.Env)
        {
            var file = new EnvFileEditRow(vm.RemoveEnvRow, vm.ClassifyEnvFileAsync, vm.EnvKeysFor, vm.RepoFileExists)
                { File = e.File };
            foreach (var t in e.Templates ?? [])
                file.Templates.Add(new TemplateFileRow(r => file.Templates.Remove(r), vm.RepoFileExists) { Path = t });
            foreach (var kv in e.Set)
                file.Set.Add(new KvEditRow(r => file.Set.Remove(r)) { Key = kv.Key, Value = kv.Value });
            vm.Env.Add(file);
        }

        foreach (var comp in c.Compose)
        {
            var row = new ComposeFileEditRow(vm.RemoveComposeRow, vm.RepoFileExists, vm.ReadRepoFile, vm.SprigVariableNames);
            row.Seed(comp.File, comp.Overrides);
            vm.Compose.Add(row);
        }

        return vm;
    }

    void RemoveInputRow(InputEditRow r) => Inputs.Remove(r);
    void RemoveEnvRow(EnvFileEditRow r) => Env.Remove(r);
    void RemoveComposeRow(ComposeFileEditRow r) => Compose.Remove(r);

    // -- ${sprig.*} variables (workspace + declared inputs) --------------------

    void OnInputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (InputEditRow r in e.OldItems) r.PropertyChanged -= OnInputRowChanged;
        if (e.NewItems is not null)
            foreach (InputEditRow r in e.NewItems) r.PropertyChanged += OnInputRowChanged;
        RefreshSprigVariableNames();
        OnPropertyChanged(nameof(HasInputs));
    }

    void OnInputRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InputEditRow.Name)) RefreshSprigVariableNames();
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
        SprigVariableNames.Clear();
        foreach (var n in names) SprigVariableNames.Add(n);
    }

    // -- compose ---------------------------------------------------------------

    [RelayCommand] private void AddInput() => Inputs.Add(new InputEditRow(RemoveInputRow));

    [RelayCommand]
    private void AddEnvFile()
    {
        var file = new EnvFileEditRow(RemoveEnvRow, ClassifyEnvFileAsync, EnvKeysFor, RepoFileExists);
        file.Set.Add(new KvEditRow(r => file.Set.Remove(r)));
        Env.Add(file);
    }

    [RelayCommand]
    private void AddComposeFile()
        => Compose.Add(new ComposeFileEditRow(RemoveComposeRow, RepoFileExists, ReadRepoFile, SprigVariableNames));

    /// <summary>True if a repo-relative path names a file that exists in the repo (cheap, best-effort).</summary>
    public bool RepoFileExists(string file)
    {
        var rel = (file ?? "").Trim();
        if (rel.Length == 0) return false;
        try { return System.IO.File.Exists(Path.Combine(RepoPath, rel)); }
        catch { return false; }
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
    /// Path suggestions for a repo-relative file field (env files, the compose file): file-system
    /// entries under the repo root whose trailing segment matches what's been typed, returned as
    /// repo-relative forward-slash paths (directories keep a trailing slash so you can drill in).
    /// Pure + best-effort (never throws).
    /// </summary>
    public IReadOnlyList<string> SuggestRepoPaths(string input)
    {
        input = (input ?? "").Replace('\\', '/').TrimStart('/');
        try
        {
            var slash = input.LastIndexOf('/');
            var relDir = slash < 0 ? "" : input[..slash];
            var prefix = slash < 0 ? input : input[(slash + 1)..];

            var absDir = relDir.Length == 0 ? RepoPath : Path.Combine(RepoPath, relDir);
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

    /// <summary>Reconstruct a full config from the edited fields (round-trips every declared value).</summary>
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
        Env = Env.Select(e =>
        {
            var templates = e.Templates
                .Select(t => t.Path.Trim())
                .Where(p => p.Length > 0)
                .ToList();
            return new EnvOverride
            {
                File = e.File.Trim(),
                Templates = templates.Count > 0 ? templates : null,
                Set = ToDict(e.Set),
            };
        }).ToList(),
        Compose = Compose.Select(c => new ComposeConfig
        {
            File = c.File.Trim(),
            Overrides = c.CurrentOverrides,
        }).ToList(),
    };

    /// <summary>Validate the edited config and, if valid, write it back to <c>.sprig.json</c>.</summary>
    /// <returns>True on success; otherwise <see cref="Error"/> holds the reason.</returns>
    public bool Save()
    {
        var config = Build();

        // Overriding a git-tracked file would leave the worktree permanently dirty, so refuse it —
        // sprig only clobbers untracked (typically gitignored) env files.
        var tracked = config.Env.Where(e => IsTracked(e.File)).Select(e => e.File).ToList();
        if (tracked.Count > 0)
        {
            Error = $"these env files are tracked by git and can't be overridden: {string.Join(", ", tracked)}";
            return false;
        }

        // Every compose override target must exist in the repo — otherwise generation fails at
        // workspace-creation time with a much less obvious error. (Blank is caught by the validator.)
        var missingCompose = config.Compose
            .Where(cc => cc.File.Length > 0 && !System.IO.File.Exists(Path.Combine(RepoPath, cc.File)))
            .Select(cc => cc.File)
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

    // Last value wins on a duplicate key — the validator has no cross-key check, and a dict can't
    // hold duplicates anyway; this just avoids throwing while the user is mid-edit.
    static Dictionary<string, string> ToDict(IEnumerable<KvEditRow> rows)
    {
        var dict = new Dictionary<string, string>();
        foreach (var r in rows)
        {
            var key = r.Key.Trim();
            if (key.Length > 0) dict[key] = r.Value;
        }
        return dict;
    }
}
