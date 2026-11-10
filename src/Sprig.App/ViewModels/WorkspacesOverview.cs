using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Hosting;
using Sprig.Engine.Workspaces;

namespace Sprig.App.ViewModels;

public enum OverviewFilter { All, Up, Failed, Down }

/// <summary>One workspace in the overview's table.</summary>
public sealed partial class OverviewRow(WorkspaceRecord record, Action checkedChanged) : ObservableObject
{
    public WorkspaceRecord Record => record;
    public string Key => record.Key;
    public string Name => WorkspaceItem.Display(record);
    public string Orchestration => record.Orchestration;
    public string Branch => WorkspaceItem.BranchOf(record);
    public string State => record.IsFree ? "free" : record.State.ToString().ToLowerInvariant();
    public bool IsUp => record.State == WorkspaceState.Up;
    public bool IsFailed => record.State == WorkspaceState.Failed;
    public bool IsDown => record.State == WorkspaceState.Down;

    [ObservableProperty] private string _apps = "";
    [ObservableProperty] private string _ports = "";
    [ObservableProperty] private string _worktrees = "…";
    [ObservableProperty] private bool _worktreesWarn;
    [ObservableProperty] private string _lastUp = "";

    /// <summary>How long it's been down, for "down for over a week"; zero when it isn't down.</summary>
    public TimeSpan DownFor { get; set; }

    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => checkedChanged();
}

/// <summary>A count of workspaces in one state, which filters the table to them.</summary>
public sealed partial class OverviewChip(OverviewFilter filter, IRelayCommand<OverviewChip> pick) : ObservableObject
{
    public OverviewFilter Filter => filter;
    public IRelayCommand<OverviewChip> PickCommand => pick;
    public bool IsUpKind => filter == OverviewFilter.Up;
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
                row.LastUp = record.State == WorkspaceState.Up ? "now"
                    : IsUnmade(record) ? "never"
                    : record.State == WorkspaceState.Failed ? $"failed {Ago.Text(record.UpdatedAt, now)}"
                    : record.LastUpAt is { } up ? Ago.Text(up, now) : Ago.Text(record.UpdatedAt, now);
                row.DownFor = record.State == WorkspaceState.Down && !IsUnmade(record) ? now - record.UpdatedAt : TimeSpan.Zero;
                row.Worktrees = record.IsMain ? "main checkout" : record.InPlace ? "in place" : "…";
                return row;
            }).ToList();
        CountOverviewApps();

        var count = (Func<OverviewRow, bool> f) => _overview.Count(f);
        foreach (var chip in OverviewChips)
        {
            chip.Label = chip.Filter switch
            {
                OverviewFilter.Up => $"{count(r => r.IsUp)} up",
                OverviewFilter.Failed => $"{count(r => r.IsFailed)} failed",
                OverviewFilter.Down => $"{count(r => r.IsDown)} down",
                _ => $"All {_overview.Count}",
            };
            chip.IsOn = chip.Filter == OverviewFilter;
        }
        var orchestrations = _overview.Select(r => r.Orchestration).Distinct().Count();
        OverviewSummary = $"{_overview.Count} workspace{(_overview.Count == 1 ? "" : "s")} of {orchestrations} orchestration{(orchestrations == 1 ? "" : "s")}"
            + $" · {count(r => r.IsUp)} up · {count(r => r.IsFailed)} failed · {count(r => r.IsDown)} down";
        var stale = _overview.Count(Stale);
        HasStale = stale > 0;
        StaleLabel = stale == 1 ? "Select the 1 down for over a week" : $"Select the {stale} down for over a week";
        FilterOverview();
        CheckedChanged();
    }

    /// <summary>Down long enough to be worth removing; main is always there, so it never is.</summary>
    static bool Stale(OverviewRow row) => row.IsDown && !row.Record.IsMain && row.DownFor > TimeSpan.FromDays(7);

    void CountOverviewApps()
    {
        var runners = _lastRunners ?? [];
        foreach (var row in _overview)
        {
            if (!row.IsUp)
            {
                row.Apps = "—";
                continue;
            }
            var apps = _services.Workspaces.LastPlan(row.Orchestration, row.Record.Name)?.Apps.Count(a => !a.Job) ?? 0;
            var running = runners.Count(r => r.Workspace == row.Key && r.State.IsAlive());
            row.Apps = running == 0 ? "apps stopped" : $"{running} of {apps} running";
        }
    }

    void FilterOverview()
    {
        var words = OverviewSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Matches(OverviewRow r) => words.All(w =>
            r.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
            || r.Orchestration.Contains(w, StringComparison.OrdinalIgnoreCase)
            || r.Branch.Contains(w, StringComparison.OrdinalIgnoreCase));

        OverviewRows.Clear();
        foreach (var row in _overview.Where(r => Matches(r) && OverviewFilter switch
                 {
                     OverviewFilter.Up => r.IsUp,
                     OverviewFilter.Failed => r.IsFailed,
                     OverviewFilter.Down => r.IsDown,
                     _ => true,
                 }))
            OverviewRows.Add(row);
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
        foreach (var row in _overview) row.IsChecked = Stale(row);
    }

    [RelayCommand]
    private void OpenOverviewRow(OverviewRow row) => Show(row.Key);

    [RelayCommand]
    private void BackToList() => Show(null);

    IReadOnlyList<WorkspaceRecord> CheckedRecords() => _overview.Where(r => r.IsChecked).Select(r => r.Record).ToArray();

    [RelayCommand]
    private Task BulkUp() => RunBulk("Bringing up", CheckedRecords(), (record, progress, cancellation) =>
    {
        var worktrees = _services.Workspaces.Worktrees(record.OrchestrationPath, record.Name);
        var loaded = _services.Loader.Load(record.OrchestrationPath, worktrees);
        if (loaded.Untrusted.Count > 0) throw new InvalidOperationException("it has definitions to approve first; open it and bring it up there");
        if (loaded.Orchestration is null || loaded.Problems.Count > 0)
            throw new InvalidOperationException("its orchestration can't be planned: " + string.Join("; ", loaded.Problems.Take(2).Select(p => p.Message)));
        _services.Workspaces.Up(loaded, record.Name, null, progress: progress, cancellation: cancellation);
    });

    [RelayCommand]
    private Task BulkDown() => RunBulk("Taking down", CheckedRecords().Where(r => !IsUnmade(r)).ToArray(), (record, progress, cancellation) =>
        _services.Workspaces.Down(record.Orchestration, record.Name, progress, cancellation));

    /// <summary>Free each ticked slot that has a branch, keeping its worktrees warm for the next one; free slots and
    /// in-place workspaces are passed over.</summary>
    [RelayCommand]
    private Task BulkFree() => RunBulk("Freeing", CheckedRecords().Where(r => !r.InPlace && !r.IsFree).ToArray(), (record, progress, cancellation) =>
        _services.Workspaces.Free(record.Orchestration, record.Name, progress, cancellation));

    [RelayCommand]
    private void AskBulkRemove() => ConfirmingBulkRemove = CheckedCount > 0;

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
