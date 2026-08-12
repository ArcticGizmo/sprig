using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Sprig.App.Controls;
using Sprig.Core.Graph;
using Sprig.Core.Pools;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.App.ViewModels;

public partial class WorkspacesViewModel : PageViewModel
{
    protected readonly AppServices Services;
    readonly Navigator _nav;

    public WorkspacesViewModel(AppServices services, Navigator nav)
    {
        Services = services;
        _nav = nav;
        _ = RefreshAsync();
    }

    public override string Title => "Workspaces";

    /// <summary>Raised when a create/teardown begins, carrying the checklist view-model for the view to
    /// open in its own non-blocking progress window (keeps this view-model free of Avalonia window types).</summary>
    public event Action<OperationProgressViewModel>? OperationStarted;

    /// <summary>False when no stacks exist — a workspace can't be created without one (upstream empty state).</summary>
    [ObservableProperty] private bool _hasAnyStacks;

    /// <summary>Empty-state shortcut: jump to Stacks and open the builder.</summary>
    [RelayCommand] private void BuildStack() => _nav.NewStack();

    /// <summary>Every workspace, flat — the identity set the detail pane's <see cref="Selected"/> is drawn
    /// from. The list surface renders <see cref="Pools"/> instead; both share the same item instances so
    /// selecting a row in a pool group drives this.</summary>
    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    /// <summary>The stacks' pools — one group per stack (plus a residual "(ad-hoc)" group for any
    /// pre-pool workspaces) — the grouped surface the list renders.</summary>
    public ObservableCollection<PoolGroupViewModel> Pools { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(DownCommand), nameof(ResetCommand),
        nameof(ReconcileCommand), nameof(RepairCommand), nameof(RemoveCommand), nameof(ReleaseCommand),
        nameof(ClaimCommand), nameof(RefreshStatusCommand), nameof(OpenDockerCommand))]
    private WorkspaceItemViewModel? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(DownCommand), nameof(ResetCommand),
        nameof(ReconcileCommand), nameof(RepairCommand), nameof(RemoveCommand), nameof(ReleaseCommand),
        nameof(ClaimCommand), nameof(RefreshCommand), nameof(NewWorkspaceCommand),
        nameof(RefreshStatusCommand), nameof(OpenDockerCommand))]
    private bool _busy;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _statusMessage;

    // Remove confirmation state.
    [ObservableProperty] private bool _confirmingRemove;
    [ObservableProperty] private bool _removeForce;

    // Create-workspace flow.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockerWarning))]
    private bool _isCreating;
    [ObservableProperty] private string? _newStack;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _createError;

    /// <summary>The chosen stack's repos, each tickable — untick one to create a <i>partial</i>
    /// workspace without it. Rebuilt whenever the stack selection changes.</summary>
    public ObservableCollection<WorkspaceRepoChoiceViewModel> NewRepos { get; } = [];

    /// <summary>The stack definition behind <see cref="NewRepos"/>, needed to work out which ports a
    /// deselection orphans. Null until a stack is picked.</summary>
    StackDefinition? _newStackDef;

    /// <summary>Only worth showing the repo checklist when there's a choice to make.</summary>
    public bool CanChooseRepos => NewRepos.Count > 1;

    /// <summary>True once at least one repo is unticked — the workspace will be partial.</summary>
    public bool IsPartialSelection => NewRepos.Any(r => !r.Included);

    /// <summary>Plain-language consequence of the current deselection: which repos are left out and
    /// which stack ports that leaves with no consumer (so they won't be provisioned). Null when the
    /// whole stack is selected.</summary>
    public string? PartialHint
    {
        get
        {
            if (_newStackDef is null || !IsPartialSelection) return null;
            var excluded = NewRepos.Where(r => !r.Included).Select(r => r.Name).ToList();
            var kept = NewRepos.Where(r => r.Included).Select(r => r.Name).ToList();
            if (kept.Count == 0) return "Pick at least one repo.";

            var skipped = StackSelection.OrphanedPorts(_newStackDef, kept);
            var hint = $"Partial workspace — no worktree, env or compose for {string.Join(", ", excluded)}.";
            return skipped.Count == 0 ? hint
                : $"{hint} Ports left with no consumer won't be provisioned: {string.Join(", ", skipped)}.";
        }
    }

    /// <summary>Recompute the partial-selection surface after a repo is ticked or unticked.</summary>
    void OnRepoChoiceChanged()
    {
        OnPropertyChanged(nameof(IsPartialSelection));
        OnPropertyChanged(nameof(PartialHint));
    }

    /// <summary>The in-flight checklist load, so opening the modal can wait for the repos to land
    /// (and so a test can too) rather than racing a fire-and-forget.</summary>
    Task _repoLoad = Task.CompletedTask;

    partial void OnNewStackChanged(string? value) => _repoLoad = LoadStackReposAsync(value);

    /// <summary>Load the picked stack's repos into the checklist (all ticked by default, so the
    /// default create is exactly what it was before partial workspaces existed).</summary>
    async Task LoadStackReposAsync(string? stackName)
    {
        NewRepos.Clear();
        _newStackDef = null;
        NotifyRepoChoicesChanged();
        if (string.IsNullOrEmpty(stackName)) return;

        StackDefinition? def;
        try { def = await AppServices.RunAsync(() => Services.Stacks.Get(stackName)); }
        catch { def = null; }
        // A slow load losing a race with a newer pick must not repopulate the old stack's repos.
        if (def is null || def.Name != NewStack) return;

        _newStackDef = def;
        foreach (var repo in def.Repos)
            NewRepos.Add(new WorkspaceRepoChoiceViewModel(repo, OnRepoChoiceChanged));
        NotifyRepoChoicesChanged();
    }

    void NotifyRepoChoicesChanged()
    {
        OnPropertyChanged(nameof(CanChooseRepos));
        OnRepoChoiceChanged();
    }

    /// <summary>Bring the new workspace's infra up right after creating it (default on).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockerWarning))]
    private bool _startInfraOnCreate = true;

    /// <summary>Whether the Docker engine is reachable, probed while the create modal is open.
    /// Optimistically true until the probe answers, so the warning never flashes before we know.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockerWarning))]
    private bool _dockerRunning = true;

    /// <summary>True while the engine probe is in flight (drives the "Checking…" affordance).</summary>
    [ObservableProperty] private bool _checkingDocker;

    /// <summary>Warn in the create modal when "start infra" is asked for but the engine isn't running —
    /// so the user finds out before hitting Create, not as an after-the-fact soft warning.</summary>
    public bool ShowDockerWarning => IsCreating && StartInfraOnCreate && !DockerRunning && !CheckingDocker;

    partial void OnCheckingDockerChanged(bool value) => OnPropertyChanged(nameof(ShowDockerWarning));

    /// <summary>Re-probe the engine when "start infra" is (re)ticked — it may have come up meanwhile.</summary>
    partial void OnStartInfraOnCreateChanged(bool value)
    {
        if (value && IsCreating) _ = ProbeDockerAsync();
    }

    /// <summary>Probe the Docker engine off the UI thread and update <see cref="DockerRunning"/>.</summary>
    async Task ProbeDockerAsync()
    {
        CheckingDocker = true;
        try { DockerRunning = await AppServices.RunAsync(() => Services.Docker.IsEngineRunning()); }
        catch { DockerRunning = false; }
        finally { CheckingDocker = false; }
    }

    /// <summary>Re-run the engine probe (the modal's "Recheck" affordance after starting Docker).</summary>
    [RelayCommand]
    private Task RecheckDocker() => ProbeDockerAsync();

    /// <summary>Launch Docker Desktop from the create modal (no workspace selected yet, unlike
    /// <see cref="OpenDockerCommand"/>).</summary>
    [RelayCommand]
    private void OpenDockerDesktop()
    {
        try
        {
            var exe = DockerDesktopPath();
            if (exe is null)
            {
                CreateError = "Docker Desktop wasn't found in its default location — start it manually, then Recheck.";
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }
        catch (Exception ex) { CreateError = $"couldn't open Docker Desktop: {ex.Message}"; }
    }

    public ObservableCollection<string> AvailableStacks { get; } = [];

    bool HasSelection => Selected is not null && !Busy;
    bool NotBusy => !Busy;

    /// <summary>Bound by the view to toggle the empty-state vs. detail panel.</summary>
    public bool HasSelected => Selected is not null;

    /// <summary>False when no workspaces exist at all — drives the first-run empty state.</summary>
    public bool HasWorkspaces => Workspaces.Count > 0;

    /// <summary>True once there's at least one pool (stack) or residual group to show. Drives the list
    /// surface — a stack with an empty pool still shows, since that's where Checkout lives.</summary>
    public bool HasPools => Pools.Count > 0;

    partial void OnSelectedChanged(WorkspaceItemViewModel? value)
    {
        ConfirmingRemove = false;
        Error = null;
        StatusMessage = null;
        OnPropertyChanged(nameof(HasSelected));
        if (value is not null) _ = LoadStatusAsync(value);
    }

    /// <summary>Probe docker for the workspace's live containers and fill its display flags.</summary>
    async Task LoadStatusAsync(WorkspaceItemViewModel item)
    {
        if (!item.HasInfra) { item.SetContainers([], dockerAvailable: true); return; }
        try
        {
            var list = await AppServices.RunAsync(() => Services.Workspaces.Status(item.Name));
            item.SetContainers(list, dockerAvailable: true);
        }
        catch (WorkspaceException)
        {
            // RequireWithInfra throws this when docker compose isn't available.
            item.SetContainers([], dockerAvailable: false);
        }
        catch (Exception)
        {
            item.SetContainers([], dockerAvailable: false);
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task RefreshAsync() => Guard(LoadAsync, status: null);

    /// <summary>Gathered pool state, ready to turn into view-models on the UI thread. Built off-thread
    /// (every field is a blocking Core call) so the refresh never stalls the UI.</summary>
    sealed record PoolData(
        IReadOnlyList<(string Stack, int MaxSlots, IReadOnlyList<InstanceRecord> Records)> Pools,
        IReadOnlyList<InstanceRecord> Orphans,
        bool HasStacks);

    /// <summary>Read the pool of every stack, plus any workspace that belongs to no current stack.
    /// Runs on a background thread.</summary>
    PoolData GatherPools()
    {
        var stacks = Services.Stacks.List();
        var pools = new List<(string, int, IReadOnlyList<InstanceRecord>)>();
        var claimedByPool = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stack in stacks.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var status = Services.Pools.Status(stack.Name);
            pools.Add((status.Stack, status.MaxSlots, status.Workspaces));
            foreach (var w in status.Workspaces) claimedByPool.Add(w.Workspace);
        }

        // Anything a pool didn't account for (Stack null, or a stack since deleted) still needs a home
        // in the list — gather it into the residual group rather than dropping it silently.
        var orphans = Services.Workspaces.List()
            .Where(r => !claimedByPool.Contains(r.Workspace))
            .OrderBy(r => r.Workspace, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new PoolData(pools, orphans, stacks.Count > 0);
    }

    /// <summary>The load currently in flight, so overlapping refresh requests coalesce onto it rather than
    /// interleaving two Clear/rebuild passes. In the app the UI thread already serialises these; this makes
    /// it deterministic anywhere (e.g. a test with no synchronization context, where the ctor's initial
    /// refresh could otherwise race an explicit one).</summary>
    Task? _inFlightLoad;

    /// <summary>Rebuild <see cref="Workspaces"/> and <see cref="Pools"/> from the store, preserving the
    /// current selection by name. The one refresh path for both the command and post-mutation reloads.</summary>
    Task LoadAsync()
    {
        if (_inFlightLoad is { IsCompleted: false }) return _inFlightLoad;
        return _inFlightLoad = LoadCore();
    }

    async Task LoadCore()
    {
        var keep = Selected?.Name;
        var data = await AppServices.RunAsync(GatherPools);

        HasAnyStacks = data.HasStacks;
        Workspaces.Clear();
        Pools.Clear();

        foreach (var (stack, maxSlots, records) in data.Pools)
        {
            var items = records.Select(r => new WorkspaceItemViewModel(r)).ToList();
            foreach (var item in items) Workspaces.Add(item);
            Pools.Add(new PoolGroupViewModel(stack, maxSlots, items));
        }
        if (data.Orphans.Count > 0)
        {
            var items = data.Orphans.Select(r => new WorkspaceItemViewModel(r)).ToList();
            foreach (var item in items) Workspaces.Add(item);
            Pools.Add(new PoolGroupViewModel("(ad-hoc)", maxSlots: 0, items, isPool: false));
        }

        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(HasPools));
        NavCount = Workspaces.Count;
        Selected = Workspaces.FirstOrDefault(w => w.Name == keep) ?? Workspaces.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task NewWorkspace()
    {
        CreateError = null;
        NewName = "";
        NewStack = null;
        StartInfraOnCreate = true;
        var names = await AppServices.RunAsync(() => Services.Stacks.List().Select(s => s.Name).ToList());
        AvailableStacks.Clear();
        foreach (var n in names) AvailableStacks.Add(n);
        NewStack = AvailableStacks.FirstOrDefault();
        await _repoLoad;        // open with the repo checklist already filled in
        DockerRunning = true;   // optimistic until the probe answers
        IsCreating = true;
        if (StartInfraOnCreate) _ = ProbeDockerAsync();
    }

    [RelayCommand]
    private void CancelCreate() => IsCreating = false;

    [RelayCommand]
    private async Task Create()
    {
        var stack = NewStack;
        var name = NewName.Trim();
        if (string.IsNullOrEmpty(stack)) { CreateError = "pick a stack (define one in the Stacks tab first)"; return; }
        if (string.IsNullOrEmpty(name)) { CreateError = "enter a workspace name"; return; }

        // Null selection = the whole stack, which keeps a full create on exactly the path it always took.
        var selection = IsPartialSelection
            ? NewRepos.Where(r => r.Included).Select(r => r.Name).ToList()
            : null;
        if (selection is { Count: 0 }) { CreateError = "pick at least one repo"; return; }

        // Resolve + plan up front so pre-flight problems (bad name, duplicate, bad stack) stay in the
        // inline create form; only once we have a real plan do we hand off to the progress window.
        ResolvedStack resolved;
        IReadOnlyList<WorkspaceStep> plan;
        try
        {
            resolved = await AppServices.RunAsync(() => Services.StackResolver.Resolve(stack, selection));
            plan = Services.Workspaces.PlanCreate(resolved, name);
        }
        catch (Exception ex) { CreateError = ex.Message; return; }

        var startInfra = StartInfraOnCreate;
        var modal = new OperationProgressViewModel($"Creating workspace '{name}'");
        modal.Load(plan);
        // "Start infrastructure" is driven here (not by the service) — append it as the final row.
        var infraStep = startInfra ? modal.AddStep("infra", "Start infrastructure") : null;
        IsCreating = false;
        OperationStarted?.Invoke(modal);

        Busy = true;
        CreateError = null;
        try
        {
            var progress = new Progress<WorkspaceStepProgress>(modal.Apply);
            var record = await AppServices.RunAsync(() => Services.Workspaces.Create(resolved, name, progress));

            // Optionally bring the infra up straight away. A failure here (e.g. Docker not running)
            // is a soft warning — the workspace itself was created successfully.
            var hasInfra = record.Repos.Any(r => r.ComposePaths.Count > 0);
            var started = false;
            string? infraWarning = null;
            if (infraStep is not null)
            {
                if (!hasInfra)
                {
                    infraStep.Detail = "no Docker infrastructure in this stack";
                    infraStep.State = WorkspaceStepState.Done;
                }
                else if (!await AppServices.RunAsync(() => Services.Docker.IsEngineRunning()))
                {
                    // Docker Desktop is stopped — skip the start cleanly with a plain, actionable note
                    // instead of letting `docker up` throw a raw daemon-connection error.
                    infraWarning = WorkspaceService.DockerNotRunningNote;
                    infraStep.Detail = WorkspaceService.DockerNotRunningNote;
                    infraStep.State = WorkspaceStepState.Warning;
                }
                else
                {
                    infraStep.State = WorkspaceStepState.Running;
                    try
                    {
                        await AppServices.RunAsync(() => Services.Workspaces.Up(name));
                        started = true;
                        infraStep.State = WorkspaceStepState.Done;
                    }
                    catch (Exception ex)
                    {
                        infraWarning = ex.Message;
                        infraStep.Detail = ex.Message;
                        infraStep.State = WorkspaceStepState.Warning;
                    }
                }
            }

            await RefreshCore();
            Selected = Workspaces.FirstOrDefault(w => w.Name == name) ?? Selected;
            var partialNote = record.IsPartial ? $" (partial — without {string.Join(", ", record.ExcludedRepos)})" : "";
            StatusMessage = (started ? $"created '{name}' and started its infra" : $"created '{name}'") + partialNote;
            // Both a setup failure and an infra-start failure are soft warnings — the workspace itself
            // was created. Surface whichever happened (setup first, since it ran first).
            var setupWarning = SetupWarning.Summarize(record);
            Error = setupWarning is not null ? $"created '{name}', but {setupWarning} — finish setup manually in the worktree"
                : infraWarning is not null ? $"created '{name}', but couldn't start its infra: {infraWarning}"
                : null;
            Services.NotifyStoreChanged();

            var warned = setupWarning is not null || infraWarning is not null;
            modal.Finish(
                warned ? Error! : started ? $"Created '{name}' and started its infra." : $"Created '{name}'.",
                warned ? WorkspaceStepState.Warning : WorkspaceStepState.Done);
        }
        catch (Exception ex)
        {
            // The create form is already closed — surface the failure in the progress window instead.
            // Create rolls itself back, but refresh anyway so the list always matches the store on exit.
            await TryRefreshCore();
            modal.Finish($"Couldn't create '{name}': {ex.Message}", WorkspaceStepState.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    // -- pool checkout / release ------------------------------------------------

    /// <summary>True while the checkout overlay is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHandling))]
    private bool _isCheckingOut;

    /// <summary>The stack whose pool is being checked out of (fixed for the life of the overlay).</summary>
    [ObservableProperty] private string? _checkoutStack;
    [ObservableProperty] private string? _checkoutError;

    /// <summary>The required branch name — the workspace's identity, cut across every repo in the stack.</summary>
    [ObservableProperty] private string _checkoutBranch = "";

    /// <summary>The optional checkout label — a note to recognise this workspace by.</summary>
    [ObservableProperty] private string _checkoutLabel = "";

    /// <summary>The free (unclaimed) workspaces available to reuse for this checkout.</summary>
    public ObservableCollection<WorkspaceItemViewModel> CheckoutFreeWorkspaces { get; } = [];

    /// <summary>True = build a brand-new workspace; false = reuse the selected free one. Paired with
    /// <see cref="CheckoutReuse"/> as the two options of the target radio group (kept mutually exclusive
    /// by the group + their initial values in <c>Checkout</c>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHandling))]
    private bool _checkoutNew;

    /// <summary>The inverse of <see cref="CheckoutNew"/> — bound to the "reuse a free workspace" radio.</summary>
    [ObservableProperty] private bool _checkoutReuse;

    /// <summary>The free workspace to reuse (when not building new). Drives the handling choices below.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHandling))]
    private WorkspaceItemViewModel? _checkoutTarget;

    /// <summary>True when the pool has room to build a new workspace (enables the "New workspace" choice).</summary>
    [ObservableProperty] private bool _canCheckoutNew;

    /// <summary>True when the pool has at least one free workspace to reuse (enables the "Reuse" choice).</summary>
    [ObservableProperty] private bool _canReuseWorkspace;

    // Handling mode (only when reusing a free workspace). Two radio bools sharing a group in XAML; exactly
    // one is true. Both cut a clean branch from base; keep leaves deps + volumes, fresh reinstalls and wipes.
    [ObservableProperty] private bool _modeKeep = true;
    [ObservableProperty] private bool _modeFresh;

    /// <summary>The handling choices only make sense when reusing an existing workspace.</summary>
    public bool ShowHandling => IsCheckingOut && !CheckoutNew && CheckoutTarget is not null;

    // -- "start from" picker: searchable, chipped, recent-by-default -------------

    /// <summary>The rows currently shown in the picker dropdown (recent when the search box is empty, matches
    /// otherwise). Rebuilt from <see cref="_allStartPoints"/> whenever the search text changes.</summary>
    public ObservableCollection<StartPointItemViewModel> StartPointResults { get; } = [];

    IReadOnlyList<StartPointChoice> _allStartPoints = [];
    const int StartPointRecentLimit = 8;
    string _defaultBaseLabel = "base";

    /// <summary>The chosen start point; <c>null</c> means "Default" (let each repo resolve its base).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartPointDisplay))]
    private StartPointItemViewModel? _selectedStartPointItem;

    /// <summary>True while the branch list is being fetched (branches come from a network fetch) — the
    /// dropdown shows a spinner meanwhile.</summary>
    [ObservableProperty] private bool _startPointsLoading;

    /// <summary>Whether the picker dropdown is open.</summary>
    [ObservableProperty] private bool _isStartPointOpen;

    /// <summary>The search text; empty shows the recent leaders, any text filters every candidate.</summary>
    [ObservableProperty] private string _startPointSearch = "";

    /// <summary>The dropdown's list selection (transient) — picking one funnels through
    /// <see cref="PickStartPoint"/>. Distinct from <see cref="SelectedStartPointItem"/>, the confirmed choice.</summary>
    [ObservableProperty] private StartPointItemViewModel? _pendingStartPointItem;

    /// <summary>Label on the collapsed picker: the chosen ref, or the default base.</summary>
    public string StartPointDisplay => SelectedStartPointItem?.Ref ?? DefaultStartPointLabel;

    /// <summary>Text for the always-present "Default" row / collapsed label — the resolved base.</summary>
    public string DefaultStartPointLabel => $"Default — {_defaultBaseLabel}";

    partial void OnStartPointSearchChanged(string value) => RebuildStartPointResults();

    partial void OnPendingStartPointItemChanged(StartPointItemViewModel? value)
    {
        if (value is not null) PickStartPoint(value);
    }

    void RebuildStartPointResults()
    {
        StartPointResults.Clear();
        foreach (var c in StartPointFilter.Apply(_allStartPoints, StartPointSearch, StartPointRecentLimit))
            StartPointResults.Add(new StartPointItemViewModel(c));
    }

    [RelayCommand]
    private void ToggleStartPoint()
    {
        IsStartPointOpen = !IsStartPointOpen;
        if (IsStartPointOpen) { StartPointSearch = ""; PendingStartPointItem = null; RebuildStartPointResults(); }
    }

    /// <summary>Pick a start point (or the "Default" row, which passes <c>null</c>) and close the dropdown.</summary>
    [RelayCommand]
    private void PickStartPoint(StartPointItemViewModel? item)
    {
        SelectedStartPointItem = item;
        IsStartPointOpen = false;
        PendingStartPointItem = null; // ready for the next open (so re-picking the same row still fires)
    }

    // Short-lived cache of fetched picker options per stack, so reopening skips the network refresh.
    readonly Dictionary<string, (DateTimeOffset When, StartPointOptions Opts)> _startPointCache = new(StringComparer.Ordinal);
    static readonly TimeSpan StartPointCacheTtl = TimeSpan.FromSeconds(30);

    void ApplyStartPoints(StartPointOptions options)
    {
        _defaultBaseLabel = options.Default ?? "base";
        _allStartPoints = options.Candidates;
        OnPropertyChanged(nameof(DefaultStartPointLabel));
        OnPropertyChanged(nameof(StartPointDisplay));
        RebuildStartPointResults();
    }

    /// <summary>Fill the picker fast: show cached-or-local refs immediately (no network), then refresh from a
    /// background fetch unless the cache is still warm. The actual checkout re-fetches before branching, so
    /// the instantly-shown list can lag slightly without affecting what you end up on.</summary>
    async Task LoadStartPointsAsync(string stack)
    {
        // 1) Instant: cached options if we have them, else a local (no-fetch) read — the list shows at once.
        if (_startPointCache.TryGetValue(stack, out var cached))
            ApplyStartPoints(cached.Opts);
        else
        {
            try { ApplyStartPoints(await AppServices.RunAsync(() => Services.Pools.StartPointsFor(stack, null, fetch: false))); }
            catch { ApplyStartPoints(new StartPointOptions(null, [])); }
        }

        // Skip the network refresh while the cache is warm.
        if (_startPointCache.TryGetValue(stack, out var c) && DateTimeOffset.Now - c.When < StartPointCacheTtl)
            return;

        // 2) Background: fetch remotes, refresh the list, and cache the result. The spinner shows meanwhile.
        StartPointsLoading = true;
        try
        {
            var fresh = await AppServices.RunAsync(() => Services.Pools.StartPointsFor(stack, null, fetch: true));
            _startPointCache[stack] = (DateTimeOffset.Now, fresh);
            ApplyStartPoints(fresh);
        }
        catch { /* offline / no remotes — keep the instant list */ }
        finally { StartPointsLoading = false; }
    }

    // -- branch graph (GitKraken-style visual picker) ---------------------------

    const int BranchGraphLimit = 150;

    /// <summary>True while the branch-graph overlay is open.</summary>
    [ObservableProperty] private bool _isBranchGraphOpen;
    [ObservableProperty] private bool _branchGraphLoading;
    [ObservableProperty] private string? _branchGraphError;

    /// <summary>The laid-out graph the drawn lanes bind to, and the current commit's sha (ringed).</summary>
    [ObservableProperty] private CommitGraph? _branchGraph;
    [ObservableProperty] private string? _branchGraphCurrentSha;

    /// <summary>The ref a click picked (branch name or short sha) and the commit it highlights. Selecting
    /// highlights (rings) the commit rather than applying immediately — a "Use this" confirm applies it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraphSelection))]
    private string? _graphSelectedRef;
    [ObservableProperty] private string? _graphSelectedSha;

    /// <summary>Scroll offset of the graph, so the search can jump it to a branch's row.</summary>
    [ObservableProperty] private Vector _graphScrollOffset;

    /// <summary>The overlay search's selection — picking a branch here highlights + scrolls the graph to it.</summary>
    [ObservableProperty] private StartPointItemViewModel? _graphSearchSelection;

    public bool HasGraphSelection => !string.IsNullOrWhiteSpace(GraphSelectedRef);

    /// <summary>The list rows drawn beside the lanes — one per commit, aligned by row height.</summary>
    public ObservableCollection<GraphRowViewModel> GraphRows { get; } = [];

    /// <summary>Per-row heights fed to the graph control so its dots line up with the (variable-height) rows.</summary>
    [ObservableProperty] private IReadOnlyList<double> _graphRowHeights = [];

    /// <summary>Raised when the branch-graph window should open (payload is this VM, used as its DataContext).
    /// The view's code-behind opens the resizable window, mirroring the progress-window pattern.</summary>
    public event Action<WorkspacesViewModel>? BranchGraphRequested;

    /// <summary>Open the visual branch graph for the checkout's stack (first repo), highlighting the current
    /// branch. Selecting a branch/commit there sets the same start point the dropdown does.</summary>
    [RelayCommand]
    private async Task OpenBranchGraph()
    {
        var stack = CheckoutStack;
        if (stack is null || IsBranchGraphOpen) return; // already open — don't stack windows

        BranchGraphError = null;
        GraphRows.Clear();
        GraphRowHeights = [];
        BranchGraph = null;
        GraphSelectedRef = null;
        GraphSelectedSha = null;
        GraphSearchSelection = null;
        GraphScrollOffset = default;
        IsBranchGraphOpen = true;
        BranchGraphLoading = true;
        BranchGraphRequested?.Invoke(this); // open the window now; it shows the spinner while we load
        try
        {
            var existing = CheckoutNew ? null : CheckoutTarget?.Name;
            var (commits, current) = await AppServices.RunAsync(() => Services.Pools.CommitGraphFor(stack, existing, BranchGraphLimit));
            var graph = CommitGraphLayout.Build(commits);
            var currentSha = current is null
                ? null
                : graph.Nodes.FirstOrDefault(n => n.Commit.Refs.Contains(current))?.Commit.Sha;

            BranchGraph = graph;
            BranchGraphCurrentSha = currentSha;
            foreach (var n in graph.Nodes)
                GraphRows.Add(new GraphRowViewModel(n, current));
            GraphRowHeights = GraphRows.Select(r => r.RowHeight).ToList();
        }
        catch (Exception ex) { BranchGraphError = ex.Message; }
        finally { BranchGraphLoading = false; }
    }

    [RelayCommand]
    private void CloseBranchGraph() => IsBranchGraphOpen = false;

    /// <summary>Select (highlight) a ref in the graph — a branch name (pill) or short sha (commit row). Rings
    /// the commit and remembers the pick, but does <b>not</b> apply/close; that's <see cref="ConfirmGraphSelection"/>.</summary>
    [RelayCommand]
    private void SelectGraphRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return;
        GraphSelectedRef = reference;
        var row = GraphRows.FirstOrDefault(r => r.Refs.Any(x => x.Name == reference))
                  ?? GraphRows.FirstOrDefault(r => r.ShortSha == reference);
        GraphSelectedSha = row?.Sha;
    }

    /// <summary>Apply the highlighted selection as the start point (routing through the dropdown's selection)
    /// and close the graph.</summary>
    [RelayCommand]
    private void ConfirmGraphSelection()
    {
        if (string.IsNullOrWhiteSpace(GraphSelectedRef)) return;
        SelectedStartPointItem = new StartPointItemViewModel(new StartPointChoice(GraphSelectedRef, null, false, false));
        IsBranchGraphOpen = false;
    }

    // Picking a branch in the overlay's search highlights it in the graph and scrolls down to its row.
    partial void OnGraphSearchSelectionChanged(StartPointItemViewModel? value)
    {
        if (value is null) return;
        SelectGraphRef(value.Ref);
        var idx = GraphRows.ToList().FindIndex(r => r.Sha == GraphSelectedSha);
        if (idx >= 0) GraphScrollOffset = new Vector(0, idx * GraphLinesControl.RowHeight);
    }

    /// <summary>Open the checkout overlay for a stack's pool. Defaults to reusing the least-recently-used
    /// free workspace (keep), or building a new one when the pool has none free — mirroring the CLI.</summary>
    [RelayCommand]
    private void Checkout(PoolGroupViewModel? group)
    {
        if (group is null || !group.IsPool || group.IsExhausted) return;

        CheckoutStack = group.Stack;
        CheckoutError = null;
        CheckoutBranch = "";
        CheckoutLabel = "";

        CheckoutFreeWorkspaces.Clear();
        foreach (var w in group.Workspaces.Where(w => w.Free)) CheckoutFreeWorkspaces.Add(w);
        CanReuseWorkspace = CheckoutFreeWorkspaces.Count > 0;
        CanCheckoutNew = group.Headroom > 0;

        // Prefer reusing the workspace freed longest ago (its leftover state is the least likely to matter);
        // fall back to a new one when nothing's free.
        if (CanReuseWorkspace)
        {
            CheckoutNew = false;
            CheckoutTarget = CheckoutFreeWorkspaces
                .OrderBy(w => w.Record.LastUsedAt ?? DateTimeOffset.MinValue).First();
        }
        else
        {
            CheckoutNew = true;
            CheckoutTarget = null;
        }
        CheckoutReuse = !CheckoutNew;

        ModeKeep = true;
        ModeFresh = false;

        // Reset the start-from picker to "Default", then fetch the real remote branches in the background.
        _defaultBaseLabel = "base";
        _allStartPoints = [];
        StartPointResults.Clear();
        StartPointSearch = "";
        SelectedStartPointItem = null;
        IsStartPointOpen = false;
        OnPropertyChanged(nameof(StartPointDisplay));

        IsCheckingOut = true;
        _ = LoadStartPointsAsync(group.Stack);
    }

    [RelayCommand]
    private void CancelCheckout() => IsCheckingOut = false;

    static string ModeLabel(CheckoutMode mode) => mode == CheckoutMode.Fresh ? "fresh" : "keep";

    [RelayCommand]
    private async Task ConfirmCheckout()
    {
        var stack = CheckoutStack;
        if (stack is null) return;

        var branch = CheckoutBranch.Trim();
        if (branch.Length == 0) { CheckoutError = "give this workspace a branch name"; return; }
        var label = CheckoutLabel.Trim();

        string? existing = CheckoutNew ? null : CheckoutTarget?.Name;
        if (!CheckoutNew && existing is null) { CheckoutError = "pick a workspace to reuse, or choose a new one"; return; }

        var mode = !CheckoutNew && ModeFresh ? CheckoutMode.Fresh : CheckoutMode.Keep;

        // Surface (do not resolve) a branch conflict up front, keeping the message in the overlay: a local
        // branch of this name is a hard block; one only on a remote is a soft heads-up.
        if (existing is not null)
        {
            try
            {
                var conflicts = await AppServices.RunAsync(() => Services.Pools.CheckCheckout(existing, branch));
                if (conflicts.IsBlocked)
                {
                    CheckoutError = $"branch '{branch}' already exists in {string.Join(", ", conflicts.Blocked)} — " +
                        "delete/rename it there or pick another name.";
                    return;
                }
            }
            catch (Exception ex) { CheckoutError = ex.Message; return; }
        }

        // Plan up front so pre-flight problems stay in the overlay; only a real plan opens the progress window.
        IReadOnlyList<WorkspaceStep> plan;
        try { plan = await AppServices.RunAsync(() => Services.Pools.PlanCheckout(stack, existing, mode)); }
        catch (Exception ex) { CheckoutError = ex.Message; return; }

        var heading = existing is null
            ? $"Checking out a new workspace from '{stack}'"
            : $"Checking out '{existing}' ({ModeLabel(mode)})";
        var modal = new OperationProgressViewModel(heading);
        modal.Load(plan);
        IsCheckingOut = false;
        OperationStarted?.Invoke(modal);

        Busy = true;
        Error = null;
        StatusMessage = null;
        try
        {
            var progress = new Progress<WorkspaceStepProgress>(modal.Apply);
            var labelArg = label.Length == 0 ? null : label;
            var startPoint = SelectedStartPointItem?.Ref; // null = Default (each repo resolves its base)
            var record = await AppServices.RunAsync(() =>
                Services.Pools.Checkout(stack, existing, branch, labelArg, mode, force: false, progress, startPoint));

            await RefreshCore();
            Selected = Workspaces.FirstOrDefault(w => w.Name == record.Workspace) ?? Selected;
            Services.NotifyStoreChanged();

            // A setup failure is soft: the workspace is claimed but degraded, so surface it rather than
            // reporting a clean checkout.
            if (record.SetupFailed)
            {
                Error = $"checked out '{record.Workspace}', but setup failed — this workspace is degraded; "
                      + "finish setup in the worktree, then it's ready.";
                modal.Finish(Error, WorkspaceStepState.Warning);
            }
            else
            {
                StatusMessage = $"checked out '{record.Workspace}' on branch “{branch}”";
                modal.Finish($"Checked out '{record.Workspace}' on '{branch}'.", WorkspaceStepState.Done);
            }
        }
        catch (Exception ex)
        {
            // The overlay is already closed — report in the progress window (this is where an un-pushed-commit
            // guard on fresh/refresh lands, for instance). A failed checkout can still have mutated the store
            // (a new workspace materialised before infra failed), so rebuild the list before surfacing it.
            await TryRefreshCore();
            Error = ex.Message;
            modal.Finish($"Checkout failed: {ex.Message}", WorkspaceStepState.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    bool CanClaim => Selected is { Free: true } && Selected.Record.Stack is not null && !Busy;

    /// <summary>Claim the selected free workspace straight from the detail pane: open the checkout overlay
    /// pre-targeted at this workspace (reuse, keep), so a click gives it a label and handling without
    /// hunting for it in the pool's Checkout list.</summary>
    [RelayCommand(CanExecute = nameof(CanClaim))]
    private void Claim()
    {
        var item = Selected;
        if (item is null || !item.Free || item.Record.Stack is not { } stack) return;
        var group = Pools.FirstOrDefault(p => p.IsPool && p.Stack == stack);
        if (group is null) return;

        // Reuse the pool's checkout setup, then point the reuse target at exactly this workspace.
        Checkout(group);
        CheckoutNew = false;
        CheckoutReuse = true;
        CheckoutTarget = CheckoutFreeWorkspaces.FirstOrDefault(w => w.Name == item.Name) ?? CheckoutTarget;
    }

    bool CanRelease => Selected is { Claimed: true } && !Busy;

    /// <summary>Release the selected workspace back to its pool: stop its infra (keeping all disk state) and
    /// mark it free. Cheap and safe — nothing is removed, so a mistaken release is recovered by a keep
    /// re-checkout.</summary>
    [RelayCommand(CanExecute = nameof(CanRelease))]
    private async Task Release()
    {
        var item = Selected;
        if (item is null || !item.Claimed) return;
        await Guard(async () =>
        {
            var (_, pending) = await AppServices.RunAsync(() => Services.Pools.Release(item.Name));
            await RefreshCore();
            Services.NotifyStoreChanged();
            // Surface (never act on) any pending work so it isn't silently forgotten before a later fresh checkout.
            StatusMessage = pending.HasPending
                ? $"released '{item.Name}' — infra stopped, disk kept. Pending: {pending.Summary()}"
                : $"released '{item.Name}' — its infra is stopped; disk state kept";
        }, status: null);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Up() => Lifecycle(ws => Services.Workspaces.Up(ws), "infra up");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Down() => Lifecycle(ws => Services.Workspaces.Down(ws), "infra down");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Reset() => Lifecycle(ws => Services.Workspaces.Reset(ws), "infra reset");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Reconcile()
    {
        var item = Selected;
        if (item is null) return;
        await Guard(async () =>
        {
            var report = await AppServices.RunAsync(() => Services.Reconciler.Inspect(item.Name));
            ApplyDrift(item, report);
        }, status: "reconciled");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Repair()
    {
        var item = Selected;
        if (item is null) return;
        await Guard(async () =>
        {
            await AppServices.RunAsync(() => Services.Reconciler.Repair(item.Name));
            var report = await AppServices.RunAsync(() => Services.Reconciler.Inspect(item.Name));
            ApplyDrift(item, report);
            // Repair rebuilds worktrees — reality changed, so announce it (state-driven surfaces refresh,
            // and a coach step waiting on a healthy workspace advances).
            Services.NotifyStoreChanged();
        }, status: "repaired");
    }

    /// <summary>Open one repo's worktree folder in the OS file manager.</summary>
    [RelayCommand]
    private void OpenRepoInExplorer(RepoLineViewModel? repo)
        => LaunchRepo(repo, p => new ProcessStartInfo { FileName = p, UseShellExecute = true });

    /// <summary>Open one repo's worktree in VS Code (<c>code &lt;path&gt;</c> on the PATH).</summary>
    [RelayCommand]
    private void OpenRepoInVsCode(RepoLineViewModel? repo)
        => LaunchRepo(repo, p => new ProcessStartInfo { FileName = "code", Arguments = $"\"{p}\"", UseShellExecute = true });

    /// <summary>Open a terminal at one repo's worktree (Windows Terminal, else a shell).</summary>
    [RelayCommand]
    private void OpenRepoInTerminal(RepoLineViewModel? repo)
    {
        var path = repo?.WorktreePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                // Prefer Windows Terminal; fall back to PowerShell if wt isn't installed.
                try { Process.Start(new ProcessStartInfo { FileName = "wt.exe", Arguments = $"-d \"{path}\"", UseShellExecute = true }); }
                catch { Process.Start(new ProcessStartInfo { FileName = "powershell.exe", WorkingDirectory = path, UseShellExecute = true }); }
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo { FileName = "open", Arguments = $"-a Terminal \"{path}\"" });
            else
                Process.Start(new ProcessStartInfo { FileName = "x-terminal-emulator", WorkingDirectory = path });
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    /// <summary>Launch a process for a repo's worktree path; surfaces any failure in <see cref="Error"/>.</summary>
    void LaunchRepo(RepoLineViewModel? repo, Func<string, ProcessStartInfo> build)
    {
        var path = repo?.WorktreePath;
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(build(path)); }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RefreshStatus()
    {
        var item = Selected;
        if (item is not null) await LoadStatusAsync(item);
    }

    /// <summary>Launch Docker Desktop so the user can inspect this workspace's <c>sprig-&lt;name&gt;</c> project.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenDocker()
    {
        try
        {
            var exe = DockerDesktopPath();
            if (exe is null)
            {
                Error = "Docker Desktop wasn't found in its default location. Open it manually to see the "
                      + $"'sprig-{Selected?.Name}' project.";
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }
        catch (Exception ex) { Error = $"couldn't open Docker Desktop: {ex.Message}"; }
    }

    /// <summary>Docker Desktop's exe in either Program Files location, or null if not installed there.</summary>
    static string? DockerDesktopPath()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root)) continue;
            var exe = Path.Combine(root, "Docker", "Docker", "Docker Desktop.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove() => ConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    [RelayCommand]
    private async Task ConfirmRemove()
    {
        var item = Selected;
        var force = RemoveForce;
        if (item is null) return;
        ConfirmingRemove = false;

        var record = await AppServices.RunAsync(() => Services.Workspaces.Get(item.Name));
        if (record is null)
        {
            // No record to build a checklist from — fall back to the plain guarded sweep.
            await Guard(async () =>
            {
                await AppServices.RunAsync(() => Services.Workspaces.Remove(item.Name, force));
                await RefreshCore();
                Services.NotifyStoreChanged();
            }, status: $"removed '{item.Name}'");
            return;
        }

        var modal = new OperationProgressViewModel($"Removing workspace '{item.Name}'");
        modal.Load(Services.Workspaces.PlanRemove(record, force));
        OperationStarted?.Invoke(modal);

        Busy = true;
        Error = null;
        StatusMessage = null;
        try
        {
            var progress = new Progress<WorkspaceStepProgress>(modal.Apply);
            await AppServices.RunAsync(() => Services.Workspaces.Remove(item.Name, force, progress));
            await RefreshCore();
            Services.NotifyStoreChanged();

            // Teardown never hard-fails; when a layer couldn't be dismantled the record is kept and
            // flagged, so it's still in the list after the refresh. Distinguish that (retry needed)
            // from a clean sweep (record gone), rather than a bare "some steps needed attention".
            if (await AppServices.RunAsync(() => Services.Workspaces.Get(item.Name)) is { TeardownFailed: true })
            {
                StatusMessage = $"'{item.Name}' teardown incomplete — record kept; retry once fixed";
                modal.Finish(
                    $"Teardown of '{item.Name}' couldn't finish — record kept so you can retry once the flagged steps are fixed.",
                    WorkspaceStepState.Warning);
            }
            else
            {
                StatusMessage = $"removed '{item.Name}'";
                modal.Finish($"Removed '{item.Name}'.", WorkspaceStepState.Done);
            }
        }
        catch (Exception ex)
        {
            // A teardown that threw may have removed some layers before failing — refresh so the list
            // reflects whatever actually happened, not the pre-teardown state.
            await TryRefreshCore();
            Error = ex.Message;
            modal.Finish($"Couldn't remove '{item.Name}': {ex.Message}", WorkspaceStepState.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    async Task Lifecycle(Action<string> action, string ok)
    {
        var name = Selected?.Name;
        if (name is null) return;
        await Guard(async () =>
        {
            await AppServices.RunAsync(() => action(name));
            await RefreshCore();
        }, status: ok);
    }

    static void ApplyDrift(WorkspaceItemViewModel item, WorkspaceReconcile? report)
    {
        if (report is null) { item.Drift = "not checked"; return; }
        foreach (var line in item.Repos)
            line.DriftState = report.Repos.FirstOrDefault(r => r.WorktreePath == line.WorktreePath)?.State;
        item.Drift = report.IsHealthy ? "in sync"
            : report.HasDrift ? "drift detected — run Repair"
            : "worktrees gone";
    }

    Task RefreshCore() => LoadAsync();

    /// <summary>Refresh the list, swallowing any failure. Used on error paths: an operation that failed
    /// part-way (e.g. a checkout that created the workspace but couldn't start its infra) still changed the
    /// store, so the list must be rebuilt to reflect reality — but a failed refresh must not mask the
    /// original error already being surfaced.</summary>
    async Task TryRefreshCore()
    {
        try { await RefreshCore(); } catch { /* keep the original error visible */ }
    }

    async Task Guard(Func<Task> action, string? status)
    {
        Busy = true;
        Error = null;
        StatusMessage = null;
        try
        {
            await action();
            if (status is not null) StatusMessage = status;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
