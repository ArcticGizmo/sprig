using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;
using Sprig.Engine.Trellises;
using Sprig.Model;

namespace Sprig.App.ViewModels;

/// <param name="Name">Its registered ID, which its sprouts are keyed by.</param>
/// <param name="NeedsApproval">The file's current content isn't approved, so it hasn't run yet.</param>
/// <param name="IsVirtual">Kept in sprig's store rather than a folder of the user's.</param>
public sealed record TrellisItem(string Name, string Path, bool NeedsApproval = false, bool IsVirtual = false)
{
    public string DisplayPath => IsVirtual ? "Kept in sprig" : Path.Replace('\\', '/');
    public string Folder => IsVirtual ? "kept in sprig" : System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
    public bool Missing => !File.Exists(Path);
}

/// <summary>One repo of the selected trellis, and where its definition comes from.</summary>
/// <param name="Path">The repo as the trellis names it.</param>
/// <param name="Definition">The ID of the definition sprig keeps for it, when the trellis names one.</param>
/// <param name="File">The definition file it uses, when there is one.</param>
public sealed record TrellisRepoRow(string Path, string Dir, string? Definition, bool HasOwn, string? File)
{
    public string Name => System.IO.Path.GetFileName(Dir.TrimEnd('/', '\\'));
    public string DisplayDir => Dir.Replace('\\', '/');
    public string Source => Definition is { } id ? $"Described in sprig as '{id}'" : HasOwn ? "Its own sprig.cs" : "No sprig.cs yet";
    public bool IsVirtual => Definition is not null;
    public bool Undescribed => Definition is null && !HasOwn;
    public bool CanEdit => File is not null && System.IO.File.Exists(File);
    public bool CanDescribe => Definition is null;
    public string DescribeLabel => HasOwn ? "Copy into sprig" : "Describe in sprig";
    public string DescribeTip => HasOwn
        ? "Use a copy kept in sprig instead, to try changes without committing them"
        : "Write a definition for it, kept in sprig, starting from what's in its folder";
    public bool CanUseOwn => Definition is not null && HasOwn;
}

/// <param name="Changed">An earlier version was approved; this content wasn't.</param>
/// <param name="TrustPath">Where the approval is kept: the repo's own sprig.cs, for a copy in a sprout's worktree.</param>
public sealed record PendingTrustItem(string Path, string Sha256, bool Changed, string TrustPath)
{
    public static PendingTrustItem From(NeedsTrust p) => new(p.Path.Replace('\\', '/'), p.Sha256, p.ApprovedSha256 is not null, p.TrustPath);

    public string Why => (Changed, Path != TrustPath.Replace('\\', '/')) switch
    {
        (false, _) => "not approved yet",
        (true, false) => "changed since you approved it",
        _ => "the sprout's branch changes it; this version isn't approved yet",
    };
}

/// <summary>
/// The Trellises page: register trellis files, and see what a sprout of the selected one would get. Definitions only
/// run once their exact content is approved; anything that isn't is listed for the user to approve first. Registering
/// doesn't need approval, so a trellis is in the list (and can be read) before anything in it has run.
/// </summary>
public partial class TrellisesViewModel : PageViewModel
{
    readonly AppServices _services;
    Func<Task>? _afterApproval;
    bool _quietSelection;

    public override string Title => "Trellises";

    public ObservableCollection<TrellisItem> Trellises { get; } = [];
    public ObservableCollection<PendingTrustItem> PendingTrust { get; } = [];
    public ObservableCollection<ProblemRow> LoadProblems { get; } = [];
    public ObservableCollection<TrellisRepoRow> Repos { get; } = [];
    public ObservableCollection<string> NewRepoDirs { get; } = [];

    [ObservableProperty] private bool _hasRepos;

    // ---- editing a definition ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _isEditing;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _editorText = "";
    [ObservableProperty] private string _editingTitle = "";
    [ObservableProperty] private string _editingPath = "";
    [ObservableProperty] private string _editingNote = "";
    [ObservableProperty] private string _editorStatus = "";
    [ObservableProperty] private bool _editorStatusBad;
    [ObservableProperty] private bool _confirmingDiscard;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _savedText = "";

