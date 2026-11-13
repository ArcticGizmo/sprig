using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Logs;
using Sprig.Engine.Definitions;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Workspaces;
using Sprig.Engine.Orchestrations;

namespace Sprig.App.ViewModels;

/// <param name="HeldBy">For a main that's down, another definition's workspace that's up in the same checkout.</param>
/// <param name="Orphaned">Its repo or orchestration has gone, so nothing else would ever remove it.</param>
public sealed record WorkspaceItem(WorkspaceRecord Record, WorkspaceRecord? HeldBy = null, bool Orphaned = false)
{
    /// <summary>A slot by its folder's name, as git and the file system show it; main by its ID, the name the slots are
    /// numbered from; another one in place by ID and name.</summary>
    public string Title => Display(Record);
    public string Branch => BranchOf(Record);
    public string State => Record.IsFree ? "free" : Record.State.ToString().ToLowerInvariant();
    public bool IsUp => Record.State == WorkspaceState.Up;
    public bool IsFailed => Record.State == WorkspaceState.Failed;
    public bool IsFree => Record.IsFree;
    public bool IsMain => Record.IsMain;

    /// <summary>A slot with a branch checked out, which can be freed for the next one.</summary>
    public bool CanFree => !Record.InPlace && !Record.IsFree;

    /// <summary>Main is always there and runs with the definitions' own values, while its repo or orchestration is.</summary>
    public bool CanRemove => !Record.IsMain || Orphaned;
    public bool HasSettings => !Record.IsMain;

    /// <summary>It has infra whose data can go. A main that has never come up has none yet.</summary>
    public bool CanEmptyData => Record.Compose.Count > 0;

    public bool IsHeld => HeldBy is not null;
    public string HeldLine => HeldBy is { } other ? $"{other.SlotName} is up in this checkout" : "";

    public static string Display(WorkspaceRecord record)
        => record.IsMain ? record.Orchestration : record.InPlace ? $"{record.Orchestration} / {record.Name}" : record.SlotName;

    public static string BranchOf(WorkspaceRecord record)
        => record.IsMain ? record.CheckoutBranch is { } on ? $"main checkout · {on}" : "main checkout"
            : record.InPlace ? "repos in place" : record.Branch ?? "free";
}

/// <summary>One app or job of the selected workspace, with what the host says about it.</summary>
/// <param name="key">The app as the host names it, <c>repo/app</c>.</param>
/// <param name="name">For messages: <c>repo / app</c>.</param>
/// <param name="repo">The repo's name.</param>
/// <param name="app">The app's own name.</param>
/// <param name="port">Its main port, or "" when it has none.</param>
public sealed partial class WorkspaceAppRow(string key, string name, string folder, string url, string values, bool job,
    string repo = "", string app = "", string port = "") : ObservableObject
{
    public string Key => key;
    public string Name => name;
    public string Repo => repo;
    public string App => app.Length > 0 ? app : key[(key.IndexOf('/') + 1)..];
    public string Port => port.Length > 0 ? port : job ? "job" : "";
    public string Folder => folder;
    public string Url => url;
    public string Values => values;
    public bool IsJob => job;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLine), nameof(IsReady), nameof(IsStoppedOrDone), nameof(IsFinished), nameof(IsSpinning))]
    private string _state = "not running";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLine))]
    private string _detail = "";

    [ObservableProperty] private bool _isGood;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblem), nameof(IsStoppedOrDone), nameof(IsSpinning))]
    private bool _isBad;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpinning))]
    private bool _isBusy;

    /// <summary>Held back until what it depends on is ready, rather than doing anything itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpinning))]
    private bool _isWaiting;

    /// <summary>Building, starting or stopping: its dot goes round, as its node does in the graph, while one waiting
    /// holds still.</summary>
    public bool IsSpinning => IsBusy && !IsWaiting && !IsProblem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStoppedOrDone))]
    private bool _isAlive;

    [ObservableProperty] private int _restarts;

    /// <summary>Its log is the one showing.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Ctrl- or Shift-clicked, so one menu acts on it and the others marked with it.</summary>
    [ObservableProperty] private bool _isMarked;
    [ObservableProperty] private bool _isPinned;

    public bool HasDetail => Detail.Length > 0;
    public string StateLine => Detail.Length == 0 ? State
        : Detail.StartsWith(State, StringComparison.OrdinalIgnoreCase) ? Detail : $"{State} · {Detail}";
    public bool IsReady => State == "ready";
    public bool IsFinished => State == "finished";

    /// <summary>Exited, failed, or going round restarts: worth a look.</summary>
    public bool IsProblem => IsBad || State == "restarting";

    public bool IsStoppedOrDone => !IsAlive && !IsBad;

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));

    partial void OnStateChanged(string value) => OnPropertyChanged(nameof(IsProblem));

    public void MarkStopping()
    {
        (State, Detail, IsGood, IsBad, IsBusy, IsWaiting) = ("stopping", "", false, false, true, false);
    }

    /// <summary>Show <paramref name="status"/>; null when the host has no runner for this app.</summary>
    public void Apply(RunnerStatus? status)
    {
        var restarts = status is { Restarts: > 0 } ? $"restarted {status.Restarts}×" : null;
        (State, Detail) = status?.State switch
        {
            null => ("not running", ""),
            RunnerState.Building => ("building", ""),
            RunnerState.Waiting => ("waiting", "for what it depends on"),
            RunnerState.Starting => ("starting", status.Error ?? ""),
            RunnerState.Ready => ("ready", restarts ?? ""),
            RunnerState.Running => ("running", ""),
            RunnerState.Completed => ("finished", restarts is null ? "" : $"after {restarts}"),
            RunnerState.Restarting => ("restarting", status.Error ?? ""),
            RunnerState.Exited => ("exited", status.Error ?? $"code {status.Exit}"),
            RunnerState.Failed => ("failed", status.Error ?? ""),
            RunnerState.Stopping => ("stopping", ""),
            RunnerState.Stopped => ("stopped", status.Error ?? ""),
            _ => (status.State.ToString().ToLowerInvariant(), ""),
        };
        IsGood = status?.State is RunnerState.Ready or RunnerState.Completed;
        IsBad = status?.State is RunnerState.Failed or RunnerState.Exited;
        IsBusy = status?.State is RunnerState.Building or RunnerState.Waiting or RunnerState.Starting or RunnerState.Running
            or RunnerState.Restarting or RunnerState.Stopping;
        IsWaiting = status?.State == RunnerState.Waiting;
        IsAlive = status is not null && (status.State.IsAlive() || status.State == RunnerState.Stopping);
        Restarts = status?.Restarts ?? 0;
    }
}

public sealed record WorkspaceInfraRow(string Name, string Ports);

