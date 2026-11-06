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
using Sprig.Engine.Workspaces;
using Sprig.Engine.Store;
using Sprig.Engine.Orchestrations;
using Sprig.Model;

namespace Sprig.App.ViewModels;

/// <param name="Name">Its registered ID, which its workspaces are keyed by.</param>
/// <param name="NeedsApproval">The file's current content isn't approved, so it hasn't run yet.</param>
/// <param name="InSprig">Kept in sprig's store rather than a folder of the user's.</param>
/// <param name="Caption">Its workspaces at a glance, or why it can't run yet.</param>
public sealed record OrchestrationItem(string Name, string Path, bool NeedsApproval = false, bool InSprig = false, string Caption = "", bool CaptionIsGood = false)
{
    public string DisplayPath => Path.Replace('\\', '/');
    public string Kept => InSprig ? "in sprig" : "in a folder";
    public bool Missing => !File.Exists(Path);
}

/// <param name="Changed">An earlier version was approved; this content wasn't.</param>
/// <param name="TrustPath">Where the approval is kept: the repo's own sprig.cs, for a copy in a workspace's worktree.</param>
public sealed record PendingTrustItem(string Path, string Sha256, bool Changed, string TrustPath)
{
    public static PendingTrustItem From(NeedsTrust p) => new(p.Path.Replace('\\', '/'), p.Sha256, p.ApprovedSha256 is not null, p.TrustPath);

    public string Why => (Changed, Path != TrustPath.Replace('\\', '/')) switch
    {
        (false, _) => "not approved yet",
        (true, false) => "changed since you approved it",
        _ => "the workspace's branch changes it; this version isn't approved yet",
    };
}

public enum OrchestrationTab { Overview, Values, Ports, Code }

public enum StatusKind { None, Good, Warn, Bad }

/// <summary>
/// The Orchestrations page. An orchestration opens on a summary: what it runs, whether it can run, how its workspaces
/// run, and a way to start one. What every app gets, each port, and the code sit in their own tabs. Definitions only run
/// once their exact content is approved; anything that isn't is listed for the user to approve first.
/// </summary>
public partial class OrchestrationsViewModel : PageViewModel
{
    readonly AppServices _services;
    Func<Task>? _afterApproval;
    bool _quietSelection;

    public override string Title => "Orchestrations";
    public override string? NavSubtitle => "repos run together";

    public ObservableCollection<OrchestrationItem> Orchestrations { get; } = [];
    public ObservableCollection<PendingTrustItem> PendingTrust { get; } = [];
    public ObservableCollection<ProblemRow> LoadProblems { get; } = [];
    public ObservableCollection<RepoCard> Cards { get; } = [];
    public ObservableCollection<AddedRow> Added { get; } = [];
    public ObservableCollection<PolicyRow> Policies { get; } = [];
    public ObservableCollection<WorkspaceRow> WorkspacesOfIt { get; } = [];
    public ObservableCollection<ChecklistStep> Steps { get; } = [];
    public ObservableCollection<GroupChip> GroupChips { get; } = [];

