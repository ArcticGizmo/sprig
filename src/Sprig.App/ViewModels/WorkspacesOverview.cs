using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Git;
using Sprig.Engine.Hosting;
using Sprig.Engine.Workspaces;

namespace Sprig.App.ViewModels;

public enum OverviewFilter { All, Running, Failed, Stopped }

/// <summary>One workspace in the overview's table.</summary>
public sealed partial class OverviewRow(WorkspaceRecord record, Action checkedChanged) : ObservableObject
{
    public WorkspaceRecord Record => record;
    public string Key => record.Key;
    public string Name => WorkspaceItem.Display(record);
    public string Orchestration => record.Orchestration;
    public string Branch => WorkspaceItem.BranchOf(record);

    /// <summary>From what's running (see <see cref="WorkspaceService.RunState"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFailed), nameof(IsStopped), nameof(IsPaused), nameof(Shown), nameof(IsUp),
        nameof(IsComingUp), nameof(IsInTrouble))]
    private string _state = WorkspaceService.RunState(record, [], null);

    /// <summary>How far its apps have got while it runs, which colours it apart from <see cref="State"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Shown), nameof(IsUp), nameof(IsComingUp), nameof(IsInTrouble))]
    private AppsReadiness _readiness = AppsReadiness.Nothing;

    public bool IsRunning => State == "running";
    public bool IsFailed => State == "failed";
    public bool IsPaused => State == "paused";

    /// <summary>The word shown: <see cref="State"/>, or starting while its apps are on their way up.</summary>
    public string Shown => Readiness.Word(State);

    public bool IsUp => Readiness.Up(State);
    public bool IsComingUp => Readiness.ComingUp(State);
    public bool IsInTrouble => Readiness.InTrouble(State);

    /// <summary>Paused counts as stopped: none of its apps is going.</summary>
    public bool IsStopped => State is "stopped" or "paused";

    [ObservableProperty] private string _apps = "";

    /// <summary>Its apps' and containers' CPU and memory added up, for finding the workspace that's weighing the machine
    /// down; empty when nothing of it runs.</summary>
    [ObservableProperty] private string _usage = "";

    /// <summary>The same, apps and infra apart; null for no tooltip at all.</summary>
    [ObservableProperty] private string? _usageTip;

    /// <summary>Its total, for the summary of them all.</summary>
    public ResourceUsage? Total { get; set; }

    [ObservableProperty] private string _ports = "";
    [ObservableProperty] private string _worktrees = "…";
    [ObservableProperty] private bool _worktreesWarn;
    [ObservableProperty] private string _lastUp = "";

    /// <summary>When it was last started, stopped or made ready; null for a main never made ready.</summary>
    public DateTimeOffset? LastUsed { get; set; }

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => checkedChanged();

    /// <summary>What Tidy up found of its branch; empty until it's asked, or when there's nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTidy))]
    private string _tidy = "";

    [ObservableProperty] private string? _tidyTip;
    [ObservableProperty] private bool _tidyWarn;

    /// <summary>Freeing it loses nothing.</summary>
    [ObservableProperty] private bool _tidyCanGo;

    public bool HasTidy => Tidy.Length > 0;

    public void ShowTidy(SlotTidiness? found)
    {
        TidyCanGo = found?.CanGo == true;
        TidyWarn = found is { Fate: BranchFate.Gone } || found is { Fate: BranchFate.Merged, Dirty: true };
        Tidy = found switch
        {
            null => "",
            { Fate: BranchFate.Merged, Dirty: true } => "merged, but has changes",
            { Fate: BranchFate.Merged } => "merged",
            { Fate: BranchFate.Gone } => "remote deleted, not merged",
            _ => "",
        };
        TidyTip = found is null || Tidy.Length == 0 ? null : string.Join("\n", found.Repos.Select(c => c.Fate switch
        {
            BranchFate.Merged => $"{c.Into}: has everything on it",
            BranchFate.Gone => $"{c.Into}: is missing some of it, and its remote branch was deleted",
            BranchFate.Empty => $"{c.Into}: nothing committed on it here",
            _ => $"{c.Into}: is missing some of it",
        }));
    }
}