/// <summary>What Start starts, like a launch profile: every app (<see cref="Group"/> null) or one group.</summary>
/// <param name="Apps">What it starts, in words.</param>
public sealed record StartChoice(string? Group, string Label, string Apps);

/// <summary>
/// The Workspaces page: every workspace with its state, the folders, urls and ports of the selected one, and a form to make
/// a new one. Up, down and remove run in the background and report to a progress window the view opens. The selected
/// workspace's apps can be started and stopped in the sprig host, each showing its state, with one app's log beside them.
/// </summary>
public partial class WorkspacesViewModel : PageViewModel
{
    readonly AppServices _services;
    Func<Task>? _afterApproval;

    public override string Title => "Workspaces";
    public override string? NavSubtitle => "running copies";

    public ObservableCollection<WorkspaceItem> Workspaces { get; } = [];
    public ObservableCollection<string> OrchestrationNames { get; } = [];
    public ObservableCollection<PendingTrustItem> PendingTrust { get; } = [];

    /// <summary>The files waiting for approval, read and approved one at a time.</summary>
    public CodeReviewViewModel Review { get; }

    [ObservableProperty] private WorkspaceItem? _selected;
    [ObservableProperty] private IReadOnlyList<WorkspaceAppRow> _apps = [];
    [ObservableProperty] private IReadOnlyList<WorkspaceInfraRow> _infra = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings), nameof(WarningsBadge))]
    private IReadOnlyList<ProblemRow> _warnings = [];

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrustInModal), nameof(TrustOnPage), nameof(MessageInModal), nameof(MessageOnPage))]
    private bool _isAddOpen;
    [ObservableProperty] private string? _newOrchestration;
    [ObservableProperty] private string _newBranch = "";
    [ObservableProperty] private string _newFrom = "";

    /// <summary>The slot asked for, when the form was opened from a free one.</summary>
    int? _newSlot;

    /// <summary>Which slot the branch would go in, in words.</summary>
    [ObservableProperty] private string _newSlotLine = "";
    [ObservableProperty] private bool _newSlotBad;

    /// <summary>The branch would reuse a free slot, so there's data to keep or empty.</summary>
    [ObservableProperty] private bool _newReusesSlot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewKeepData))]
    private bool _newEmptyData;

    public bool NewKeepData
    {
        get => !NewEmptyData;
        set => NewEmptyData = !value;
    }

    /// <summary>The slot the branch would reuse; its kept values fill the form's settings.</summary>
    WorkspaceRecord? _newSlotRecord;

    /// <summary>The slot the branch would go in is broken, so the form asks whether to fix it or use the next one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewSlotBroken), nameof(NewFixSlotLabel), nameof(NewNextSlotLabel))]
    private BrokenSlot? _newBroken;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewUseNextSlot))]
    private bool _newFixSlot;

    public bool NewUseNextSlot
    {
        get => !NewFixSlot;
        set => NewFixSlot = !value;
    }

    public bool NewSlotBroken => NewBroken is not null;
    public string NewFixSlotLabel => NewBroken is { } b ? $"Fix {b.SlotName}: delete what's in the way and make it again" : "";
    public string NewNextSlotLabel => NewBroken is { } b ? $"Use {b.NextName} instead" : "";

    /// <summary>An orchestration's workspaces start from the branch its last one started from.</summary>
    partial void OnNewOrchestrationChanged(string? value)
    {
        if (value is null) return;
        NewFrom = _services.Ui.WorkspaceChoices(value).From ?? "";
        if (!IsAddOpen) return;
        NewBranchChoices = [];
        FillSlotChoices(value);
        CheckSlotSoon(immediately: true);
        _ = LoadNewSettingsAsync(value);
    }

    partial void OnNewBranchChanged(string value)
    {
        if (IsAddOpen) CheckSlotSoon();
    }

    static readonly TimeSpan SlotCheckDelay = TimeSpan.FromMilliseconds(300);
    CancellationTokenSource? _slotCheck;

    /// <summary>The definitions of the form's orchestration or repo, once read, so checking a slot can look at its
    /// folders. Kept per ID, since reading them can take a while.</summary>
    LoadedOrchestration? _newLoaded;
    string? _newLoadedFor;

    /// <summary>Check the slot once typing pauses, off the UI thread. The line keeps saying what it said until the
    /// answer arrives, so the form doesn't flicker on every keystroke.</summary>
    void CheckSlotSoon(bool immediately = false)
    {
        _slotCheck?.Cancel();
        _slotCheck = new CancellationTokenSource();
        _ = CheckSlotAsync(immediately ? TimeSpan.Zero : SlotCheckDelay, _slotCheck.Token);
    }

    async Task CheckSlotAsync(TimeSpan delay, CancellationToken cancel)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancel);
        }
        catch (OperationCanceledException) { return; }
        if (NewOrchestration is not { } id)
        {
            ShowSlot("", bad: false, null);
            return;
        }
        if (NewMode == NewCheckout.NoBranch)
        {
            var at = NewFrom.Trim() is { Length: > 0 } f ? f : null;
            var (freeLine, freeBad) = await AppServices.RunAsync(() => FreeSlotPreview(id, at));
            if (cancel.IsCancellationRequested) return;
            ShowSlot(freeLine, freeBad, null);
            return;
        }
        var (branch, _) = NewTarget();
        var slot = _newSlot;
        var loaded = _newLoadedFor == id ? _newLoaded : null;
        var makes = NewMode == NewCheckout.NewBranch;
        var (line, bad, record, broken) = await AppServices.RunAsync(() => SlotPreview(id, branch, slot, loaded, makes));
        if (cancel.IsCancellationRequested) return;

        var before = _newSlotRecord?.Key;
        ShowSlot(line, bad, record, broken);
        if (record?.Key != before) _ = LoadNewSettingsAsync(id);
    }

    /// <summary>Which slot the branch would go in (the one it's in already, a free one to reuse, or a new one), or what
    /// stands in the way of it: a broken slot (which the form then asks about), or the branch checked out somewhere
    /// else.</summary>
    /// <param name="makes">The branch is to be made, so one of that name already there stands in the way.</param>
    (string Line, bool Bad, WorkspaceRecord? Record, BrokenSlot? Broken) SlotPreview(string id, string branch, int? slot, LoadedOrchestration? loaded,
        bool makes)
    {
        try
        {
            var (name, record) = _services.Workspaces.SlotFor(id, branch, slot);
            if (makes && branch.Length > 0 && loaded is not null && _services.Workspaces.BranchTaken(loaded, branch) is { } taken)
                return ($"{char.ToUpperInvariant(taken[0])}{taken[1..]}.", true, record, null);
            if (loaded is not null && _services.Workspaces.BrokenSlotFor(loaded, branch, slot) is { } broken)
                return ($"{broken.Why}.", true, record, broken);
            if (branch.Length > 0 && loaded is not null && _services.Workspaces.CheckOutBlocker(loaded, branch, slot) is { } blocked)
                return (blocked, true, record, null);
            return (record switch
            {
                null => $"Goes in {id}--{name}, a new slot.",
                { IsFree: true } => $"Reuses {record.SlotName}, which is free: its folders, installed packages, ports and values carry over.",
                _ => $"'{branch}' is already in {record.SlotName}, so it comes up there again.",
            }, false, record, null);
        }
        catch (WorkspaceException e)
        {
            return (e.Message, true, null, null);
        }
    }

    void ShowSlot(string line, bool bad, WorkspaceRecord? record, BrokenSlot? broken = null)
    {
        _newSlotRecord = record;
        NewReusesSlot = record?.IsFree == true;
        NewSlotLine = line;
        NewSlotBad = bad;
        // A different broken slot asks afresh, starting from the choice that deletes nothing.
        if (broken?.SlotName != NewBroken?.SlotName) NewFixSlot = false;
        NewBroken = broken;
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageInModal), nameof(MessageOnPage))]
    private string _formMessage = "";
    [ObservableProperty] private bool _formFailed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrustInModal), nameof(TrustOnPage))]
    private bool _hasPendingTrust;

    // Approvals and form messages belong to whichever is in front: the new-workspace modal, or the selected workspace.
    public bool TrustInModal => HasPendingTrust && IsAddOpen;
    public bool TrustOnPage => HasPendingTrust && !IsAddOpen;
    public bool MessageInModal => FormMessage.Length > 0 && IsAddOpen;
    public bool MessageOnPage => FormMessage.Length > 0 && !IsAddOpen;

    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _confirmingRemove;
    [ObservableProperty] private bool _confirmingEmptyData;
    [ObservableProperty] private bool _deleteBranch;

    /// <summary>The view opens a progress window for each operation it's handed.</summary>
    public event Action<OperationProgressViewModel>? ProgressStarted;

    public bool IsEmpty => Workspaces.Count == 0;
    public bool HasSelection => Selected is not null;
    public bool HasOrchestrations => OrchestrationNames.Count > 0;
    public bool CanDeleteBranch => Selected?.Record is { InPlace: false, Branch: not null };

    public WorkspacesViewModel(AppServices services)
    {
        _services = services;
        Review = new CodeReviewViewModel(services.Trust) { AllApproved = ApproveAll };
        _services.HostStatus.Changed += ApplyRunners;
        OrchestrationNames.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasOrchestrations));
        _lastRunners = _services.HostStatus.Runners;
        DetailsOpen = _services.Ui.DetailsOpen;
        Logs.ShowTimes = _services.Ui.LogTimes;
        Logs.Wrap = _services.Ui.LogWrap;
        Refresh();
    }

    protected override void OnActivated() => Refresh();

    // Rebuilding the list reloads the selected workspace's apps and log, so it's left alone unless something changed.
    protected override void OnWindowFocused()
    {
        if (IsAddOpen || IsSettingsOpen || ConfirmingRemove || ConfirmingBulkRemove || ConfirmingTakeOver || IsWorking || HasPendingTrust) return;
        var records = _services.Workspaces.List().Select(r => (r.Key, r.State, r.UpdatedAt));
        var names = _services.Orchestrations.List().Select(e => e.Id).Concat(_services.Stored.Repos().Select(r => r.Id));
        if (!records.SequenceEqual(Workspaces.Where(w => !IsUnmade(w.Record)).Select(w => (w.Record.Key, w.Record.State, w.Record.UpdatedAt)))
            || !names.SequenceEqual(OrchestrationNames))
            Refresh();
    }

    /// <summary>A main that has never come up, listed so it can be started: <see cref="WorkspaceService.MainOf"/>.</summary>
    internal static bool IsUnmade(WorkspaceRecord record) => record.CreatedAt == default;

    /// <summary>Raised after the list of workspaces is read again, so the nav can follow.</summary>
    public event Action? WorkspacesChanged;

    [RelayCommand]
    private void Refresh()
    {
        var keep = Selected?.Record.Key;
        var orchestration = NewOrchestration;
        OrchestrationNames.Clear();
        foreach (var entry in _services.Orchestrations.List()) OrchestrationNames.Add(entry.Id);
        // A repo can run on its own; IDs are unique across orchestrations and repos, so the names don't collide.
        foreach (var repo in _services.Stored.Repos()) OrchestrationNames.Add(repo.Id);

        Workspaces.Clear();
        var stored = _services.Workspaces.List();
        // Every repo and orchestration has a main, listed before it has ever come up.
        var mains = OrchestrationNames
            .Where(id => !stored.Any(r => r.Orchestration == id && r.IsMain))
            .Select(id => _services.Orchestrations.Resolve(id) is { } path ? _services.Workspaces.MainOf(id, path) : null)
            .OfType<WorkspaceRecord>();
        foreach (var record in stored.Concat(mains))
            Workspaces.Add(new WorkspaceItem(record, record.IsMain && record.State != WorkspaceState.Up ? _services.Workspaces.HolderOf(record) : null,
                Orphaned: record.IsMain && !OrchestrationNames.Contains(record.Orchestration)));
        NavCount = Workspaces.Count(s => s.IsUp);

        NewOrchestration = OrchestrationNames.Contains(orchestration ?? "") ? orchestration : OrchestrationNames.FirstOrDefault();

        OnPropertyChanged(nameof(IsEmpty));
        Selected = Workspaces.FirstOrDefault(s => s.Record.Key == keep);
        BuildOverview();
        if (Selected is null && IsActive) _ = LoadOverviewStatusAsync();
        WorkspacesChanged?.Invoke();
    }

    /// <summary>Show one workspace, by its key; null shows them all.</summary>
    public void Show(string? key)
    {
        Selected = key is null ? null : Workspaces.FirstOrDefault(s => s.Record.Key == key);
        if (Selected is null) _ = LoadOverviewStatusAsync();
    }

    /// <summary>The header offers the one that applies: take an up workspace down, or bring any other up. A free slot
    /// has nothing to run until a branch is checked out in it.</summary>
    public bool ShowTakeDown => Selected?.IsUp == true;
    public bool ShowBringUp => Selected is { IsUp: false, IsFree: false };
    public bool ShowRunControls => Selected?.IsFree != true;

    partial void OnSelectedChanged(WorkspaceItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDeleteBranch));
        OnPropertyChanged(nameof(ShowTakeDown));
        OnPropertyChanged(nameof(ShowBringUp));
        OnPropertyChanged(nameof(ShowRunControls));
        OnPropertyChanged(nameof(ShowStartChoices));
        ConfirmingRemove = false;
        ConfirmingEmptyData = false;
        CancelTakeOver();
        DeleteBranch = false;
        ShowDetails(value?.Record);
    }

    void ShowDetails(WorkspaceRecord? record)
    {
        var plan = record is null ? null : _services.Workspaces.LastPlan(record.Orchestration, record.Name);
        ShowPlan(record, plan, preview: false);
        if (record is { IsFree: false } && plan is null) _ = PreviewAsync(record);
    }

    /// <summary>Plan a workspace that has never come up, so its page shows what it would run rather than nothing. Loading
    /// its definitions never asks for approval here: one that needs it says so, and bringing it up asks.</summary>
    async Task PreviewAsync(WorkspaceRecord record)
    {
        string? why = null;
        (Plan Plan, WorkspaceRecord Record)? preview = null;
        try
        {
            preview = await AppServices.RunAsync(() =>
            {
                var loaded = _services.Loader.Load(record.OrchestrationPath, _services.Workspaces.Worktrees(record.OrchestrationPath, record.Name));
                if (loaded.Untrusted.Count > 0) { why = "Its definitions need approving before sprig can say what it runs. Bringing it up asks."; return null; }
                if (loaded.Orchestration is null || loaded.Problems.Count > 0)
                {
                    why = "It can't be planned: " + string.Join("; ", loaded.Problems.Take(2).Select(p => p.Message));
                    return null;
                }
                return ((Plan, WorkspaceRecord)?)_services.Workspaces.Preview(loaded, record);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WorkspaceException)
        {
            why = $"Its definitions couldn't be read: {e.Message}";
        }
        // Another workspace picked meanwhile, or this one came up and has a plan of its own now.
        if (Selected?.Record.Key != record.Key || _services.Workspaces.LastPlan(record.Orchestration, record.Name) is not null) return;
        if (preview is { } shown) ShowPlan(shown.Record, shown.Plan, preview: true);
        else if (why is not null) SayApps(why, bad: true);
    }

    /// <param name="preview">Planned for showing only (<see cref="WorkspaceService.Preview"/>), since it has never come up.</param>
    void ShowPlan(WorkspaceRecord? record, Plan? plan, bool preview)
    {
        _plan = plan;
        _planRecord = plan is null ? null : record;
        FillStartChoices(record, plan);
        WorkspaceLinks = plan?.Links ?? [];
        AppsMessage = "";
        if (preview) SayApps("Not up yet: these are the apps its definitions name. The ports in their values are only examples until it comes up and is given its own.", bad: false);
        if (record is null || plan is null)
        {
            Apps = [];
            Infra = [];
            Warnings = [];
            LoadWorkspace(null, null);
            return;
        }

        // A plan newer than the record (a repo added since the workspace last came up) names repos the record hasn't got;
        // those apps wait for the next bring-up rather than breaking the page.
        Apps = plan.Apps.Where(app => record.Repos.Any(r => r.RepoPath == app.RepoPath)).Select(app =>
        {
            var repo = record.Repos.First(r => r.RepoPath == app.RepoPath);
            var dir = Path.GetFullPath(Path.Combine(repo.Dir, app.Path));
            var port = plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name)?.Port;
            var files = app.ValueFiles();
            return new WorkspaceAppRow(app.Key, $"{repo.Name} / {app.Name}", dir.Replace('\\', '/'), port is null ? "" : $"http://localhost:{port}",
                files.Count > 0 ? string.Join(", ", files) : "none", app.Job,
                repo.Name, app.Name, port?.ToString() ?? "");
        }).ToArray();
        ApplyRunners(_lastRunners);
        LoadWorkspace(record, plan);

        Infra = plan.Containers.Select(c => new WorkspaceInfraRow($"{c.Owner} / {c.Name}", string.Join(", ", c.Ports)))
            .Concat(plan.Compose.SelectMany(c => c.Services.Select(s => new WorkspaceInfraRow($"{c.Owner} / {s.Name}", string.Join(", ", s.Mappings)))))
            .ToArray();

        Warnings = PlanView.Rows(plan.Problems.Where(p => p.Severity == ProblemSeverity.Warning), Path.GetDirectoryName(record.OrchestrationPath)!).ToArray();
    }

    // ---- running the selected workspace's apps ----

    IReadOnlyList<RunnerStatus>? _lastRunners;
    Plan? _plan;

    /// <summary>The record <see cref="_plan"/> was shown for: a preview's carries the repos a main gets once it comes up.</summary>
    WorkspaceRecord? _planRecord;
    bool _polling;

    /// <summary>The selected workspace's logs: every app interleaved, or one app's.</summary>
    public WorkspaceLogPane Logs { get; } = new();

    /// <summary>The orchestration's quick links, at this workspace's ports.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceLinks))]
    private IReadOnlyList<PlannedLink> _workspaceLinks = [];

    public bool HasWorkspaceLinks => WorkspaceLinks.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsMessage))]
    private string _appsMessage = "";

    [ObservableProperty] private bool _appsMessageIsBad;

    public bool HasAppsMessage => AppsMessage.Length > 0;

    /// <summary>Show what the host says about each app of the selected workspace; null when no host is running.</summary>
    public void ApplyRunners(IReadOnlyList<RunnerStatus>? runners)
    {
        _lastRunners = runners;
        var key = Selected?.Record.Key;
        foreach (var row in Apps)
            row.Apply(runners?.FirstOrDefault(r => r.Workspace == key && r.Name == row.Key));
        StatesChanged();
        RefreshGraph();
        if (Selected is null) CountOverviewApps();
    }

    /// <summary>Read the logs, and every few seconds the containers' states while the graph or a container's log shows.
    /// The view calls this every second while the page shows; the app-wide <see cref="HostStatus"/> poll brings the
    /// apps' states.</summary>
    public async Task PollAsync()
    {
        if (_polling || Selected is null) return;
        _polling = true;
        try
        {
            await Logs.PollAsync();
            await PollInfraAsync();
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Ask the host now rather than at the next tick, after something here changed what's running.</summary>
    async Task RefreshRunnersAsync()
    {
        await _services.HostStatus.PollAsync();
        await PollAsync();
    }

    /// <summary>What Start can start: every app, then each group the definitions name. Empty until the workspace has a
    /// plan.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartChoices))]
    private IReadOnlyList<StartChoice> _startChoices = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartLabel), nameof(StartTip))]
    private StartChoice? _startChoice;

    bool _fillingStartChoices;

    public bool ShowStartChoices => ShowRunControls && StartChoices.Count > 1;
    public string StartLabel => StartChoice?.Group is { } group ? $"Start {group}" : "Start all";
    public string StartTip => StartChoice?.Group is null
        ? "Every app, after what each depends on; a workspace that's down is brought up first"
        : $"{StartChoice.Apps}, after what each depends on; a workspace that's down is brought up first";

    /// <summary>The choices for <paramref name="plan"/>'s groups, with the one last picked for the orchestration (or repo)
    /// chosen, so every workspace of it starts the same slice.</summary>
    void FillStartChoices(WorkspaceRecord? record, Plan? plan)
    {
        _fillingStartChoices = true;
        try
        {
            StartChoices = plan is null || record is null ? []
                : [new StartChoice(null, "Every app", $"All {plan.Apps.Count} apps"),
                   .. plan.Groups.Select(g => new StartChoice(g.Name, g.Name, string.Join(", ", g.Apps.Select(a => a[(a.IndexOf('/') + 1)..]))))];
            var remembered = record is null ? null : _services.Ui.StartGroup(record.Orchestration);
            StartChoice = StartChoices.FirstOrDefault(c => c.Group == remembered) ?? StartChoices.FirstOrDefault();
        }
        finally
        {
            _fillingStartChoices = false;
        }
    }

    partial void OnStartChoiceChanged(StartChoice? value)
    {
        if (!_fillingStartChoices && value is not null && Selected?.Record is { } record) _services.Ui.SetStartGroup(record.Orchestration, value.Group);
    }

    [RelayCommand]
    private void PickStartChoice(StartChoice choice) => StartChoice = choice;

    /// <summary>Picks <paramref name="choice"/> and starts it in one go, from the ▶ beside it in the choices.</summary>
    [RelayCommand]
    private async Task StartChoiceNow(StartChoice choice)
    {
        StartChoice = choice;
        await StartAsync(null);
    }

    [RelayCommand]
    private async Task StartApps() => await StartAsync(null);

    [RelayCommand]
    private async Task StartApp(WorkspaceAppRow row) => await StartAsync([row.Key], open: true);

    /// <summary>What Start's choice picks (every app, or a group), leaving alone what's already going; or the apps
    /// <paramref name="keys"/> names, started afresh, after what they depend on. Starting means everything they need too:
    /// a workspace that isn't up is brought up first.</summary>
    /// <param name="open">Show the one app's log once it's starting.</param>
    async Task StartAsync(IReadOnlyList<string>? keys, bool open = false)
    {
        if (Selected?.Record is not { } record || keys is { Count: 0 }) return;
        // Taken now: bringing the workspace up reads it afresh, choices and all.
        var group = keys is null ? StartChoice?.Group : null;
        var starting = new Starting(group, keys);
        // Brought up here, its apps build while the infra comes up, and starting them releases those builds.
        var prebuilt = record.State != WorkspaceState.Up;
        if (prebuilt)
        {
            if (!await UpAsync(retry: () => StartAsync(keys, open), starting, build: starting)) return;
            if (Selected?.Record is not { State: WorkspaceState.Up } up) return;
            record = up;
        }
        if (_services.Workspaces.LastPlan(record.Orchestration, record.Name) is not { } plan) return;
        try
        {
            var names = starting.AppsIn(plan);
            // Up for one group, only its infra is running; what this adds starts first, so its apps find it there.
            var missing = await AppServices.RunAsync(() => _services.Workspaces.MissingInfra(record, plan, names));
            if (missing.Count > 0)
            {
                var title = WorkspaceItem.Display(record);
                if (!await RunAsync($"Starting what {(group is not null ? $"the {group} group" : keys is [var only] ? only : "the apps")} uses in {title}",
                        (progress, cancellation) => _services.Workspaces.StartInfra(missing, progress, cancellation),
                        $"{string.Join(", ", missing.Select(t => t.Key))} {(missing.Count == 1 ? "is" : "are")} running.")) return;
            }
            await AppServices.RunAsync(() =>
            {
                var specs = RunnerSpecs.For(record, plan, names);
                var answer = _services.Host.Send(new HostRequest("start", specs, Restart: prebuilt ? [] : keys ?? []));
                if (!answer.Ok) throw new InvalidOperationException(answer.Error ?? "the host wouldn't start the apps");
            });
            // Looked up after, since bringing the workspace up reads it afresh and its rows are new ones.
            var one = keys is [var key] ? Apps.FirstOrDefault(a => a.Key == key) : null;
            SayApps(one is not null ? $"Starting {one.Name}."
                : keys is not null ? $"Starting {keys.Count} apps, each after what it depends on."
                : group is not null ? $"Starting the {group} group, each app after what it depends on."
                : "Starting the apps, each after what it depends on.", bad: false);
            if (open && one is not null) OpenApp(one);
            await RefreshRunnersAsync();
        }
        catch (Exception e)
        {
            SayApps(e.Message, bad: true);
        }
    }

    [RelayCommand]
    private Task RestartRunning() => RestartRunningAsync(jobs: false);

    [RelayCommand]
    private Task RestartRunningWithJobs() => RestartRunningAsync(jobs: true);

    /// <summary>Every app that's going, built again and after what it depends on, so a change in the worktree takes
    /// effect; with <paramref name="jobs"/>, the jobs they depend on run again first. Stopped apps stay stopped.</summary>
    async Task RestartRunningAsync(bool jobs)
    {
        if (Selected?.Record is not { } record || _services.Workspaces.LastPlan(record.Orchestration, record.Name) is not { } plan) return;
        try
        {
            var count = await AppServices.RunAsync(() =>
            {
                var runners = _services.Host.TrySend(new HostRequest("list"))?.Runners ?? [];
                var (specs, restart) = RunnerSpecs.RestartRunning(record, plan, runners, jobs: jobs);
                if (specs.Count == 0) return 0;
                var answer = _services.Host.Send(new HostRequest("start", specs, Restart: restart));
                if (!answer.Ok) throw new InvalidOperationException(answer.Error ?? "the host wouldn't restart the apps");
                return restart.Count(name => specs.Any(s => s.Name == name && !s.Job));
            });
            SayApps(count == 0 ? "Nothing is running to restart."
                : $"Restarting {(count == 1 ? "1 app" : $"{count} apps")}{(jobs ? " after their jobs run again" : "")}, each built again and after what it depends on.",
                bad: false);
            await RefreshRunnersAsync();
        }
        catch (Exception e)
        {
            SayApps(e.Message, bad: true);
        }
    }

    [RelayCommand]
    private async Task StopApps() => await StopAsync(null);

    [RelayCommand]
    private async Task StopApp(WorkspaceAppRow row) => await StopAsync([row]);

    /// <param name="rows">Null for every app in the workspace.</param>
    async Task StopAsync(IReadOnlyList<WorkspaceAppRow>? rows)
    {
        if (Selected?.Record is not { } record || rows is { Count: 0 }) return;
        var one = rows is [var only] ? only : null;
        SayApps(rows is null ? "Stopping the apps…" : one is not null ? $"Stopping {one.Name}…" : $"Stopping {rows.Count} apps…", bad: false);
        // Shown at once; the stop itself can take up to the grace period.
        foreach (var r in Apps.Where(a => (rows is null || rows.Contains(a)) && a.IsAlive)) r.MarkStopping();
        var keys = rows?.Select(r => r.Key).ToArray();
        var stopped = await AppServices.RunAsync(() => keys is null
            ? _services.Host.StopAndWait(record.Key)
            : keys.AsParallel().SelectMany(key => _services.Host.StopAndWait(record.Key, key)).ToArray());
        var forced = stopped.Where(s => s.Error is not null).Select(s => $"{s.Name}: {s.Error}").ToArray();
        SayApps(forced.Length > 0 ? string.Join("; ", forced)
            : stopped.Count == 0 ? "Nothing was running."
            : rows is null ? "The apps have stopped. The infra is still up."
            : one is not null ? $"{one.Name} has stopped." : $"{stopped.Count} apps have stopped.",
            bad: forced.Length > 0);
        await RefreshRunnersAsync();
    }

    void SayApps(string message, bool bad)
    {
        AppsMessage = message;
        AppsMessageIsBad = bad;
    }

    // ---- making and changing workspaces ----

    [RelayCommand]
    private void OpenAdd()
    {
        _newSlot = null;
        OpenForm();
    }

    /// <summary>The new-workspace form for the selected free slot, so its branch goes there rather than in the lowest
    /// free one.</summary>
    [RelayCommand]
    private void CheckOutHere()
    {
        if (Selected?.Record is not { IsFree: true } record) return;
        _newSlot = int.Parse(record.Name, System.Globalization.CultureInfo.InvariantCulture);
        NewBranch = "";
        if (NewOrchestration != record.Orchestration) NewOrchestration = record.Orchestration;
        OpenForm();
    }

    void OpenForm()
    {
        FormMessage = "";
        FormFailed = false;
        PendingTrust.Clear();
        HasPendingTrust = false;
        NewEmptyData = false;
        NewBroken = null;
        NewFixSlot = false;
        _newLoaded = null;
        _newLoadedFor = null;
        NewBranchChoices = [];
        FillSlotChoices(NewOrchestration);
        IsAddOpen = true;
        CheckSlotSoon(immediately: true);
        _ = LoadNewSettingsAsync(NewOrchestration);
    }

    [RelayCommand]
    private void CancelAdd()
    {
        _slotCheck?.Cancel();
        IsAddOpen = false;
        FormMessage = "";
        PendingTrust.Clear();
        HasPendingTrust = false;
        _afterApproval = null;
    }

    [RelayCommand]
    private async Task Create()
    {
        FormMessage = "";
        FormFailed = false;
        if (NewOrchestration is not { } name || _services.Orchestrations.Resolve(name) is not { } path)
        {
            Fail("Choose an orchestration or a repo. Add one on the Orchestrations or Repos page first.");
            return;
        }

        if (NewMode == NewCheckout.NoBranch)
        {
            await CreateFreeSlotAsync(name, path);
            return;
        }
        // The app checks branches out in slots of worktrees, which keep their ports; running in place is left to sprig up.
        var (branch, track) = NewTarget();
        if (branch.Length == 0)
        {
            Fail(NewMode == NewCheckout.NewBranch ? "Give the new branch a name." : "Pick the branch to check out.");
            return;
        }
        var loaded = await LoadAsync(path, Create);
        if (loaded is null) return;
        _newLoaded = loaded;
        _newLoadedFor = name;
        // Checked again, since the folders may have changed since the line under the branch last looked. Said there, with
        // the form still open, rather than partway through in the progress window.
        var newSlot = _newSlot;
        _slotCheck?.Cancel();
        BrokenSlot? broken;
        try { broken = await AppServices.RunAsync(() => _services.Workspaces.BrokenSlotFor(loaded, branch, newSlot)); }
        catch (WorkspaceException e)
        {
            Fail(e.Message);
            return;
        }
        if (broken is not null && broken.SlotName != NewBroken?.SlotName)
        {
            // Newly broken since the form last looked: ask before doing either.
            ShowSlot($"{broken.Why}.", bad: true, _newSlotRecord, broken);
            return;
        }
        var fixSlot = broken is not null && NewFixSlot;
        if (broken is not null && !fixSlot) newSlot = int.Parse(broken.Next, System.Globalization.CultureInfo.InvariantCulture);
        if (await AppServices.RunAsync(() => _services.Workspaces.CheckOutBlocker(loaded, branch, newSlot, fixSlot)) is { } blocked)
        {
            ShowSlot(blocked, bad: true, _newSlotRecord, broken);
            return;
        }
        string slot;
        try { slot = _services.Workspaces.SlotFor(name, branch, newSlot).Slot; }
        catch (WorkspaceException e)
        {
            Fail(e.Message);
            return;
        }
        if (NewValues(loaded) is not { } values) return;
        // A box emptied of a value the slot kept clears it, so the default applies again.
        var unset = NewSettings.Where(s => s.Value.Length == 0 && s.Kept is not null).Select(s => s.Name).ToArray();

        var from = NewFrom.Trim() is { Length: > 0 } f ? f : null;
        _services.Ui.SetWorkspaceChoices(name, new WorkspaceOptions(From: from));
        var makes = NewMode == NewCheckout.NewBranch;
        // An existing branch that some of an orchestration's repos don't have is made in those from their default branch.
        var options = new CheckOutOptions(newSlot, makes ? from : null, NewEmptyData, fixSlot, NewBranch: makes, Track: track);
        var slotName = $"{loaded.Id}--{slot}";

        // The progress window takes over from here, and reports any failure.
        IsAddOpen = false;
        var ok = await RunAsync($"Checking out {branch} in {slotName}",
            (progress, cancellation) => _services.Workspaces.CheckOut(loaded, branch, options, progress, cancellation, values: values, unset: unset),
            $"{branch} is up in {slotName}.", retry: Create);
        if (ok)
        {
            NewBranch = "";
            NewExisting = "";
            _newSlot = null;
            foreach (var input in NewSettings) input.Value = "";
        }
        Refresh();
        Selected = Workspaces.FirstOrDefault(s => s.Record.Orchestration == loaded.Id && s.Record.Branch == branch) ?? Selected;
    }

    [RelayCommand]
    private Task Up() => UpAsync(retry: Up);

    /// <summary>Bring the selected workspace up, asking for approval first when a definition needs it, and before taking
    /// over a checkout another definition is up in. True once it's up.</summary>
    /// <param name="retry">What to carry on with once the user approves, or agrees to take over.</param>
    /// <param name="starting">The apps about to be started, so only their infra comes up; null for all of it.</param>
    /// <param name="build">The apps to build while the infra comes up, held until a start without a hold releases them,
    /// which mustn't name them to restart; null to build nothing ahead.</param>
    /// <param name="jobsAgain">Those apps' jobs run again even if they've finished.</param>
    async Task<bool> UpAsync(Func<Task> retry, Starting? starting = null, Starting? build = null, bool jobsAgain = false)
    {
        if (Selected?.Record is not { } record) return false;
        var takeOver = _takeOverAgreed;
        _takeOverAgreed = false;
        var loaded = await LoadAsync(record.OrchestrationPath, retry, record.Name);
        if (loaded is null) return false;
        if (record.InPlace && !takeOver && await AppServices.RunAsync(() => _services.Workspaces.HolderOf(loaded, record.Name)) is { } holder)
        {
            AskTakeOver(holder, record, retry);
            return false;
        }
        var title = WorkspaceItem.Display(record);
        string? held = null;
        Action<WorkspaceRecord, Plan>? beforeInfra = build is null ? null
            : (r, plan) => held = _services.Host.Prebuild(r, plan, build.AppsIn(plan), jobsAgain);
        // No branch given: a slot stays on whatever its worktrees have checked out, even if that was switched with git.
        var ok = await RunAsync($"Bringing {title} up",
            (progress, cancellation) => _services.Workspaces.Up(loaded, record.Name, null, progress: progress, cancellation: cancellation, takeOver: takeOver,
                beforeInfra: beforeInfra, starting: starting),
            $"{title} is up.",
            retry: retry);
        if (!ok && held is not null) _ = AppServices.RunAsync(() => _services.Host.TrySend(new HostRequest("stop", Workspace: held)));
        Refresh();
        return ok;
    }

    // ---- taking over a checkout ----

    /// <summary>Another definition is up in the checkout the selected main runs in, so starting it asks first.</summary>
    [ObservableProperty] private bool _confirmingTakeOver;
    [ObservableProperty] private string _takeOverTitle = "";
    [ObservableProperty] private string _takeOverLine = "";

    Func<Task>? _afterTakeOver;
    bool _takeOverAgreed;

    void AskTakeOver(WorkspaceRecord holder, WorkspaceRecord record, Func<Task> then)
    {
        var where = holder.Repos.Where(r => !r.Worktree).Select(r => r.Dir.Replace('\\', '/')).FirstOrDefault() ?? "the same checkout";
        TakeOverTitle = $"Take over from {holder.SlotName}?";
        TakeOverLine = $"{WorkspaceItem.Display(holder)} is up in {where}, and one definition runs in a checkout at a time. "
                       + $"Taking over stops its apps and takes its infra down, keeping its data, then starts {record.Orchestration} there.";
        _afterTakeOver = then;
        ConfirmingTakeOver = true;
    }

    [RelayCommand]
    private void CancelTakeOver()
    {
        ConfirmingTakeOver = false;
        _afterTakeOver = null;
    }

    [RelayCommand]
    private async Task TakeOver()
    {
        ConfirmingTakeOver = false;
        if (_afterTakeOver is not { } next) return;
        _afterTakeOver = null;
        _takeOverAgreed = true;
        await next();
    }

    [RelayCommand]
    private async Task Down()
    {
        if (Selected?.Record is not { } record) return;
        var title = WorkspaceItem.Display(record);
        await RunAsync($"Stopping {title}",
            (progress, cancellation) => _services.Workspaces.Down(record.Orchestration, record.Name, progress, cancellation),
            $"{title} is down. Its data, worktrees and ports are kept.");
        Refresh();
    }

    /// <summary>Free the selected slot for the next branch, keeping its branch, data, ports and values.</summary>
    [RelayCommand]
    private async Task Free()
    {
        if (Selected?.Record is not { } record || record.InPlace || record.IsFree) return;
        await RunAsync($"Freeing {record.SlotName}",
            (progress, cancellation) => _services.Workspaces.Free(record.Orchestration, record.Name, progress, cancellation),
            $"{record.SlotName} is free. '{record.Branch}' is kept with its commits, and the slot keeps its data, ports and values for the next branch.");
        Refresh();
    }

    [RelayCommand]
    private void AskEmptyData() => ConfirmingEmptyData = true;

    [RelayCommand]
    private void CancelEmptyData() => ConfirmingEmptyData = false;

    /// <summary>Delete the selected workspace's infra data, then bring it back as it was: up again if it was up, with the
    /// apps that were running started again after the jobs they depend on, which run again against the empty data.</summary>
    [RelayCommand]
    private async Task EmptyData()
    {
        if (Selected is not { CanEmptyData: true, Record: var record }) return;
        ConfirmingEmptyData = false;
        // Taken before, since emptying stops them.
        var running = await AppServices.RunAsync(() => (_services.Host.TrySend(new HostRequest("list"))?.Runners ?? [])
            .Where(r => r.Workspace == record.Key && r.State.IsAlive()).Select(r => r.Name).ToArray());
        var wasUp = record.State == WorkspaceState.Up;
        var title = WorkspaceItem.Display(record);
        var emptied = await RunAsync($"Emptying {title}'s data",
            (progress, cancellation) => _services.Workspaces.EmptyData(record.Orchestration, record.Name, progress, cancellation),
            wasUp ? $"{title}'s data is empty. It comes back up next, with the apps that were running."
                : $"{title}'s data is empty. It starts that way next time it comes up.");
        Refresh();
        if (emptied && wasUp) await BringBackAsync(running);
    }

    /// <param name="running">The apps that were running, by key; jobs among them are left to the apps that need them.</param>
    async Task BringBackAsync(IReadOnlyList<string> running)
    {
        // Built while the infra comes up. A migration that finished did so against the data that's gone, so jobs run again.
        var build = new Starting(Apps: running);
        if (!await UpAsync(retry: () => BringBackAsync(running), build: build, jobsAgain: true)) return;
        if (Selected?.Record is not { State: WorkspaceState.Up } record
            || _services.Workspaces.LastPlan(record.Orchestration, record.Name) is not { } plan) return;
        var apps = running.Where(n => plan.Apps.Any(a => a.Key == n && !a.Job)).ToArray();
        if (apps.Length == 0) return;
        try
        {
            await AppServices.RunAsync(() =>
            {
                var specs = RunnerSpecs.For(record, plan, apps);
                // Releases the held builds; naming them to restart would build them again.
                var answer = _services.Host.Send(new HostRequest("start", specs, Restart: []));
                if (!answer.Ok) throw new InvalidOperationException(answer.Error ?? "the host wouldn't start the apps");
            });
            SayApps($"Starting {(apps.Length == 1 ? "the app that was running" : $"the {apps.Length} apps that were running")} again, after their jobs.", bad: false);
            await RefreshRunnersAsync();
        }
        catch (Exception e)
        {
            SayApps(e.Message, bad: true);
        }
    }

    [RelayCommand]
    private void AskRemove() => ConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    [RelayCommand]
    private async Task Remove()
    {
        if (Selected is not { CanRemove: true, Record: var record }) return;
        ConfirmingRemove = false;
        var options = new RemoveOptions(DeleteBranch && !record.InPlace);
        var title = WorkspaceItem.Display(record);
        await RunAsync($"Removing {title}",
            (progress, cancellation) =>
            {
                if (record.IsMain) _services.Workspaces.RemoveMain(record.Orchestration, progress, cancellation);
                else _services.Workspaces.Remove(record.Orchestration, record.Name, options, progress, cancellation);
            },
            $"{title} is gone.");
        Refresh();
    }

    [RelayCommand]
    private async Task ApproveAll()
    {
        foreach (var item in PendingTrust) _services.Trust.Approve(item.TrustPath, item.Sha256);
        PendingTrust.Clear();
        HasPendingTrust = false;
        Review.Clear();
        if (_afterApproval is { } next)
        {
            _afterApproval = null;
            await next();
        }
    }

    /// <summary>Load an orchestration, asking for approval first when a definition needs it. Null when it can't be used yet;
    /// the form says why.</summary>
    /// <param name="workspace">When it names a workspace with worktrees, the repos' definitions are read from those.</param>
    async Task<LoadedOrchestration?> LoadAsync(string path, Func<Task> retry, string? workspace = null)
    {
        IsWorking = true;
        try
        {
            var loaded = await AppServices.RunAsync(() =>
                _services.Loader.Load(path, workspace is null ? null : _services.Workspaces.Worktrees(path, workspace)));
            if (loaded.Untrusted.Count > 0)
            {
                AskForTrust(loaded.Untrusted, retry);
                return null;
            }
            if (loaded.Orchestration is null || loaded.Problems.Count > 0)
            {
                var page = loaded.Alone ? "Repos" : "Orchestrations";
                Fail("It can't be planned: " + string.Join("; ", loaded.Problems.Take(3).Select(p => p.Message)) +
                     $". The {page} page shows the details.");
                return null;
            }
            return loaded;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail($"It couldn't be read: {e.Message}");
            return null;
        }
        finally
        {
            IsWorking = false;
        }
    }

    void AskForTrust(IEnumerable<NeedsTrust> pending, Func<Task> then)
    {
        PendingTrust.Clear();
        foreach (var p in pending) PendingTrust.Add(PendingTrustItem.From(p));
        HasPendingTrust = PendingTrust.Count > 0;
        _afterApproval = then;
        Review.Load(PendingTrust);
        // Asked for because the user just started something, so the review opens straight away.
        Review.OpenCommand.Execute(null);
    }

    /// <summary>An error's first paragraph: what went wrong. The rest (a service's log, say) is already on the failed
    /// step, so the summary under the steps stays short.</summary>
    internal static string Headline(string message)
    {
        var end = message.IndexOf("\n\n", StringComparison.Ordinal);
        return (end < 0 ? message : message[..end]).Trim();
    }

    /// <summary>Run one operation off the UI thread, reporting into a progress window.</summary>
    /// <param name="retry">Run again once the user approves definitions the operation found unapproved.</param>
    async Task<bool> RunAsync(string heading, Action<IProgress<WorkspaceEvent>, CancellationToken> work, string success, Func<Task>? retry = null)
    {
        var window = new OperationProgressViewModel(heading);
        var progress = new Progress<WorkspaceEvent>(window.Apply);
        ProgressStarted?.Invoke(window);
        IsWorking = true;
        var running = AppServices.RunAsync(() => work(progress, window.Cancellation));
        try
        {
            await FinishedOrAbandoned(running, window.Cancellation);
            // Progress<T> posts its reports, so let the last ones land before the summary.
            await Task.Yield();
            window.Finish(success, ProgressState.Done);
            return true;
        }
        catch (OperationCanceledException)
        {
            await Task.Yield();
            window.Finish(running.IsCompleted
                ? "Cancelled part-way. What had finished stays done; run it again to finish the rest."
                : "Cancelled, but the step it was on hasn't let go and is still going in the background. Docker has probably stopped answering: restarting Docker Desktop frees it.",
                ProgressState.Warning);
            return false;
        }
        catch (WorkspaceException e) when (e.Untrusted.Count > 0 && retry is not null)
        {
            AskForTrust(e.Untrusted, retry);
            window.Finish("The workspace's branch changes these definitions, so they need approving first:\n"
                + string.Join("\n", e.Untrusted.Select(u => u.Path.Replace('\\', '/')))
                + "\nReview them in the main window and it carries on.", ProgressState.Error);
            return false;
        }
        catch (WorkspaceException e) when (e.Asks.Count > 0)
        {
            window.Finish($"{Headline(e.Message)}.\nGive {(e.Asks.Count == 1 ? "it" : "them")} a value with the workspace's Settings, then bring it up again.",
                ProgressState.Error);
            return false;
        }
        catch (Exception e)
        {
            var problems = e is WorkspaceException se ? se.Problems.Where(p => p.Severity == ProblemSeverity.Error).Take(4).Select(p => p.Message) : [];
            window.Finish(string.Join("\n", new[] { Headline(e.Message) }.Concat(problems)), ProgressState.Error);
            return false;
        }
        finally
        {
            // Abandoned work keeps the page's up and down buttons off until it really ends, so two never overlap.
            if (running.IsCompleted) IsWorking = false;
            else _ = running.ContinueWith(_ => Dispatcher.UIThread.Post(() => IsWorking = false), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The work's own outcome; or, once it has been cancelled and still hasn't stopped a few seconds later, an
    /// <see cref="OperationCanceledException"/> without it. Whatever it was blocked on (a docker that has stopped
    /// answering, say) may never notice the cancel, and the window must not be held open by it.
    /// </summary>
    public static async Task FinishedOrAbandoned(Task running, CancellationToken cancellation, TimeSpan? patience = null)
    {
        var asked = Task.Delay(Timeout.Infinite, cancellation);
        if (await Task.WhenAny(running, asked) != running
            && await Task.WhenAny(running, Task.Delay(patience ?? TimeSpan.FromSeconds(5))) != running)
            throw new OperationCanceledException(cancellation);
        await running;
    }

    void Fail(string message)
    {
        FormFailed = true;
        FormMessage = message;
    }
}