    public bool IsDirty => IsEditing && EditorText != SavedText;

    public Sprig.App.Authoring.LanguageHostClient Language => _services.Language;

    // ---- a new trellis kept in sprig ----
    [ObservableProperty] private bool _isNewOpen;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newRepoPath = "";
    [ObservableProperty] private bool _newWorktrees = true;
    [ObservableProperty] private string _newFrom = "main";
    [ObservableProperty] private bool _newRandomPorts;
    [ObservableProperty] private string _newMessage = "";
    [ObservableProperty] private bool _newFailed;

    [ObservableProperty] private bool _confirmingRemove;

    [ObservableProperty] private TrellisItem? _selected;
    [ObservableProperty] private bool _isAddOpen;
    [ObservableProperty] private string _newPath = "";
    [ObservableProperty] private string _addMessage = "";
    [ObservableProperty] private bool _addFailed;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private PlanView? _plan;
    [ObservableProperty] private bool _hasPendingTrust;
    [ObservableProperty] private bool _pendingIsTrellis;
    [ObservableProperty] private bool _hasLoadProblems;

    public bool IsEmpty => Trellises.Count == 0;
    public bool HasSelection => Selected is not null;

    public TrellisesViewModel(AppServices services)
    {
        _services = services;
        Refresh();
    }

    protected override void OnActivated() => Refresh();

    void Refresh() => Refresh(Selected?.Name, replan: true);

    /// <param name="replan">False when the caller shows what goes with the selection itself.</param>
    void Refresh(string? keep, bool replan)
    {
        var previous = Selected;
        Trellises.Clear();
        foreach (var e in _services.Trellises.List())
            Trellises.Add(new TrellisItem(e.Id, e.Path, NeedsApproval(e.Path), _services.Virtuals.IsVirtual(e.Path)));
        NavCount = Trellises.Count;
        OnPropertyChanged(nameof(IsEmpty));
        _quietSelection = !replan;
        try { Selected = Trellises.FirstOrDefault(t => t.Name == keep) ?? previous ?? Trellises.FirstOrDefault(); }
        finally { _quietSelection = false; }
    }

