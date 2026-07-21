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
    CancellationTokenSource? _cts;

    public EnvFileEditRow(
        Action<EnvFileEditRow> remove,
        Func<string, CancellationToken, Task<EnvFileStatus>> classify,
        Func<string, IReadOnlyList<string>> keysFor)
    {
        _remove = remove;
        _classify = classify;
        _keysFor = keysFor;
    }

    [ObservableProperty] private string _file = "";
    public ObservableCollection<KvEditRow> Set { get; } = [];

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

    partial void OnFileChanged(string value)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        StatusReady = ClassifyAsync(value, cts.Token);
    }

    async Task ClassifyAsync(string file, CancellationToken ct)
    {
        EnvFileStatus result;
        IReadOnlyList<string> keys;
        try
        {
            result = await _classify(file, ct);
            keys = await Task.Run(() => _keysFor(file), ct);
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        Status = result;
        AvailableKeys.Clear();
        foreach (var k in keys) AvailableKeys.Add(k);
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

    /// <summary>The compose overrides as loaded from disk — seeds the overlay's first build so a
    /// missing/blank compose path doesn't drop them before the file is (re)supplied.</summary>
    IReadOnlyList<ComposeOverride>? _composeSeed;

    /// <summary>True only while <see cref="Load"/> is populating fields, so the property-change hooks
    /// don't rebuild the overlay repeatedly mid-load (we do it once at the end).</summary>
    bool _loading;

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

    /// <summary>The interactive compose editor — renders the source file with clickable value tokens.
    /// Rebuilt whenever the compose file path changes; null when compose isn't enabled.</summary>
    [ObservableProperty] private ComposeOverlayViewModel? _composeOverlay;

    /// <summary>Whether this repo declares a compose override block.</summary>
    [ObservableProperty] private bool _hasCompose;
    [ObservableProperty] private string _composeFile = "";

    /// <summary>True when the named compose file exists in the repo — the override target must exist.</summary>
    [ObservableProperty] private bool _composeFileFound;

    [ObservableProperty] private string? _error;

    /// <summary>A compose file path has been entered (and compose is enabled) — drives the ✓/⚠ subtext.</summary>
    public bool ComposeFileEntered => HasCompose && !string.IsNullOrWhiteSpace(ComposeFile);
    public bool ShowComposeFound => ComposeFileEntered && ComposeFileFound;
    public bool ShowComposeMissing => ComposeFileEntered && !ComposeFileFound;

    public static RepoEditViewModel Load(string repoPath, IGitService? git = null)
    {
        var vm = new RepoEditViewModel(repoPath) { _loading = true };
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
            });

        foreach (var e in c.Env)
        {
            var file = new EnvFileEditRow(vm.RemoveEnvRow, vm.ClassifyEnvFileAsync, vm.EnvKeysFor) { File = e.File };
            foreach (var kv in e.Set)
                file.Set.Add(new KvEditRow(r => file.Set.Remove(r)) { Key = kv.Key, Value = kv.Value });
            vm.Env.Add(file);
        }

        if (c.Compose is { } comp)
        {
            vm.HasCompose = true;
            vm.ComposeFile = comp.File;
            vm._composeSeed = comp.Overrides;
        }

        vm._loading = false;
        vm.RecomputeComposeFileStatus();
        vm.RebuildComposeOverlay();

        return vm;
    }

    void RemoveInputRow(InputEditRow r) => Inputs.Remove(r);
    void RemoveEnvRow(EnvFileEditRow r) => Env.Remove(r);

    // -- ${sprig.*} variables (workspace + declared inputs) --------------------

    void OnInputsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (InputEditRow r in e.OldItems) r.PropertyChanged -= OnInputRowChanged;
        if (e.NewItems is not null)
            foreach (InputEditRow r in e.NewItems) r.PropertyChanged += OnInputRowChanged;
        RefreshSprigVariableNames();
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

    partial void OnHasComposeChanged(bool value)
    {
        OnPropertyChanged(nameof(ComposeFileEntered));
        OnPropertyChanged(nameof(ShowComposeFound));
        OnPropertyChanged(nameof(ShowComposeMissing));
        if (_loading) return;
        RecomputeComposeFileStatus();
        RebuildComposeOverlay();
    }

    partial void OnComposeFileChanged(string value)
    {
        if (_loading) return;
        RecomputeComposeFileStatus();
        RebuildComposeOverlay();
    }

    partial void OnComposeFileFoundChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowComposeFound));
        OnPropertyChanged(nameof(ShowComposeMissing));
    }

    /// <summary>The compose override target must exist in the repo — recompute whether it does.</summary>
    void RecomputeComposeFileStatus()
    {
        var rel = ComposeFile.Trim();
        bool found;
        try { found = rel.Length > 0 && System.IO.File.Exists(Path.Combine(RepoPath, rel)); }
        catch { found = false; }
        ComposeFileFound = found;
        OnPropertyChanged(nameof(ComposeFileEntered));
    }

    /// <summary>(Re)build the overlay from the current compose file, carrying forward whatever
    /// overrides are already in play (live edits, else the values loaded from disk).</summary>
    void RebuildComposeOverlay()
    {
        if (!HasCompose) { ComposeOverlay = null; return; }

        var seed = ComposeOverlay?.ToOverrides() ?? _composeSeed;
        var abs = Path.Combine(RepoPath, ComposeFile.Trim());
        var text = "";
        try { if (ComposeFile.Trim().Length > 0 && System.IO.File.Exists(abs)) text = System.IO.File.ReadAllText(abs); }
        catch { /* unreadable → empty overlay; the ✓/⚠ subtext already flags a missing file */ }

        ComposeOverlay = new ComposeOverlayViewModel(text, seed, SprigVariableNames);
    }

    [RelayCommand] private void AddInput() => Inputs.Add(new InputEditRow(RemoveInputRow));

    [RelayCommand]
    private void AddEnvFile()
    {
        var file = new EnvFileEditRow(RemoveEnvRow, ClassifyEnvFileAsync, EnvKeysFor);
        file.Set.Add(new KvEditRow(r => file.Set.Remove(r)));
        Env.Add(file);
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
        }).ToList(),
        Env = Env.Select(e => new EnvOverride
        {
            File = e.File.Trim(),
            Set = ToDict(e.Set),
        }).ToList(),
        Compose = HasCompose
            ? new ComposeConfig
            {
                File = ComposeFile.Trim(),
                Overrides = ComposeOverlay?.ToOverrides() ?? [],
            }
            : null,
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

        // The compose override target must exist in the repo — otherwise generation fails at
        // workspace-creation time with a much less obvious error. (Blank is caught by the validator.)
        if (config.Compose is { File.Length: > 0 } cc && !System.IO.File.Exists(Path.Combine(RepoPath, cc.File)))
        {
            Error = $"compose file not found in the repo: {cc.File}";
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