/// <summary>A count of workspaces in one state, which filters the table to them.</summary>
public sealed partial class OverviewChip(OverviewFilter filter, IRelayCommand<OverviewChip> pick) : ObservableObject
{
    public OverviewFilter Filter => filter;
    public IRelayCommand<OverviewChip> PickCommand => pick;
    public bool IsUpKind => filter == OverviewFilter.Running;
    public bool IsFailedKind => filter == OverviewFilter.Failed;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _isOn;
}

/// <summary>
/// The overview shown when no one workspace is picked: every workspace in a table, ticked to bring up, take down or remove
/// several at once, with a warning before removing any whose worktrees hold work that isn't committed or pushed.
/// </summary>
public partial class WorkspacesViewModel
{
    List<OverviewRow> _overview = [];
    IReadOnlyDictionary<string, IReadOnlyList<string>> _blockers = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The clock for "3 days ago"; tests pass their own.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    public ObservableCollection<OverviewRow> OverviewRows { get; } = [];

    /// <summary><see cref="OverviewRows"/> split in two, since a main runs the checkout as it is and a slot is sprig's
    /// own.</summary>
    public ObservableCollection<OverviewRow> OverviewMainRows { get; } = [];
    public ObservableCollection<OverviewRow> OverviewSlotRows { get; } = [];
    public bool HasOverviewMainRows => OverviewMainRows.Count > 0;
    public bool HasOverviewSlotRows => OverviewSlotRows.Count > 0;

    public ObservableCollection<OverviewChip> OverviewChips { get; } = [];

    [ObservableProperty] private OverviewFilter _overviewFilter = OverviewFilter.All;

    /// <summary>Words that each have to appear in a workspace's name, orchestration or branch for it to be listed.</summary>
    [ObservableProperty] private string _overviewSearch = "";

    /// <summary>There are workspaces, but the state chip and the search between them leave none listed.</summary>
    [ObservableProperty] private bool _noOverviewMatches;
    [ObservableProperty] private string _overviewSummary = "";
    [ObservableProperty] private int _checkedCount;
    [ObservableProperty] private string _guardNote = "";
    [ObservableProperty] private bool _allChecked;
    [ObservableProperty] private string _staleLabel = "";
    [ObservableProperty] private bool _hasStale;
    [ObservableProperty] private bool _confirmingBulkRemove;
    [ObservableProperty] private bool _bulkDeleteBranches;

    public bool HasChecked => CheckedCount > 0;
    public bool NoneChecked => CheckedCount == 0;
    public string CheckedLabel => $"{CheckedCount} selected";

    /// <summary>Main is always there, so it's never counted for removing; only a slot with a branch can be freed.</summary>
    int Removable => _overview.Count(r => r.IsChecked && !r.Record.IsMain);
    int MainsChecked => _overview.Count(r => r.IsChecked && r.Record.IsMain);

    public bool CanBulkRemove => Removable > 0;
    public bool CanBulkFree => _overview.Any(r => r.IsChecked && !r.Record.InPlace && !r.Record.IsFree);
    public string BulkRemoveTitle => Removable == 1 ? "Remove 1 workspace?" : $"Remove {Removable} workspaces?";
    public string MainsPassedOver => MainsChecked switch
    {
        0 => "",
        1 => "The main checkout you ticked stays: it's always there.",
        var n => $"The {n} main checkouts you ticked stay: they're always there.",
    };

