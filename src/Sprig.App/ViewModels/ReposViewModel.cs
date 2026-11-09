using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;
using Sprig.Model;

namespace Sprig.App.ViewModels;

/// <summary>One repo in the Repos list. Its summary arrives once its definition has run.</summary>
public partial class RepoItem(RepoEntry entry) : ObservableObject
{
    public RepoEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public bool InRepo => Entry.InRepo;
    public string DisplayFolder => Entry.Folder.Replace('\\', '/');
    public string DisplayFile => Entry.DefinitionFile.Replace('\\', '/');
    public string Kept => InRepo ? "in repo" : "in sprig";
    public bool Missing => !File.Exists(Entry.DefinitionFile);

    [ObservableProperty] private string _summary = "";
}

/// <summary>A line in one of a repo's sections: what it runs, its infra, what it offers, its settings.</summary>
public sealed record RepoFact(string Name, string Detail);

/// <summary>One of the repo's own workspaces, on its page.</summary>
/// <param name="Where">"in place", or the branch its worktree is on.</param>
public sealed record RepoWorkspaceRow(string Key, string Name, string Where, string State, bool IsUp, bool IsFailed);

/// <summary>
/// The Repos page: every repo sprig knows by ID, what each one runs, offers and needs, and where its definition is
/// kept. A repo is described here once; orchestrations only pick repos and fill what they need.
/// </summary>
public partial class ReposViewModel : PageViewModel
{
    readonly AppServices _services;

    public override string Title => "Repos";
    public override string? NavSubtitle => "what each repo runs";

    public ObservableCollection<RepoItem> Repos { get; } = [];
    public ObservableCollection<string> UsedBy { get; } = [];
    public ObservableCollection<RepoFact> Runs { get; } = [];
    public ObservableCollection<RepoFact> Infra { get; } = [];
    public ObservableCollection<RepoFact> Offers { get; } = [];
    public ObservableCollection<RepoFact> Settings { get; } = [];
    public ObservableCollection<RepoWorkspaceRow> WorkspacesOfIt { get; } = [];

    public bool HasWorkspaces => WorkspacesOfIt.Count > 0;

    public DefinitionEditorViewModel Editor { get; }

    /// <summary>Opens an orchestration on the Orchestrations page, by ID.</summary>
    public Action<string>? OpenOrchestration { get; set; }

    /// <summary>Opens the new-workspace form for a repo, to run it on its own.</summary>
    public Action<string>? StartWorkspace { get; set; }

    /// <summary>Opens a workspace on the Workspaces page, by its key, where its apps are started, stopped and followed.</summary>
    public Action<string>? OpenWorkspace { get; set; }

    [RelayCommand]
    private void OpenWorkspaceRow(RepoWorkspaceRow? row)
    {
        if (row is not null) OpenWorkspace?.Invoke(row.Key);
    }