    // ---- the picture ----
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture), nameof(ShowCards))]
    private OrchestrationGraph? _graph;
    [ObservableProperty] private IReadOnlyList<GraphWire> _graphLines = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InspectingApp), nameof(InspectingCard))]
    private AppSection? _inspectedApp;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InspectingCard))]
    private RepoCard? _inspectedCard;

    Plan? _model;
    string? _selectedAppKey;
    string? _groupShown;

    public bool HasPicture => Graph is { Boxes.Count: > 0 };
    public bool ShowCards => !HasPicture && HasCards;
    public bool InspectingApp => InspectedApp is not null;
    public bool InspectingCard => InspectedApp is null && InspectedCard is not null;
    public ObservableCollection<string> NewRepoDirs { get; } = [];

    public DefinitionEditorViewModel Editor { get; }

    /// <summary>The files waiting for approval, read and approved one at a time.</summary>
    public CodeReviewViewModel Review { get; }

    /// <summary>The dialog that writes the line filling a need, opened from the checklist.</summary>
    public FillNeedsViewModel FillNeeds { get; }

    IReadOnlyList<(string Repo, Sprig.Engine.Authoring.UnfilledNeed Need)> _unfilled = [];

    /// <summary>Opens a repo on the Repos page, by ID.</summary>
    public Action<string>? OpenRepo { get; set; }

    /// <summary>Opens the new-workspace form with this orchestration chosen, by ID.</summary>
    public Action<string>? StartWorkspace { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(NothingSelected))]
    private OrchestrationItem? _selected;

    [ObservableProperty] private OrchestrationTab _tab;
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private StatusKind _statusKind;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private PlanView? _plan;
    [ObservableProperty] private bool _hasPendingTrust;
    [ObservableProperty] private bool _pendingIsOrchestration;
    [ObservableProperty] private bool _hasLoadProblems;
    [ObservableProperty] private bool _hasAdded;
    [ObservableProperty] private bool _hasWorkspaces;
    [ObservableProperty] private bool _confirmingRemove;
    [ObservableProperty] private bool _showChecklist;

    // ---- adding an orchestration ----
    /// <summary>The first step of + Add: which kind of orchestration, before any of either form's fields.</summary>
    [ObservableProperty] private bool _isChooseOpen;
    [ObservableProperty] private bool _isAddOpen;
    [ObservableProperty] private string _newPath = "";
    [ObservableProperty] private string _addMessage = "";
    [ObservableProperty] private bool _addFailed;

    [ObservableProperty] private bool _isNewOpen;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newRepoPath = "";
    [ObservableProperty] private string _newMessage = "";
    [ObservableProperty] private bool _newFailed;

    public bool IsEmpty => Orchestrations.Count == 0;
    public bool HasSelection => Selected is not null;
    public bool NothingSelected => Selected is null;
    public bool HasCards => Cards.Count > 0;
    public bool HasGroups => GroupChips.Count > 0;
    public bool ShowsPlan => Plan is not null;
    public bool IsOverview => Tab == OrchestrationTab.Overview;
    public bool IsValues => Tab == OrchestrationTab.Values;
    public bool IsPorts => Tab == OrchestrationTab.Ports;
    public bool IsCode => Tab == OrchestrationTab.Code;
    public bool StatusGood => StatusKind == StatusKind.Good;
    public bool StatusWarn => StatusKind == StatusKind.Warn;
    public bool StatusBad => StatusKind == StatusKind.Bad;
    public string ValuesTabLabel => Plan is { } p ? $"Values  {p.Apps.Sum(a => a.Rows.Count)}" : "Values";
    public string PortsTabLabel => Plan is { } p ? $"Ports  {p.Ports.Count}" : "Ports";

    partial void OnStatusKindChanged(StatusKind value)
    {
        OnPropertyChanged(nameof(StatusGood));
        OnPropertyChanged(nameof(StatusWarn));
        OnPropertyChanged(nameof(StatusBad));
    }

    public OrchestrationsViewModel(AppServices services)
    {
        _services = services;
        Editor = new DefinitionEditorViewModel(services) { AfterSave = AfterSaveAsync };
        Editor.Closed += () => Tab = OrchestrationTab.Overview;
        Review = new CodeReviewViewModel(services.Trust) { AllApproved = ApproveAll };
        FillNeeds = new FillNeedsViewModel
        {
            Apply = async (file, text) =>
            {
                await AppServices.RunAsync(() => _services.Authoring.ApplyEdit(file, text));
                await PlanSelectedAsync();
            },
            OpenCode = () => Tab = OrchestrationTab.Code,
        };
        Review.Approved += SetStatus;
        // Only listed here, for the nav's count; planning runs definitions, so it waits until the page is shown.
        Refresh(null, replan: false);
    }

    protected override void OnActivated() => Refresh(Selected?.Name, replan: !Editor.IsDirty);

    /// <summary>Show one orchestration, by ID.</summary>
    public void Show(string id)
    {
        if (Editor.IsDirty) return;
        if (Editor.IsEditing) Editor.CloseCommand.Execute(null);
        Tab = OrchestrationTab.Overview;
        Refresh(id, replan: true);
    }

    /// <param name="replan">False when the caller shows what goes with the selection itself.</param>
    void Refresh(string? keep, bool replan)
    {
        var previous = Selected?.Name;
        var workspaces = _services.Workspaces.List();
        _quietSelection = true;
        try
        {
            Orchestrations.Clear();
            foreach (var e in _services.Orchestrations.List())
            {
                var needsApproval = NeedsApproval(e.Path);
                var mine = workspaces.Where(s => s.Orchestration == e.Id).ToArray();
                var up = mine.Count(s => s.State == WorkspaceState.Up);
                var caption = needsApproval ? "not approved yet"
                    : mine.Length == 0 ? "no workspaces"
                    : up > 0 ? $"{Count(up, "workspace")} up"
                    : $"{Count(mine.Length, "workspace")}, none up";
                Orchestrations.Add(new OrchestrationItem(e.Id, e.Path, needsApproval, _services.Stored.IsKept(e.Path), caption, up > 0));
            }
            NavCount = Orchestrations.Count;
            OnPropertyChanged(nameof(IsEmpty));
            Selected = Orchestrations.FirstOrDefault(t => t.Name == keep) ?? Orchestrations.FirstOrDefault(t => t.Name == previous) ?? Orchestrations.FirstOrDefault();
        }
        finally { _quietSelection = false; }
        if (replan && Selected is not null) _ = PlanSelectedAsync();
    }

    bool NeedsApproval(string path)
    {
        try { return _services.Trust.Check(path, DefinitionHash.Of(File.ReadAllText(path))).State != TrustState.Trusted; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    partial void OnSelectedChanged(OrchestrationItem? value)
    {
        ConfirmingRemove = false;
        if (value is not null && !_quietSelection) _ = PlanSelectedAsync();
    }

    partial void OnTabChanged(OrchestrationTab value)
    {
        OnPropertyChanged(nameof(IsOverview));
        OnPropertyChanged(nameof(IsValues));
        OnPropertyChanged(nameof(IsPorts));
        OnPropertyChanged(nameof(IsCode));
        if (value == OrchestrationTab.Code && !Editor.IsEditing) OpenOrchestrationCode();
    }

    [RelayCommand]
    private void ShowTab(OrchestrationTab tab) => Tab = tab;

    partial void OnPlanChanged(PlanView? value)
    {
        OnPropertyChanged(nameof(ShowsPlan));
        OnPropertyChanged(nameof(ValuesTabLabel));
        OnPropertyChanged(nameof(PortsTabLabel));
    }

    // ---- planning ----

    [RelayCommand]
    private Task Replan() => PlanSelectedAsync();

    /// <summary>The definition files the last load of the selected orchestration read, by where their approval is kept.</summary>
    IReadOnlyList<string> _definitionFiles = [];

    /// <summary>Removing an orchestration keeps its approvals, so re-adding it asks nothing; this is the way to be asked
    /// again, as on first use. Its repos' sprig.cs files are approved per file, so other orchestrations using them ask
    /// again too.</summary>
    [RelayCommand]
    private void ForgetApprovals()
    {
        if (Selected is not { Missing: false } orchestration) return;
        foreach (var file in _definitionFiles.Append(orchestration.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            _services.Trust.Revoke(file);
        Refresh(orchestration.Name, replan: true);
    }

    int _planVersion;

    async Task PlanSelectedAsync()
    {
        if (Selected is not { } orchestration) return;
        var version = ++_planVersion;
        ClearResults();
        Description = "";
        if (orchestration.Missing)
        {
            ShowSummary(null, null);
            ShowLoadProblems([new PlanProblem(ProblemSeverity.Error, "the orchestration file has moved or been deleted; remove it and add it again", new SourceLocation(orchestration.Path, 0))], orchestration.Path);
            SetStatus();
            return;
        }

        IsBusy = true;
        SetStatus();
        try
        {
            var loaded = await AppServices.RunAsync(() => _services.Loader.Load(orchestration.Path));
            if (version != _planVersion) return;
            _definitionFiles = [.. loaded.Results.Select(r => r is NeedsTrust pending ? pending.TrustPath : r.Path)];
            Description = loaded.Orchestration is null ? "" : OrchestrationSummary.Description(loaded);
            if (loaded.Untrusted.Count > 0)
            {
                ShowSummary(loaded, null);
                AskForTrust(loaded.Untrusted, PlanSelectedAsync);
                return;
            }
            if (loaded.Orchestration is null || loaded.Problems.Count > 0)
            {
                ShowSummary(loaded, null);
                ShowLoadProblems(loaded.Problems, orchestration.Path);
                return;
            }
            var settings = _services.Settings.Get();
            var plan = await AppServices.RunAsync(() =>
            {
                var problems = new List<PlanProblem>();
                var built = Planner.Build(new PlanInput(loaded.Orchestration, loaded.Repos, "plan", settings, PortAllocator.Listening(),
                    ComposeFiles: loaded.ComposeFiles(problems)));
                if (!built.HasErrors) problems.AddRange(_services.Workspaces.CheckFiles(built, loaded.RepoDirs));
                return built with { Problems = [.. problems, .. built.Problems] };
            });
            if (version != _planVersion) return;
            Plan = new PlanView(plan, Path.GetDirectoryName(orchestration.Path)!);
            ShowSummary(loaded, plan);
        }
        finally
        {
            if (version == _planVersion)
            {
                IsBusy = false;
                SetStatus();
            }
        }
    }

    void ShowSummary(LoadedOrchestration? loaded, Plan? plan)
    {
        Cards.Clear();
        if (loaded is not null)
            foreach (var card in OrchestrationSummary.Cards(loaded, plan, _services.Stored)) Cards.Add(card);
        _unfilled = loaded is null ? [] : OrchestrationSummary.Unfilled(loaded, [.. Cards]);
        OnPropertyChanged(nameof(HasCards));
        ShowPicture(plan);
        Added.Clear();
        Policies.Clear();
        if (plan is not null)
        {
            foreach (var row in OrchestrationSummary.Added(plan)) Added.Add(row);
            foreach (var row in OrchestrationSummary.Policies(plan)) Policies.Add(row);
        }
        HasAdded = Added.Count > 0;
        WorkspacesOfIt.Clear();
        if (Selected is { } orchestration)
            foreach (var s in _services.Workspaces.List().Where(s => s.Orchestration == orchestration.Name).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                WorkspacesOfIt.Add(new WorkspaceRow(s.Name, s.State switch
                {
                    WorkspaceState.Up => "up",
                    WorkspaceState.Starting => "coming up",
                    WorkspaceState.Failed => $"failed{(s.Error is { } e ? $": {e.Split('\n')[0]}" : "")}",
                    _ => "down",
                }, s.State == WorkspaceState.Up, s.State == WorkspaceState.Failed));
        HasWorkspaces = WorkspacesOfIt.Count > 0;
    }

    /// <summary>The picture needs a plan; without one, the cards say what's known.</summary>
    void ShowPicture(Plan? plan)
    {
        _model = plan;
        _selectedAppKey = null;
        _groupShown = null;
        InspectedApp = null;
        GroupChips.Clear();
        if (plan is null)
        {
            Graph = null;
            GraphLines = [];
            InspectedCard = null;
            OnPropertyChanged(nameof(ShowCards));
            OnPropertyChanged(nameof(HasGroups));
            return;
        }
        var kept = Cards.Where(c => c.InRepo is not null).GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First().Kept);
        Graph = OrchestrationGraph.Build(plan, kept);
        if (plan.Groups.Count > 0)
        {
            GroupChips.Add(new GroupChip(null, "Everything", true));
            foreach (var g in plan.Groups) GroupChips.Add(new GroupChip(g.Name, $"group {g.Name}", false));
        }
        OnPropertyChanged(nameof(HasGroups));
        OnPropertyChanged(nameof(ShowCards));
        InspectedCard = Cards.FirstOrDefault();
        DrawLines();
    }

    void DrawLines()
    {
        var group = _groupShown is { } name ? _model?.Groups.FirstOrDefault(g => g.Name == name)?.Apps.ToHashSet() : null;
        GraphLines = Graph?.Lines(_selectedAppKey, group) ?? [];
    }

    /// <summary>An app in the picture: its wires stand out, and the inspector shows its values.</summary>
    [RelayCommand]
    private void SelectApp(GraphItem? item)
    {
        if (item is null) return;
        _selectedAppKey = item.Key;
        InspectedApp = Plan?.Apps.FirstOrDefault(a => a.Title == $"{item.Owner} / {item.Name}");
        if (InspectedApp is null) InspectedCard = Cards.FirstOrDefault(c => c.Name == item.Owner) ?? InspectedCard;
        DrawLines();
    }

    /// <summary>A repo's box: the inspector shows its card.</summary>
    [RelayCommand]
    private void SelectBox(GraphBox? box)
    {
        if (box is null || box.IsOrchestration) return;
        _selectedAppKey = null;
        InspectedApp = null;
        InspectedCard = Cards.FirstOrDefault(c => c.Name == box.Owner);
        DrawLines();
    }

    [RelayCommand]
    private void ShowGroup(GroupChip? chip)
    {
        if (chip is null) return;
        _groupShown = chip.Name;
        for (var i = 0; i < GroupChips.Count; i++) GroupChips[i] = GroupChips[i] with { IsOn = GroupChips[i].Name == chip.Name };
        DrawLines();
    }

    /// <summary>The line under the header: whether a workspace can start, and if not, the one thing in the way.</summary>
    void SetStatus()
    {
        var errors = Plan?.Problems.Count(p => p.IsError) ?? 0;
        var warnings = Plan?.Problems.Count(p => !p.IsError) ?? 0;
        (Status, StatusKind) = this switch
        {
            { Selected: null } => ("", StatusKind.None),
            { IsBusy: true } => ("Checking…", StatusKind.None),
            { HasPendingTrust: true } => ("Review the code: nothing runs until you approve it.", StatusKind.Warn),
            { HasLoadProblems: true } => ($"Can't plan: {Count(LoadProblems.Count, "problem")} below.", StatusKind.Bad),
            _ when errors > 0 => ($"{Count(errors, "problem")} to fix before a workspace can start; see Values.", StatusKind.Bad),
            { Plan: not null } => ($"Ready to start{(warnings > 0 ? $", with {Count(warnings, "warning")} on Values" : "")}. Checked at {DateTime.Now:HH:mm}.", StatusKind.Good),
            _ => ("", StatusKind.None),
        };
        CanStart = StatusKind == StatusKind.Good;
        BuildChecklist(errors);
    }

    /// <summary>The steps to a first workspace, shown only while one of them is in the way.</summary>
    void BuildChecklist(int errors)
    {
        Steps.Clear();
        if (Selected is null || IsBusy)
        {
            ShowChecklist = false;
            return;
        }
        var unfilledErrors = Plan?.Problems.Count(p => p.IsError && (p.Message.Contains(UnfilledMessage, StringComparison.Ordinal)
            || p.Message.Contains(SeveralOfferMessage, StringComparison.Ordinal))) ?? 0;
        foreach (var step in OrchestrationChecklist.Steps(HasPendingTrust ? Review.Remaining : 0, LoadProblems.Count, [.. Cards], Plan is not null, errors - unfilledErrors))
            Steps.Add(step);
        ShowChecklist = OrchestrationChecklist.Blocks(Steps);
    }

    // How the planner words an import nothing answers, or several repos answer; the checklist counts those under its
    // own step.
    const string UnfilledMessage = "but no repo in the orchestration exports it";
    const string SeveralOfferMessage = "; pick one in the orchestration with orchestration.Bind(";

    [RelayCommand]
    private async Task RunStep(ChecklistStep? step)
    {
        switch (step?.Action)
        {
            case StepAction.Review: Review.OpenCommand.Execute(null); break;
            case StepAction.EditCode: Tab = OrchestrationTab.Code; break;
            case StepAction.FillNeeds when Selected is { } orchestration: FillNeeds.Open(orchestration.Path, _unfilled); break;
            case StepAction.ShowValues: Tab = OrchestrationTab.Values; break;
            case StepAction.Start: Start(); break;
        }
    }

    static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    void AskForTrust(IEnumerable<NeedsTrust> pending, Func<Task> then)
    {
        PendingTrust.Clear();
        foreach (var p in pending) PendingTrust.Add(PendingTrustItem.From(p));
        HasPendingTrust = PendingTrust.Count > 0;
        // Only an orchestration that has run can say which repos it uses, so their definitions are a second ask.
        PendingIsOrchestration = Selected is { } orchestration && PendingTrust.Any(p => string.Equals(p.Path, orchestration.DisplayPath, StringComparison.OrdinalIgnoreCase));
        _afterApproval = then;
        Review.Note = PendingIsOrchestration
            ? "The orchestration names its repos, so once it's approved sprig asks about any of their sprig.cs files you haven't approved."
            : "";
        Review.Load(PendingTrust);
    }

    [RelayCommand]
    private async Task ApproveAll()
    {
        foreach (var item in PendingTrust) _services.Trust.Approve(item.TrustPath, item.Sha256);
        PendingTrust.Clear();
        HasPendingTrust = false;
        Review.Clear();
        Refresh(Selected?.Name, replan: false);
        if (_afterApproval is { } next)
        {
            _afterApproval = null;
            await next();
        }
    }

    void ShowLoadProblems(IEnumerable<PlanProblem> problems, string orchestrationPath)
    {
        LoadProblems.Clear();
        foreach (var row in PlanView.Rows(problems, Path.GetDirectoryName(orchestrationPath)!)) LoadProblems.Add(row);
        HasLoadProblems = LoadProblems.Count > 0;
    }

    void ClearResults()
    {
        Plan = null;
        PendingTrust.Clear();
        HasPendingTrust = false;
        Review.Clear();
        LoadProblems.Clear();
        HasLoadProblems = false;
    }

    // ---- what the header and cards offer ----

    [RelayCommand]
    private void Start()
    {
        if (Selected is { } orchestration && CanStart) StartWorkspace?.Invoke(orchestration.Name);
    }

    [RelayCommand]
    private void EditCode() => Tab = OrchestrationTab.Code;

    [RelayCommand]
    private void OpenRepoById(string? id)
    {
        if (id is not null) OpenRepo?.Invoke(id);
    }

    /// <summary>A repo the orchestration names by folder: its own sprig.cs, opened here.</summary>
    [RelayCommand]
    private void EditRepoFile(RepoCard? card)
    {
        if (card is null) return;
        var file = Path.Combine(card.Folder, OrchestrationLoader.RepoDefinitionFile);
        if (!File.Exists(file)) return;
        OpenInEditor(file, $"{card.Name}: in repo", "In the repo, so saving leaves an uncommitted change there. Saving approves what you saved.");
    }

    /// <summary>Bring a repo the orchestration names by folder in through the Repos list, so where its file is kept can
    /// be switched.</summary>
    [RelayCommand]
    private async Task AddToRepos(RepoCard? card)
    {
        if (card is null || Selected is not { } orchestration) return;
        try { await AppServices.RunAsync(() => _services.Authoring.UseFromRepos(orchestration.Path, card.Path)); }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            ShowLoadProblems([new PlanProblem(ProblemSeverity.Error, e.Message, new SourceLocation(orchestration.Path, 0))], orchestration.Path);
            SetStatus();
            return;
        }
        await PlanSelectedAsync();
    }

    // ---- the code ----

    void OpenOrchestrationCode()
    {
        if (Selected is not { } orchestration || orchestration.Missing) return;
        OpenInEditor(orchestration.Path, $"{orchestration.Name}: the orchestration",
            orchestration.InSprig ? "In sprig. Saving approves what you saved, and checks it again." : "In your folder, so saving changes the file there. Saving approves what you saved, and checks it again.");
    }

    void OpenInEditor(string file, string title, string note)
    {
        if (Editor.Open(file, title, note) is { } error && Selected is { } orchestration)
        {
            ShowLoadProblems([new PlanProblem(ProblemSeverity.Error, error, new SourceLocation(file, 0))], orchestration.Path);
            SetStatus();
            return;
        }
        Tab = OrchestrationTab.Code;
    }

    async Task<(string, bool)> AfterSaveAsync()
    {
        await PlanSelectedAsync();
        var errors = Plan?.Problems.Count(p => p.IsError) ?? 0;
        if (HasPendingTrust) return ("Saved. Something it uses needs approving: see Overview.", true);
        if (HasLoadProblems) return ($"Saved, but it doesn't plan: {LoadProblems.FirstOrDefault()?.Message}", true);
        if (errors > 0) return ($"Saved. The plan has {Count(errors, "problem")}: see Values.", true);
        return ($"Saved, and it plans. {Plan?.Summary}", false);
    }

    // ---- adding and removing orchestrations ----

    [RelayCommand]
    private void OpenChoose()
    {
        IsAddOpen = false;
        IsNewOpen = false;
        IsChooseOpen = true;
    }

    [RelayCommand]
    private void CancelChoose() => IsChooseOpen = false;

    [RelayCommand]
    private void BackToChoose() => OpenChoose();

    [RelayCommand]
    private void OpenAdd()
    {
        IsChooseOpen = false;
        IsNewOpen = false;
        NewPath = "";
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
        var path = NewPath.Trim();
        if (path.Length == 0 || !File.Exists(path))
        {
            Fail("Choose an orchestration file (a .orchestration.cs) that exists.");
            return;
        }
        path = LongPath.Of(path);

        IsBusy = true;
        DefinitionResult result;
        try { result = await AppServices.RunAsync(() => _services.Definitions.Evaluate(path)); }
        finally { IsBusy = false; }

        switch (result)
        {
            case NeedsTrust pending:
                var guessed = _services.Orchestrations.Add(_services.Orchestrations.UnusedId(OrchestrationRegistry.IdFromFile(path), path), path);
                CloseAdd();
                Refresh(guessed.Id, replan: false);
                ClearResults();
                Description = "";
                ShowSummary(null, null);
                AskForTrust([pending], PlanSelectedAsync);
                SetStatus();
                return;
            case Evaluated { Model: OrchestrationModel model }:
                var entry = _services.Orchestrations.Add(_services.Orchestrations.UnusedId(model.Name, path), path);
                CloseAdd();
                Refresh(entry.Id, replan: true);
                return;
            case Evaluated:
                Fail("That file describes a repo (Repo.Describe), not an orchestration.");
                return;
            case Failed failed:
                Fail($"It didn't run: {failed.Reason}. {FirstError(failed.Output)}");
                return;
            case Rejected rejected:
                Fail(string.Join(" ", rejected.Violations.Select(v => $"Line {v.Line}: {v.Reason}.")));
                return;
        }
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

    /// <summary>An orchestration kept in sprig exists nowhere else, so removing one asks first.</summary>
    [RelayCommand]
    private void Remove(OrchestrationItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        if (item.InSprig && !ConfirmingRemove)
        {
            ConfirmingRemove = true;
            return;
        }
        ConfirmingRemove = false;
        _services.Authoring.RemoveOrchestration(item.Name);
        if (Selected == item) Selected = null;
        ClearResults();
        ShowSummary(null, null);
        Refresh(null, replan: true);
    }

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    // ---- a new orchestration kept in sprig ----

    [RelayCommand]
    private void OpenNew()
    {
        IsChooseOpen = false;
        IsAddOpen = false;
        NewName = "";
        NewRepoPath = "";
        NewRepoDirs.Clear();
        NewMessage = "";
        NewFailed = false;
        IsNewOpen = true;
    }

    [RelayCommand]
    private void CancelNew() => IsNewOpen = false;

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
        IsBusy = true;
        OrchestrationEntry entry;
        try
        {
            var dirs = NewRepoDirs.ToArray();
            var name = NewName.Trim();
            entry = await AppServices.RunAsync(() => _services.Authoring.CreateOrchestration(name, dirs));
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

    static string FirstError(string output)
        => output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal)) ?? "";
}