    partial void OnCheckedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(NoneChecked));
        OnPropertyChanged(nameof(CheckedLabel));
    }

    /// <summary>Read every workspace into the table again, keeping what's ticked.</summary>
    void BuildOverview()
    {
        if (OverviewChips.Count == 0)
            foreach (var filter in Enum.GetValues<OverviewFilter>()) OverviewChips.Add(new OverviewChip(filter, PickOverviewChipCommand));

        var ticked = _overview.Where(r => r.IsChecked).Select(r => r.Key).ToHashSet();
        var now = Clock();
        _overview = Workspaces.Select(s => s.Record)
            .OrderBy(r => r.Orchestration, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.IsMain ? 0 : 1)
            .ThenBy(r => r.State switch { WorkspaceState.Failed => 0, WorkspaceState.Up => 1, WorkspaceState.Starting => 2, _ => 3 })
            // Slots by number, so storefront--10 comes after storefront--2.
            .ThenBy(r => WorkspaceService.IsSlot(r.Name) ? int.Parse(r.Name, System.Globalization.CultureInfo.InvariantCulture) : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(record =>
            {
                var row = new OverviewRow(record, CheckedChanged) { IsChecked = ticked.Contains(record.Key) };
                var plan = _services.Workspaces.LastPlan(record.Orchestration, record.Name);
                var ports = plan?.Ports.Select(p => p.Port).OfType<int>().ToArray() ?? [];
                row.Ports = ports.Length == 0 ? "" : ports.Min() == ports.Max() ? $"{ports.Min()}" : $"{ports.Min()}–{ports.Max()}";
                row.LastUsed = IsUnmade(record) ? null : record.LastUpAt is { } up && up > record.UpdatedAt ? up : record.UpdatedAt;
                row.Worktrees = record.IsMain ? "the checkout, as it is" : record.InPlace ? "in place" : "…";
                // Still true only while the slot holds the branch that was checked.
                row.ShowTidy(_tidy.GetValueOrDefault(record.Key) is { } found && found.Branch == record.Branch ? found : null);
                return row;
            }).ToList();
        _overviewNow = now;
        CountOverviewApps();
        var stale = _overview.Count(r => Stale(r, now));
        HasStale = stale > 0;
        StaleLabel = stale == 1 ? "Select the 1 unused for over a week" : $"Select the {stale} unused for over a week";
        FilterOverview();
        CheckedChanged();
        CountTidy();
    }

    /// <summary>Stopped and untouched long enough to be worth removing; main is always there, so it never is.</summary>
    static bool Stale(OverviewRow row, DateTimeOffset now)
        => row.IsStopped && !row.Record.IsMain && row.LastUsed is { } used && now - used > TimeSpan.FromDays(7);

    string _overviewCounts = "";
    DateTimeOffset _overviewNow;

    /// <summary>What each workspace is running and using, from the host's last answer and docker's last measure, then the
    /// chips and summary that count them. Called each time either changes, so a row's state follows what runs.</summary>
    void CountOverviewApps()
    {
        var runners = _lastRunners ?? [];
        var projects = RunningProjects();
        foreach (var row in _overview)
        {
            row.State = WorkspaceService.RunState(row.Record, runners, projects);
            var plan = _services.Workspaces.LastPlan(row.Orchestration, row.Record.Name);
            row.Readiness = AppsReadiness.Of(row.Record, plan, runners);
            row.LastUp = row.IsRunning ? "now"
                : row.LastUsed is not { } used ? "never"
                : row.IsFailed ? $"failed {Ago.Text(used, _overviewNow)}"
                : Ago.Text(used, _overviewNow);

            var apps = ResourceUsage.Sum(runners.Where(r => r.Workspace == row.Key).Select(r => r.Usage));
            var infra = _services.InfraUsage.Of(row.Record);
            row.Total = ResourceUsage.Sum([apps, infra]);
            row.Usage = row.Total?.Text ?? "";
            row.UsageTip = row.Total is null ? null
                : $"Apps: {apps?.Line ?? "none running"}\nInfra: {(row.Record.IsMain ? "yours, not measured" : infra?.Line ?? "none running")}";

            var count = plan?.Apps.Count(a => !a.Job) ?? 0;
            var ready = row.Readiness;
            row.Apps = ready.Started > 0 ? $"{ready.Ready} of {ready.Total} ready" + (ready.Down.Count > 0 ? $", {ready.Down.Count} down" : "")
                : row.Record.Paused is { } paused ? $"{paused.Apps.Count} of {count} paused"
                : row.IsRunning ? "apps stopped" : "—";
        }

        int Count(Func<OverviewRow, bool> f) => _overview.Count(f);
        foreach (var chip in OverviewChips)
        {
            chip.Label = chip.Filter switch
            {
                OverviewFilter.Running => $"{Count(r => r.IsRunning)} running",
                OverviewFilter.Failed => $"{Count(r => r.IsFailed)} failed",
                OverviewFilter.Stopped => $"{Count(r => r.IsStopped)} stopped",
                _ => $"All {_overview.Count}",
            };
            chip.IsOn = chip.Filter == OverviewFilter;
        }
        var orchestrations = _overview.Select(r => r.Orchestration).Distinct().Count();
        _overviewCounts = $"{_overview.Count} workspace{(_overview.Count == 1 ? "" : "s")} of {orchestrations} orchestration{(orchestrations == 1 ? "" : "s")}"
            + $" · {Count(r => r.IsRunning)} running · {Count(r => r.IsFailed)} failed · {Count(r => r.IsStopped)} stopped";
        SummariseOverview();
        // A state chip's rows change as things start and stop; listed again only when which rows it picks has.
        if (OverviewFilter != OverviewFilter.All && !_overview.Where(PassesFilter).Where(Matches).SequenceEqual(OverviewRows)) FilterOverview();
    }

    /// <summary>The counts, and what everything sprig runs is using between them.</summary>
    void SummariseOverview()
        => OverviewSummary = ResourceUsage.Sum(_overview.Select(r => r.Total)) is { } total
            ? $"{_overviewCounts} · using {ResourceUsage.CpuText(total.Cpu)} CPU, {ResourceUsage.MemoryText(total.Memory)}"
            : _overviewCounts;

    /// <summary>Every word searched for is in its name, orchestration or branch.</summary>
    bool Matches(OverviewRow r) => OverviewSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(w =>
        r.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
        || r.Orchestration.Contains(w, StringComparison.OrdinalIgnoreCase)
        || r.Branch.Contains(w, StringComparison.OrdinalIgnoreCase));

    bool PassesFilter(OverviewRow r) => OverviewFilter switch
    {
        OverviewFilter.Running => r.IsRunning,
        OverviewFilter.Failed => r.IsFailed,
        OverviewFilter.Stopped => r.IsStopped,
        _ => true,
    };

    void FilterOverview()
    {
        OverviewRows.Clear();
        foreach (var row in _overview.Where(r => Matches(r) && PassesFilter(r)))
            OverviewRows.Add(row);
        OverviewMainRows.Clear();
        OverviewSlotRows.Clear();
        foreach (var row in OverviewRows) (row.Record.IsMain ? OverviewMainRows : OverviewSlotRows).Add(row);
        OnPropertyChanged(nameof(HasOverviewMainRows));
        OnPropertyChanged(nameof(HasOverviewSlotRows));
        NoOverviewMatches = _overview.Count > 0 && OverviewRows.Count == 0;
        foreach (var chip in OverviewChips) chip.IsOn = chip.Filter == OverviewFilter;
    }

    partial void OnOverviewFilterChanged(OverviewFilter value)
    {
        FilterOverview();
        CheckedChanged();
    }

    partial void OnOverviewSearchChanged(string value)
    {
        FilterOverview();
        CheckedChanged();
    }

    [RelayCommand]
    private void ClearOverviewSearch() => OverviewSearch = "";

    void CheckedChanged()
    {
        CheckedCount = _overview.Count(r => r.IsChecked);
        AllChecked = OverviewRows.Count > 0 && OverviewRows.All(r => r.IsChecked);
        OnPropertyChanged(nameof(CanBulkRemove));
        OnPropertyChanged(nameof(CanBulkFree));
        OnPropertyChanged(nameof(BulkRemoveTitle));
        OnPropertyChanged(nameof(MainsPassedOver));
        UpdateGuardNote();
    }

    void UpdateGuardNote()
    {
        var held = _overview.Where(r => r.IsChecked && _blockers.TryGetValue(r.Key, out var b) && b.Count > 0).Select(r => r.Name).ToArray();
        GuardNote = held.Length == 0 ? ""
            : $"{string.Join(", ", held)} {(held.Length == 1 ? "has" : "have")} work that isn't committed or pushed; removing skips {(held.Length == 1 ? "it" : "them")} and says why.";
    }

    /// <summary>Ask git about every workspace's worktrees, off the UI thread, for the Worktrees column and the warning
    /// before removing.</summary>
    public async Task LoadOverviewStatusAsync()
    {
        var rows = _overview.ToArray();
        var results = await AppServices.RunAsync(() => rows.Select(r => r.Record.InPlace
            ? (r.Key, Trees: (IReadOnlyList<WorktreeStatus>)[], Blockers: (IReadOnlyList<string>)[])
            : (r.Key, Trees: _services.Workspaces.WorktreeStatus(r.Record), Blockers: _services.Workspaces.RemovalBlockers(r.Record, deleteBranch: true))).ToArray());
        _blockers = results.ToDictionary(r => r.Key, r => r.Blockers);
        foreach (var (key, trees, _) in results)
        {
            if (_overview.FirstOrDefault(r => r.Key == key) is not { } row || row.Record.InPlace) continue;
            var changed = trees.Count(t => t.Dirty);
            var unpushed = trees.Sum(t => t.Unpushed);
            var missing = trees.Count(t => !t.Exists);
            row.Worktrees = trees.Count == 0 ? "—"
                : missing > 0 ? $"{missing} missing"
                : changed > 0 ? $"{changed} of {trees.Count} {(changed == 1 ? "has" : "have")} changes"
                : unpushed > 0 ? (unpushed == 1 ? "1 unpushed commit" : $"{unpushed} unpushed commits")
                : "all clean";
            row.WorktreesWarn = missing + changed + unpushed > 0;
        }
        UpdateGuardNote();
    }

    [RelayCommand]
    private void PickOverviewChip(OverviewChip chip) => OverviewFilter = chip.Filter;

    [RelayCommand]
    private void ToggleAllChecked()
    {
        var to = !AllChecked;
        foreach (var row in OverviewRows) row.IsChecked = to;
    }

    [RelayCommand]
    private void ClearChecked()
    {
        foreach (var row in _overview) row.IsChecked = false;
    }

    [RelayCommand]
    private void SelectStale()
    {
        foreach (var row in _overview) row.IsChecked = Stale(row, Clock());
    }

    IReadOnlyDictionary<string, SlotTidiness> _tidy = new Dictionary<string, SlotTidiness>();

    /// <summary>Tidy up is checking: fetching every slot's repos, then looking at each branch.</summary>
    [ObservableProperty] private bool _tidying;

    /// <summary>What the last Tidy up found, in a line; empty before one has run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTidySummary))]
    private string _tidySummary = "";

    public bool HasTidySummary => TidySummary.Length > 0;

    [ObservableProperty] private bool _hasTidyPicks;
    [ObservableProperty] private string _tidyPickLabel = "";

    public string TidyButtonText => Tidying ? "Checking…" : "Tidy up";

    partial void OnTidyingChanged(bool value) => OnPropertyChanged(nameof(TidyButtonText));

    /// <summary>
    /// Fetch every slot's repos and see which branches are merged or had their remote deleted, so the slots done with can
    /// be ticked and freed. Only on asking: fetching is a round trip per repo, too slow to run on every refresh.
    /// </summary>
    [RelayCommand]
    private async Task TidyUp()
    {
        if (Tidying) return;
        Tidying = true;
        try
        {
            var records = _overview.Select(r => r.Record).ToArray();
            TidyReport? report = null;
            await RunAsync("Tidying up", (progress, cancellation) => report = _services.Workspaces.TidyUp(records, progress, cancellation),
                () => TidyMessage(report!));
            if (report is not null) ShowTidyReport(report);
        }
        finally
        {
            Tidying = false;
        }
    }

    /// <summary>The progress window's last word: what was found, and what to do next.</summary>
    internal static string TidyMessage(TidyReport report)
    {
        var free = report.Slots.Count(s => s.CanGo);
        var look = report.Slots.Count(s => s.Fate == BranchFate.Gone || s is { Fate: BranchFate.Merged, Dirty: true });
        return TidySummaryOf(report)
            + (free > 0 ? $"\nClose this and use “Select the {free} merged” to tick {(free == 1 ? "it" : "them")}, then Free or Remove." : "")
            + (look > 0 ? $"\n{look} marked in amber {(look == 1 ? "needs" : "need")} a look before freeing." : "")
            + (free + look == 0 ? "\nNothing to tidy." : "");
    }

    static string TidySummaryOf(TidyReport report)
    {
        var merged = report.Slots.Count(s => s.Fate == BranchFate.Merged);
        var gone = report.Slots.Count(s => s.Fate == BranchFate.Gone);
        return (report.Slots.Count == 0 ? "No slots have a branch to check."
                : merged + gone == 0 ? $"Checked {report.Slots.Count}: every branch is still in use."
                : $"Checked {report.Slots.Count}: {merged} merged, {gone} with the remote deleted but not merged.")
            + (report.Unfetched.Count == 0 ? "" : $" Couldn't reach the remote of {string.Join(", ", report.Unfetched)}, so those may be out of date.");
    }

    public void ShowTidyReport(TidyReport report)
    {
        _tidy = report.Slots.ToDictionary(s => s.Key);
        foreach (var row in _overview)
            row.ShowTidy(_tidy.GetValueOrDefault(row.Key) is { } found && found.Branch == row.Record.Branch ? found : null);
        CountTidy();
        TidySummary = TidySummaryOf(report);
    }

    void CountTidy()
    {
        var count = _overview.Count(r => r.TidyCanGo);
        HasTidyPicks = count > 0;
        TidyPickLabel = count == 1 ? "Select the 1 merged" : $"Select the {count} merged";
    }

    /// <summary>Tick the slots whose branches are merged, ready to free or remove.</summary>
    [RelayCommand]
    private void SelectTidy()
    {
        foreach (var row in _overview) row.IsChecked = row.TidyCanGo;
    }

    [RelayCommand]
    private void OpenOverviewRow(OverviewRow row) => Show(row.Key);

    [RelayCommand]
    private void BackToList() => Show(null);

    IReadOnlyList<WorkspaceRecord> CheckedRecords() => _overview.Where(r => r.IsChecked).Select(r => r.Record).ToArray();

    /// <summary>Start everything in each ticked workspace: made ready, its infra started, then its apps, each after what
    /// it depends on. Free slots have nothing to start, so they're passed over.</summary>
    [RelayCommand]
    private Task BulkStart() => RunBulk("Starting", CheckedRecords().Where(r => !r.IsFree).ToArray(), (record, progress, cancellation) =>
    {
        var worktrees = _services.Workspaces.Worktrees(record.OrchestrationPath, record.Name);
        var loaded = _services.Loader.Load(record.OrchestrationPath, worktrees);
        if (loaded.Untrusted.Count > 0) throw new InvalidOperationException("it has definitions to approve first; open it and start it there");
        if (loaded.Orchestration is null || loaded.Problems.Count > 0)
            throw new InvalidOperationException("its orchestration can't be planned: " + string.Join("; ", loaded.Problems.Take(2).Select(p => p.Message)));
        var ready = _services.Workspaces.Up(loaded, record.Name, null, progress: progress, cancellation: cancellation, starting: Starting.All);
        if (_services.Workspaces.LastPlan(ready.Orchestration, ready.Name) is not { } plan || plan.Apps.Count == 0) return;
        var answer = _services.Host.Send(new HostRequest("start", RunnerSpecs.For(ready, plan), Restart: []));
        if (!answer.Ok) throw new InvalidOperationException(answer.Error ?? "the host wouldn't start the apps");
    });

    /// <summary>Stop everything in each ticked workspace, apps and infra, keeping the containers and their data.</summary>
    [RelayCommand]
    private Task BulkStop() => RunBulk("Stopping", CheckedRecords().Where(r => !IsUnmade(r)).ToArray(), (record, progress, cancellation) =>
        _services.Workspaces.Down(record.Orchestration, record.Name, progress, cancellation));

    /// <summary>Free each ticked slot that has a branch, keeping its worktrees warm for the next one; free slots and
    /// in-place workspaces are passed over.</summary>
    [RelayCommand]
    private Task BulkFree() => RunBulk("Freeing", CheckedRecords().Where(r => !r.InPlace && !r.IsFree).ToArray(), (record, progress, cancellation) =>
        _services.Workspaces.Free(record.Orchestration, record.Name, progress, cancellation));

    [RelayCommand]
    private void AskBulkRemove() => ConfirmingBulkRemove = CanBulkRemove;

    [RelayCommand]
    private void CancelBulkRemove() => ConfirmingBulkRemove = false;

    [RelayCommand]
    private async Task BulkRemove()
    {
        ConfirmingBulkRemove = false;
        var options = new RemoveOptions(BulkDeleteBranches);
        // Main is always there, so removing passes over it.
        await RunBulk("Removing", CheckedRecords().Where(r => !r.IsMain).ToArray(), (record, progress, cancellation) =>
            _services.Workspaces.Remove(record.Orchestration, record.Name, options with { DeleteBranch = options.DeleteBranch && !record.InPlace },
                progress, cancellation));
    }

    /// <summary>One workspace after another in one progress window, each step named for its workspace. One failing doesn't stop
    /// the rest, but a cancel does; the summary says which failed and why.</summary>
    async Task RunBulk(string verb, IReadOnlyList<WorkspaceRecord> records, Action<WorkspaceRecord, IProgress<WorkspaceEvent>, CancellationToken> each)
    {
        if (records.Count == 0) return;
        var failed = new List<string>();
        await RunAsync($"{verb} {records.Count} workspace{(records.Count == 1 ? "" : "s")}", (progress, cancellation) =>
        {
            foreach (var record in records)
            {
                cancellation.ThrowIfCancellationRequested();
                var named = new NamedProgress(progress, WorkspaceItem.Display(record));
                try
                {
                    each(record, named, cancellation);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    failed.Add($"{WorkspaceItem.Display(record)}: {e.Message.Split('\n')[0]}");
                }
            }
            if (failed.Count > 0)
                throw new InvalidOperationException($"{records.Count - failed.Count} of {records.Count} done. Not done:\n" + string.Join("\n", failed));
        }, $"All {records.Count} done.");
        ClearChecked();
        Refresh();
    }

    /// <summary>Leads each step's name with its workspace's, so several workspaces' steps read apart in one window.</summary>
    sealed class NamedProgress(IProgress<WorkspaceEvent> inner, string name) : IProgress<WorkspaceEvent>
    {
        public void Report(WorkspaceEvent value) => inner.Report(value with { Step = value.Step.Length > 0 ? $"{name}: {value.Step}" : name });
    }
}