    bool NeedsApproval(string path)
    {
        try { return _services.Trust.Check(path, DefinitionHash.Of(File.ReadAllText(path))).State != TrustState.Trusted; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    partial void OnSelectedChanged(TrellisItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is not null && !_quietSelection) _ = PlanSelectedAsync();
    }

    [RelayCommand]
    private void OpenAdd()
    {
        NewPath = "";
        AddMessage = "";
        AddFailed = false;
        IsAddOpen = true;
    }

    [RelayCommand]
    private void CancelAdd() => IsAddOpen = false;

    [RelayCommand]
    private Task Replan() => PlanSelectedAsync();

    async Task PlanSelectedAsync()
    {
        if (Selected is not { } trellis) return;
        ClearResults();
        if (trellis.Missing)
        {
            Repos.Clear();
            HasRepos = false;
            ShowLoadProblems([new PlanProblem(ProblemSeverity.Error, "the trellis file has moved or been deleted; remove it and add it again", new SourceLocation(trellis.Path, 0))], trellis.Path);
            return;
        }

        IsBusy = true;
        BusyText = "Running the definitions…";
        try
        {
            var loaded = await AppServices.RunAsync(() => _services.Loader.Load(trellis.Path));
            ShowRepos(loaded);
            if (loaded.Untrusted.Count > 0)
            {
                AskForTrust(loaded.Untrusted, PlanSelectedAsync);
                return;
            }
            if (loaded.Trellis is null || loaded.Problems.Count > 0)
            {
                ShowLoadProblems(loaded.Problems, trellis.Path);
                return;
            }
            var settings = _services.Settings.Get();
            var plan = await AppServices.RunAsync(() =>
            {
                var problems = new System.Collections.Generic.List<PlanProblem>();
                var built = Planner.Build(new PlanInput(loaded.Trellis, loaded.Repos, "plan", settings, PortAllocator.Listening(),
                    ComposeFiles: loaded.ComposeFiles(problems)));
                if (!built.HasErrors) problems.AddRange(_services.Sprouts.CheckFiles(built, loaded.RepoDirs));
                return built with { Problems = [.. problems, .. built.Problems] };
            });
            Plan = new PlanView(plan, Path.GetDirectoryName(trellis.Path)!);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Add()
    {
        AddMessage = "";
        AddFailed = false;
        var path = NewPath.Trim();
        if (path.Length == 0 || !File.Exists(path))
        {
            Fail("Choose a trellis file (a .trellis.cs) that exists.");
            return;
        }
        path = LongPath.Of(path);

        IsBusy = true;
        BusyText = "Reading the trellis…";
        DefinitionResult result;
        try { result = await AppServices.RunAsync(() => _services.Definitions.Evaluate(path)); }
        finally { IsBusy = false; }

        switch (result)
        {
            case NeedsTrust pending:
                var guessed = _services.Trellises.Add(_services.Trellises.UnusedId(TrellisRegistry.IdFromFile(path), path), path);
                CloseAdd();
                Refresh(guessed.Id, replan: false);
                ClearResults();
                AskForTrust([pending], PlanSelectedAsync);
                return;
            case Evaluated { Model: TrellisModel model }:
                var entry = _services.Trellises.Add(_services.Trellises.UnusedId(model.Name, path), path);
                CloseAdd();
                Refresh(entry.Id, replan: true);
                return;
            case Evaluated:
                Fail("That file describes a repo (Repo.Describe), not a trellis.");
                return;
            case Failed failed:
                Fail($"It didn't run: {failed.Reason}. {FirstError(failed.Output)}");
                return;
            case Rejected rejected:
                Fail(string.Join(" ", rejected.Violations.Select(v => $"Line {v.Line}: {v.Reason}.")));
                return;
        }
    }

    [RelayCommand]
    private async Task ApproveAll()
    {
        foreach (var item in PendingTrust) _services.Trust.Approve(item.TrustPath, item.Sha256);
        PendingTrust.Clear();
        HasPendingTrust = false;
        Refresh(Selected?.Name, replan: false);
        if (_afterApproval is { } next)
        {
            _afterApproval = null;
            await next();
        }
    }

    /// <summary>A trellis kept in sprig exists nowhere else, so removing one asks first.</summary>
    [RelayCommand]
    private void Remove(TrellisItem? item)
    {
        if (item is null) return;
        if (item.IsVirtual && !ConfirmingRemove)
        {
            ConfirmingRemove = true;
            return;
        }
        ConfirmingRemove = false;
        _services.Authoring.RemoveTrellis(item.Name);
        if (Selected == item) Selected = null;
        ClearResults();
        Repos.Clear();
        HasRepos = false;
        Refresh();
    }

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    void ShowRepos(LoadedTrellis loaded)
    {
        Repos.Clear();
        foreach (var reference in loaded.Trellis?.Repos ?? [])
        {
            var dir = loaded.RepoDirs.GetValueOrDefault(reference.Path) ?? reference.Path;
            var own = Path.Combine(dir, TrellisLoader.RepoDefinitionFile);
            var hasOwn = File.Exists(own);
            var file = reference.Definition is { } id ? _services.Virtuals.RepoFile(id) : hasOwn ? own : null;
            Repos.Add(new TrellisRepoRow(reference.Path, dir, reference.Definition, hasOwn, file));
        }
        HasRepos = Repos.Count > 0;
    }

    // ---- editing ----

    [RelayCommand]
    private void EditTrellis()
    {
        if (Selected is not { } trellis || trellis.Missing) return;
        OpenEditor(trellis.Path, $"{trellis.Name}: the trellis",
            trellis.IsVirtual ? "Kept in sprig. Saving approves what you saved." : "In your folder, so saving changes the file there. Saving approves what you saved.");
    }

    [RelayCommand]
    private void EditRepo(TrellisRepoRow? row)
    {
        if (row?.File is not { } file || !row.CanEdit) return;
        OpenEditor(file, $"{row.Name}: {(row.IsVirtual ? $"'{row.Definition}', kept in sprig" : "its own sprig.cs")}",
            row.IsVirtual
                ? "Kept in sprig, so no branch can change it. Saving approves what you saved."
                : "In the repo, so saving leaves an uncommitted change there. Saving approves what you saved.");
    }

    [RelayCommand]
    private async Task DescribeInSprig(TrellisRepoRow? row)
    {
        if (row is null || Selected is not { } trellis) return;
        string file;
        try { file = await AppServices.RunAsync(() => _services.Authoring.DescribeInSprig(trellis.Path, row.Path, row.Dir)); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            ShowLoadProblems([new PlanProblem(ProblemSeverity.Error, e.Message, new SourceLocation(trellis.Path, 0))], trellis.Path);
            return;
        }
        await PlanSelectedAsync();
        if (Repos.FirstOrDefault(r => r.Path == row.Path) is { } described) EditRepo(described);
        else OpenEditor(file, $"{row.Name}: kept in sprig", "Kept in sprig. Saving approves what you saved.");
    }

    [RelayCommand]
    private async Task UseOwnDefinition(TrellisRepoRow? row)
    {
        if (row is null || Selected is not { } trellis) return;
        try { await AppServices.RunAsync(() => _services.Authoring.UseOwnDefinition(trellis.Path, row.Path)); }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            ShowLoadProblems([new PlanProblem(ProblemSeverity.Error, e.Message, new SourceLocation(trellis.Path, 0))], trellis.Path);
            return;
        }
        await PlanSelectedAsync();
    }

    void OpenEditor(string file, string title, string note)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (IOException e)
        {
            ShowLoadProblems([new PlanProblem(ProblemSeverity.Error, e.Message, new SourceLocation(file, 0))], file);
            return;
        }
        EditingPath = file;
        EditingTitle = title;
        EditingNote = note;
        SavedText = text;
        EditorText = text;
        EditorStatus = "";
        EditorStatusBad = false;
        ConfirmingDiscard = false;
        IsEditing = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>Save, approve and plan again, saying in the editor whether it now plans.</summary>
    [RelayCommand]
    private async Task Save()
    {
        if (!IsEditing) return;
        var text = EditorText;
        try { await AppServices.RunAsync(() => _services.Authoring.Save(EditingPath, text)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SayEditor($"Couldn't save: {e.Message}", bad: true);
            return;
        }
        SavedText = text;
        SayEditor("Saved. Planning…", bad: false);
        await PlanSelectedAsync();
        var errors = Plan?.Problems.Count(p => p.IsError) ?? 0;
        if (HasPendingTrust) SayEditor("Saved. Something it uses needs approving: close the editor to see what.", bad: true);
        else if (HasLoadProblems) SayEditor($"Saved, but it doesn't plan: {LoadProblems.FirstOrDefault()?.Message}", bad: true);
        else if (errors > 0) SayEditor($"Saved. The plan has {errors} problem{(errors == 1 ? "" : "s")}: close the editor to see {(errors == 1 ? "it" : "them")}.", bad: true);
        else SayEditor($"Saved, and it plans. {Plan?.Summary}", bad: false);
    }

    void SayEditor(string message, bool bad)
    {
        EditorStatus = message;
        EditorStatusBad = bad;
    }

    [RelayCommand]
    private void CloseEditor()
    {
        if (IsDirty && !ConfirmingDiscard)
        {
            ConfirmingDiscard = true;
            return;
        }
        ConfirmingDiscard = false;
        IsEditing = false;
        EditorText = "";
        SavedText = "";
    }

    [RelayCommand]
    private void KeepEditing() => ConfirmingDiscard = false;

    // ---- a new trellis kept in sprig ----

    [RelayCommand]
    private void OpenNew()
    {
        IsAddOpen = false;
        NewName = "";
        NewRepoPath = "";
        NewRepoDirs.Clear();
        NewWorktrees = true;
        NewFrom = "main";
        NewRandomPorts = false;
        NewMessage = "";
        NewFailed = false;
        IsNewOpen = true;
    }

    [RelayCommand]
    private void CancelNew() => IsNewOpen = false;

    [RelayCommand]
    private void SwitchToAdd()
    {
        IsNewOpen = false;
        OpenAdd();
    }

    [RelayCommand]
    private void AddNewRepo()
    {
        NewMessage = "";
        NewFailed = false;
        var path = NewRepoPath.Trim();
        if (path.Length == 0) return;
        if (!Directory.Exists(path))
        {
            FailNew($"There's no folder at {path}.");
            return;
        }
        var full = LongPath.Of(path).Replace('\\', '/');
        if (NewRepoDirs.Any(d => string.Equals(d, full, StringComparison.OrdinalIgnoreCase))) return;
        NewRepoDirs.Add(full);
        NewRepoPath = "";
        if (NewName.Length == 0 && NewRepoDirs.Count == 1) NewName = Path.GetFileName(full.TrimEnd('/'));
    }

    [RelayCommand]
    private void RemoveNewRepo(string? dir)
    {
        if (dir is not null) NewRepoDirs.Remove(dir);
    }

    [RelayCommand]
    private async Task CreateNew()
    {
        NewMessage = "";
        NewFailed = false;
        if (NewRepoPath.Trim().Length > 0) AddNewRepo();
        if (NewFailed) return;
        if (NewName.Trim().Length == 0)
        {
            FailNew("Name it.");
            return;
        }
        if (NewRepoDirs.Count == 0)
        {
            FailNew("Add at least one repo folder.");
            return;
        }
        var from = NewFrom.Trim();
        if (NewWorktrees && from.Length == 0)
        {
            FailNew("Say which branch new sprouts' branches start from.");
            return;
        }

        IsBusy = true;
        BusyText = "Writing the trellis…";
        TrellisEntry entry;
        try
        {
            var dirs = NewRepoDirs.ToArray();
            var name = NewName.Trim();
            var worktrees = NewWorktrees ? from : null;
            var ports = NewRandomPorts ? "Random" : "Stable";
            entry = await AppServices.RunAsync(() => _services.Authoring.CreateTrellis(name, dirs, worktrees, ports));
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidOperationException)
        {
            FailNew(e.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }
        IsNewOpen = false;
        Refresh(entry.Id, replan: true);
    }

    void FailNew(string message)
    {
        NewFailed = true;
        NewMessage = message;
    }

    void AskForTrust(System.Collections.Generic.IEnumerable<NeedsTrust> pending, Func<Task> then)
    {
        PendingTrust.Clear();
        foreach (var p in pending) PendingTrust.Add(PendingTrustItem.From(p));
        HasPendingTrust = PendingTrust.Count > 0;
        // Only a trellis that has run can say which repos it uses, so their definitions are a second ask.
        PendingIsTrellis = Selected is { } trellis && PendingTrust.Any(p => string.Equals(p.Path, trellis.DisplayPath, StringComparison.OrdinalIgnoreCase));
        _afterApproval = then;
    }

    void ShowLoadProblems(System.Collections.Generic.IEnumerable<PlanProblem> problems, string trellisPath)
    {
        LoadProblems.Clear();
        foreach (var row in PlanView.Rows(problems, Path.GetDirectoryName(trellisPath)!)) LoadProblems.Add(row);
        HasLoadProblems = LoadProblems.Count > 0;
    }

    void ClearResults()
    {
        Plan = null;
        PendingTrust.Clear();
        HasPendingTrust = false;
        LoadProblems.Clear();
        HasLoadProblems = false;
    }

    void CloseAdd()
    {
        IsAddOpen = false;
        NewPath = "";
        AddMessage = "";
        AddFailed = false;
    }

    void Fail(string message)
    {
        AddFailed = true;
        AddMessage = message;
    }

    static string FirstError(string output)
        => output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal)) ?? "";
}