    void ListWorkspaces(RepoItem? repo)
    {
        WorkspacesOfIt.Clear();
        if (repo is not null)
            foreach (var w in _services.Workspaces.List().Where(w => w.Orchestration == repo.Id).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase))
                WorkspacesOfIt.Add(new RepoWorkspaceRow(w.Key, w.Name, w.InPlace ? "in place" : $"on {w.Branch}", w.State switch
                {
                    Sprig.Engine.Workspaces.WorkspaceState.Up => "up",
                    Sprig.Engine.Workspaces.WorkspaceState.Starting => "coming up",
                    Sprig.Engine.Workspaces.WorkspaceState.Failed => "failed",
                    _ => "down",
                }, w.State == Sprig.Engine.Workspaces.WorkspaceState.Up, w.State == Sprig.Engine.Workspaces.WorkspaceState.Failed));
        OnPropertyChanged(nameof(HasWorkspaces));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(KeptExplanation), nameof(DeriveLabel), nameof(DeriveNote), nameof(RemoveNote))]
    private RepoItem? _selected;

    [ObservableProperty] private string _check = "";
    [ObservableProperty] private bool _checkBad;
    [ObservableProperty] private bool _hasModel;
    [ObservableProperty] private bool _needsApproval;
    [ObservableProperty] private bool _hasUsedBy;
    [ObservableProperty] private bool _confirmingRemove;

    [ObservableProperty] private bool _isChooseOpen;
    [ObservableProperty] private bool _isAddOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddTitle), nameof(AddExplanation), nameof(AddPickerTitle))]
    private bool _addInRepo;
    [ObservableProperty] private string _addFolder = "";
    [ObservableProperty] private string _addId = "";
    [ObservableProperty] private string _addMessage = "";
    [ObservableProperty] private bool _addFailed;

    [ObservableProperty] private bool _isDeriveOpen;
    [ObservableProperty] private string _deriveId = "";
    [ObservableProperty] private string _deriveMessage = "";
    [ObservableProperty] private bool _deriveFailed;
    [ObservableProperty] private bool _deriveBlocked;

    NeedsTrust? _pending;

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Repos.Count == 0;
    public bool HasOffers => Offers.Count > 0;
    public bool HasSettings => Settings.Count > 0;
    public bool HasInfra => Infra.Count > 0;

    public string KeptExplanation => Selected switch
    {
        null => "",
        { InRepo: true } => "Uses the repo's own sprig.cs. Workspaces run their branch's version, and sprig asks before running one you haven't approved.",
        _ => "Kept on this machine, so nothing is written to the repo. The same for every workspace, since no branch can change it.",
    };

    /// <summary>The one way to get the other kind: a new entry made from this one, which stays as it is.</summary>
    public string DeriveLabel => Selected is { InRepo: true } ? "Copy into sprig…" : "Write to the repo…";

    public string DeriveNote => Selected switch
    {
        null => "",
        { InRepo: true } => "To try changes without touching the repo, copy it into sprig as a new ID.",
        _ => "To put it in the repo, write it there as sprig.cs; that's used from a new ID, and this one stays in sprig.",
    };

    public string DeriveTitle => Selected is { InRepo: true } ? "Copy into sprig" : "Write to the repo";

    public string DeriveExplanation => Selected switch
    {
        null => "",
        { InRepo: true } => $"Copies {Selected.DisplayFile} into sprig as a new repo, kept on this machine. '{Selected.Id}' keeps using the repo's file, and so do the orchestrations that use it.",
        _ => $"Writes this definition to {Selected.DisplayFolder}/sprig.cs, left uncommitted, and adds a new repo that uses it. '{Selected.Id}' stays in sprig as it is, and so do the orchestrations that use it.",
    };

    public string AddTitle => AddInRepo ? "Use the repo's sprig.cs" : "Write one in sprig";

    public string AddExplanation => AddInRepo
        ? "Choose a repo folder that has a sprig.cs. Workspaces run their branch's version, and nothing in it runs until you approve it."
        : "Kept in sprig, so nothing is written to the repo. It starts from the folder's sprig.cs if it has one, or else a draft from its projects, and opens for editing.";

    public string AddPickerTitle => AddInRepo ? "Choose a repo folder with a sprig.cs" : "Choose the repo's folder";

    public string RemoveNote => Selected is { InRepo: true }
        ? "It comes off the Repos list. The folder's own sprig.cs is left where it is. Orchestrations that use it will say it's missing."
        : "It comes off the Repos list, and the definition sprig keeps for it goes with it. Orchestrations that use it will say it's missing.";

    public ReposViewModel(AppServices services)
    {
        _services = services;
        Editor = new DefinitionEditorViewModel(services) { AfterSave = AfterSaveAsync };
        // Only counted here, for the nav; reading the repos runs their definitions, so it waits until the page is shown.
        NavCount = services.Stored.Repos().Count;
    }

    protected override void OnActivated()
    {
        if (!Editor.IsDirty) Refresh();
    }

    // Rebuilding the list re-runs the selected repo's definition, so it's left alone unless the Repos list changed.
    protected override void OnWindowFocused()
    {
        if (Editor.IsEditing || IsChooseOpen || IsAddOpen || IsDeriveOpen || ConfirmingRemove) return;
        if (_services.Stored.Repos().SequenceEqual(Repos.Select(r => r.Entry))) ListWorkspaces(Selected);
        else Refresh();
    }

    /// <summary>Show one repo, by ID.</summary>
    public void Show(string id)
    {
        Refresh();
        Selected = Repos.FirstOrDefault(r => r.Id == id) ?? Selected;
    }

    void Refresh(string? keep = null)
    {
        keep ??= Selected?.Id;
        Repos.Clear();
        foreach (var entry in _services.Stored.Repos()) Repos.Add(new RepoItem(entry));
        NavCount = Repos.Count;
        OnPropertyChanged(nameof(IsEmpty));
        Selected = Repos.FirstOrDefault(r => r.Id == keep) ?? Repos.FirstOrDefault();
        _ = SummariseAsync([.. Repos]);
    }

    /// <summary>Each row's "3 apps · 3 infra". Definitions are cached by content, so this only runs what changed, and
    /// never one that isn't approved.</summary>
    async Task SummariseAsync(IReadOnlyList<RepoItem> items)
    {
        foreach (var item in items)
        {
            var result = await AppServices.RunAsync(() => _services.Definitions.Evaluate(item.Entry.DefinitionFile));
            item.Summary = Summary(result);
        }
    }

    static string Summary(DefinitionResult result) => result switch
    {
        Evaluated { Model: RepoModel m } => Count(m.Apps.Count, "app") + (InfraCount(m) is > 0 and var n ? $" · {n} infra" : ""),
        Evaluated => "describes an orchestration",
        NeedsTrust => "not approved yet",
        _ => "doesn't run",
    };

    static int InfraCount(RepoModel m) => m.Containers.Count + m.ComposeFiles.Sum(f => f.Services.Count);

    static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    partial void OnSelectedChanged(RepoItem? value)
    {
        ConfirmingRemove = false;
        UsedBy.Clear();
        ClearModel();
        ListWorkspaces(value);
        if (value is null) return;
        foreach (var id in _services.Authoring.UsedBy(value.Id)) UsedBy.Add(id);
        HasUsedBy = UsedBy.Count > 0;
        _ = CheckAsync(value);
    }

    void ClearModel()
    {
        Runs.Clear();
        Infra.Clear();
        Offers.Clear();
        Settings.Clear();
        HasModel = false;
        NeedsApproval = false;
        _pending = null;
        RaiseSections();
    }

    void RaiseSections()
    {
        OnPropertyChanged(nameof(HasOffers));
        OnPropertyChanged(nameof(HasSettings));
        OnPropertyChanged(nameof(HasInfra));
    }

    /// <summary>Run the repo's definition on its own and show what it describes, so a mistake shows before any
    /// orchestration uses it.</summary>
    async Task CheckAsync(RepoItem repo)
    {
        if (repo.Missing)
        {
            Say($"{repo.DisplayFile} isn't there.", bad: true);
            return;
        }
        Say("Running it…", bad: false);
        var result = await AppServices.RunAsync(() => _services.Definitions.Evaluate(repo.Entry.DefinitionFile));
        if (Selected?.Id != repo.Id) return;
        repo.Summary = Summary(result);
        ClearModel();
        switch (result)
        {
            case Evaluated { Model: RepoModel model }:
                Show(model);
                Say("", bad: false);
                break;
            case Evaluated:
                Say("Its definition describes an orchestration, not a repo (it calls Orchestration.Describe).", bad: true);
                break;
            case NeedsTrust pending:
                _pending = pending;
                NeedsApproval = true;
                Say("Not approved yet, so it hasn't run. It's C# that sprig runs as you: read it, then approve it.", bad: true);
                break;
            case Failed failed:
                Say($"It doesn't run: {failed.Reason}. {FirstError(failed.Output)}", bad: true);
                break;
            case Rejected rejected:
                Say(string.Join(" ", rejected.Violations.Select(v => $"Line {v.Line}: {v.Reason}.")), bad: true);
                break;
        }
    }

    void Show(RepoModel model)
    {
        foreach (var app in model.Apps)
            Runs.Add(new RepoFact(app.Name, string.Join(" · ", RunDetail(app))));
        foreach (var c in model.Containers)
            Infra.Add(new RepoFact(c.Name, $"{c.Image} · {Endpoints(c.Endpoints)}"));
        foreach (var file in model.ComposeFiles)
            foreach (var service in file.Services)
                Infra.Add(new RepoFact(service.Name, $"from {file.File} · {Endpoints(service.Endpoints)}"));
        foreach (var export in model.Exports)
            Offers.Add(new RepoFact(export.Name, $"{Describe(export.Value)} · a {export.Type}"));
        foreach (var setting in model.Settings)
            Settings.Add(new RepoFact(setting.Name, setting.Default is { } own
                ? $"a {setting.Type}; {DescribeDefault(own)} unless a workspace or orchestration gives another"
                : $"a {setting.Type}; asked for when a workspace is made"));
        HasModel = true;
        RaiseSections();
    }

    static IEnumerable<string> RunDetail(AppModel app)
    {
        yield return app.Kind switch { "dotnet" => ".NET", "node" => "Node", var k => k };
        yield return app.Path;
        if (app.Job) yield return "a job: runs to the end, then what waits for it starts";
        var health = app.Endpoints.FirstOrDefault(e => e.Health is not null)?.Health;
        if (health is not null) yield return $"ready on {health}";
        else if (app.ReadyLog is not null) yield return "ready on a log line";
        if (app.WaitFor.Count > 0) yield return $"after {string.Join(", ", app.WaitFor)}";
    }

    static string Endpoints(IReadOnlyList<EndpointModel> endpoints)
        => endpoints.Count == 0 ? "no ports" : string.Join(", ", endpoints.Select(e => e.Name));

    /// <summary>"{catalog-api.http.url}" → "catalog-api's url"; literal text is kept, with passwords masked.</summary>
    public static string Describe(string template)
    {
        IReadOnlyList<TemplatePart> parts;
        try { parts = TemplateText.Parse(template); }
        catch (FormatException) { return template; }
        return string.Concat(parts.Select(p => p switch
        {
            TextPart t => ValueMask.Apply(t.Text),
            RefPart { Path: var path } when path.StartsWith("setting.", StringComparison.Ordinal) => $"its setting {path["setting.".Length..]}",
            RefPart { Path: var path } when path.Split('.') is [var resource, _, var what] => $"{resource}'s {what}",
            RefPart r => r.Path,
            _ => "",
        }));
    }

    /// <summary>"its own mail" for a default built from the repo's infra, else the value in words.</summary>
    public static string DescribeDefault(string template)
    {
        IReadOnlyList<TemplatePart> parts;
        try { parts = TemplateText.Parse(template); }
        catch (FormatException) { return template; }
        var own = parts.OfType<RefPart>().Select(p => p.Path.Split('.')).Where(bits => bits.Length == 3).Select(bits => bits[0]).Distinct().ToArray();
        return own.Length > 0 ? $"its own {string.Join(" and ", own)}" : Describe(template);
    }

    void Say(string message, bool bad)
    {
        Check = message;
        CheckBad = bad;
    }

    async Task<(string, bool)> AfterSaveAsync()
    {
        if (Selected is not { } repo) return ("Saved.", false);
        await CheckAsync(repo);
        return CheckBad ? ($"Saved. {Check}", true) : ("Saved, and it runs.", false);
    }

    // ---- actions ----

    [RelayCommand]
    private void Edit()
    {
        if (Selected is not { Missing: false } repo) return;
        var error = Editor.Open(repo.Entry.DefinitionFile, $"{repo.Id}: {repo.Kept}",
            repo.InRepo
                ? "In the repo, so saving leaves an uncommitted change there. Saving approves what you saved."
                : "In sprig, so no branch can change it. Saving approves what you saved.");
        if (error is not null) Say(error, bad: true);
    }

    [RelayCommand]
    private async Task Approve()
    {
        if (_pending is not { } pending || Selected is not { } repo) return;
        _services.Trust.Approve(pending.TrustPath, pending.Sha256);
        await CheckAsync(repo);
    }

    [RelayCommand]
    private void OpenDerive()
    {
        if (Selected is not { } repo) return;
        OnPropertyChanged(nameof(DeriveTitle));
        OnPropertyChanged(nameof(DeriveExplanation));
        DeriveId = _services.Authoring.SuggestId(repo.Entry, inRepo: !repo.InRepo);
        var blocked = repo.InRepo
            ? repo.Missing ? $"{repo.DisplayFile} isn't there, so there's nothing to copy." : null
            : _services.Authoring.WriteToRepoBlocker(repo.Entry);
        DeriveBlocked = blocked is not null;
        DeriveFailed = DeriveBlocked;
        DeriveMessage = blocked ?? "";
        IsDeriveOpen = true;
    }

    [RelayCommand]
    private void CancelDerive() => IsDeriveOpen = false;

    /// <summary>Make the new entry and show it. Nothing about the selected one changes.</summary>
    [RelayCommand]
    private async Task Derive()
    {
        if (Selected is not { } repo || DeriveBlocked) return;
        var id = DeriveId.Trim();
        if (Ids.Check(id, "a repo ID") is { } bad)
        {
            FailDerive(bad);
            return;
        }
        RepoEntry made;
        try
        {
            made = await AppServices.RunAsync(() => repo.InRepo
                ? _services.Authoring.CopyIntoSprig(repo.Id, id)
                : _services.Authoring.WriteToRepo(repo.Id, id));
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            FailDerive(e.Message);
            return;
        }
        IsDeriveOpen = false;
        Refresh(made.Id);
    }

    void FailDerive(string message)
    {
        DeriveFailed = true;
        DeriveMessage = message;
    }

    /// <summary>Only once its definition runs: the form would only say why it can't.</summary>
    [RelayCommand]
    private void Start()
    {
        if (Selected is { } repo && HasModel) StartWorkspace?.Invoke(repo.Id);
    }

    [RelayCommand]
    private void OpenOrchestrationById(string? id)
    {
        if (id is not null) OpenOrchestration?.Invoke(id);
    }

    /// <summary>Taking a repo kept in sprig off the list deletes its definition, and orchestrations using either kind
    /// break, so it asks first.</summary>
    [RelayCommand]
    private void Remove()
    {
        if (Selected is not { } repo) return;
        if (!ConfirmingRemove)
        {
            ConfirmingRemove = true;
            return;
        }
        ConfirmingRemove = false;
        _services.Authoring.DeleteRepo(repo.Id);
        Selected = null;
        Refresh();
    }

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    // ---- adding ----

    // The ID follows the folder until it's typed over.
    string _suggestedId = "";

    partial void OnAddFolderChanged(string value)
    {
        var folder = value.Trim().TrimEnd('/', '\\');
        if (Path.GetFileName(folder).Length == 0 || (AddId.Length > 0 && AddId != _suggestedId)) return;
        _suggestedId = _services.Authoring.SuggestNewId(folder, AddInRepo);
        AddId = _suggestedId;
    }

    /// <summary>+ Add starts with a choice, so neither form's fields show until it's made.</summary>
    [RelayCommand]
    private void OpenChoose()
    {
        IsAddOpen = false;
        IsChooseOpen = true;
    }

    [RelayCommand]
    private void CancelChoose() => IsChooseOpen = false;

    [RelayCommand]
    private void BackToChoose() => OpenChoose();

    [RelayCommand]
    private void OpenAddInRepo() => OpenAdd(inRepo: true);

    [RelayCommand]
    private void OpenAddInSprig() => OpenAdd(inRepo: false);

    void OpenAdd(bool inRepo)
    {
        IsChooseOpen = false;
        AddInRepo = inRepo;
        _suggestedId = "";
        AddId = "";
        AddFolder = "";
        AddMessage = "";
        AddFailed = false;
        IsAddOpen = true;
    }

    [RelayCommand]
    private void CancelAdd() => IsAddOpen = false;

    [RelayCommand]
    private async Task Add()
    {
        AddMessage = "";
        AddFailed = false;
        var folder = AddFolder.Trim();
        var id = AddId.Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            FailAdd("Choose the repo's folder.");
            return;
        }
        if (AddInRepo && !File.Exists(Path.Combine(folder, StoredDefinitions.RepoFileName)))
        {
            FailAdd("That folder has no sprig.cs. Go back and choose Write one in sprig to start one.");
            return;
        }
        if (Ids.Check(id, "a repo ID") is { } bad)
        {
            FailAdd(bad);
            return;
        }
        RepoEntry entry;
        var inRepo = AddInRepo;
        try { entry = await AppServices.RunAsync(() => _services.Authoring.AddRepo(folder, id, inRepo)); }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            FailAdd(e.Message);
            return;
        }
        IsAddOpen = false;
        Refresh(entry.Id);
        // A draft is a starting point; open it so it can be finished.
        if (!entry.InRepo) Edit();
    }

    void FailAdd(string message)
    {
        AddFailed = true;
        AddMessage = message;
    }

    static string FirstError(string output)
        => output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal)) ?? "";
}
