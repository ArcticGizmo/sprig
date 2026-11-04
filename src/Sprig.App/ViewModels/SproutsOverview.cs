using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Hosting;
using Sprig.Engine.Sprouts;

namespace Sprig.App.ViewModels;

public enum OverviewFilter { All, Up, Failed, Down }

/// <summary>One sprout in the overview's table.</summary>
public sealed partial class OverviewRow(SproutRecord record, Action checkedChanged) : ObservableObject
{
    public SproutRecord Record => record;
    public string Key => record.Key;
    public string Name => record.Name;
    public string Trellis => record.Trellis;
    public string Branch => record.Branch ?? "repos in place";
    public string State => record.State.ToString().ToLowerInvariant();
    public bool IsUp => record.State == SproutState.Up;
    public bool IsFailed => record.State == SproutState.Failed;
    public bool IsDown => record.State == SproutState.Down;

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

/// <summary>A count of sprouts in one state, which filters the table to them.</summary>
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
/// The overview shown when no one sprout is picked: every sprout in a table, ticked to bring up, take down or remove
/// several at once, with a warning before removing any whose worktrees hold work that isn't committed or pushed.
/// </summary>
public partial class SproutsViewModel
{
    List<OverviewRow> _overview = [];
    IReadOnlyDictionary<string, IReadOnlyList<string>> _blockers = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>The clock for "3 days ago"; tests pass their own.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    public ObservableCollection<OverviewRow> OverviewRows { get; } = [];
    public ObservableCollection<OverviewChip> OverviewChips { get; } = [];

    [ObservableProperty] private OverviewFilter _overviewFilter = OverviewFilter.All;
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

    /// <summary>Read every sprout into the table again, keeping what's ticked.</summary>
    void BuildOverview()
    {
        if (OverviewChips.Count == 0)
            foreach (var filter in Enum.GetValues<OverviewFilter>()) OverviewChips.Add(new OverviewChip(filter, PickOverviewChipCommand));

        var ticked = _overview.Where(r => r.IsChecked).Select(r => r.Key).ToHashSet();
        var now = Clock();
        _overview = Sprouts.Select(s => s.Record)
            .OrderBy(r => r.Trellis, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.State switch { SproutState.Failed => 0, SproutState.Up => 1, SproutState.Starting => 2, _ => 3 })
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(record =>
            {
                var row = new OverviewRow(record, CheckedChanged) { IsChecked = ticked.Contains(record.Key) };
                var plan = _services.Sprouts.LastPlan(record.Trellis, record.Name);
                var ports = plan?.Ports.Select(p => p.Port).OfType<int>().ToArray() ?? [];
                row.Ports = ports.Length == 0 ? "" : ports.Min() == ports.Max() ? $"{ports.Min()}" : $"{ports.Min()}–{ports.Max()}";
                row.LastUp = record.State == SproutState.Up ? "now"
                    : record.State == SproutState.Failed ? $"failed {Ago.Text(record.UpdatedAt, now)}"
                    : record.LastUpAt is { } up ? Ago.Text(up, now) : Ago.Text(record.UpdatedAt, now);
                row.DownFor = record.State == SproutState.Down ? now - record.UpdatedAt : TimeSpan.Zero;
                row.Worktrees = record.InPlace ? "in place" : "…";
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
        var trellises = _overview.Select(r => r.Trellis).Distinct().Count();
        OverviewSummary = $"{_overview.Count} sprout{(_overview.Count == 1 ? "" : "s")} of {trellises} trellis{(trellises == 1 ? "" : "es")}"
            + $" · {count(r => r.IsUp)} up · {count(r => r.IsFailed)} failed · {count(r => r.IsDown)} down";
        var stale = _overview.Count(Stale);
        HasStale = stale > 0;
        StaleLabel = stale == 1 ? "Select the 1 down for over a week" : $"Select the {stale} down for over a week";
        FilterOverview();
        CheckedChanged();
    }

    static bool Stale(OverviewRow row) => row.IsDown && row.DownFor > TimeSpan.FromDays(7);

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
            var apps = _services.Sprouts.LastPlan(row.Trellis, row.Name)?.Apps.Count(a => !a.Job) ?? 0;
            var running = runners.Count(r => r.Sprout == row.Key && r.State.IsAlive());
            row.Apps = running == 0 ? "apps stopped" : $"{running} of {apps} running";
        }
    }

    void FilterOverview()
    {
        OverviewRows.Clear();
        foreach (var row in _overview.Where(r => OverviewFilter switch
                 {
                     OverviewFilter.Up => r.IsUp,
                     OverviewFilter.Failed => r.IsFailed,
                     OverviewFilter.Down => r.IsDown,
                     _ => true,
                 }))
            OverviewRows.Add(row);
        foreach (var chip in OverviewChips) chip.IsOn = chip.Filter == OverviewFilter;
    }

    partial void OnOverviewFilterChanged(OverviewFilter value)
    {
        FilterOverview();
        CheckedChanged();
    }

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

    /// <summary>Ask git about every sprout's worktrees, off the UI thread, for the Worktrees column and the warning
    /// before removing.</summary>
    public async Task LoadOverviewStatusAsync()
    {
        var rows = _overview.ToArray();
        var results = await AppServices.RunAsync(() => rows.Select(r => r.Record.InPlace
            ? (r.Key, Trees: (IReadOnlyList<WorktreeStatus>)[], Blockers: (IReadOnlyList<string>)[])
            : (r.Key, Trees: _services.Sprouts.WorktreeStatus(r.Record), Blockers: _services.Sprouts.RemovalBlockers(r.Record, deleteBranch: true))).ToArray());
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

    IReadOnlyList<SproutRecord> CheckedRecords() => _overview.Where(r => r.IsChecked).Select(r => r.Record).ToArray();

    [RelayCommand]
    private Task BulkUp() => RunBulk("Bringing up", CheckedRecords(), (record, progress) =>
    {
        var worktrees = _services.Sprouts.Worktrees(record.TrellisPath, record.Name);
        var loaded = _services.Loader.Load(record.TrellisPath, worktrees);
        if (loaded.Untrusted.Count > 0) throw new InvalidOperationException("it has definitions to approve first; open it and bring it up there");
        if (loaded.Trellis is null || loaded.Problems.Count > 0)
            throw new InvalidOperationException("its trellis can't be planned: " + string.Join("; ", loaded.Problems.Take(2).Select(p => p.Message)));
        _services.Sprouts.Up(loaded, record.Name, record.Branch, progress);
    });

    [RelayCommand]
    private Task BulkDown() => RunBulk("Taking down", CheckedRecords(), (record, progress) => _services.Sprouts.Down(record.Trellis, record.Name, progress));

    [RelayCommand]
    private void AskBulkRemove() => ConfirmingBulkRemove = CheckedCount > 0;

    [RelayCommand]
    private void CancelBulkRemove() => ConfirmingBulkRemove = false;

    [RelayCommand]
    private async Task BulkRemove()
    {
        ConfirmingBulkRemove = false;
        var options = new RemoveOptions(BulkDeleteBranches);
        await RunBulk("Removing", CheckedRecords(), (record, progress) =>
            _services.Sprouts.Remove(record.Trellis, record.Name, options with { DeleteBranch = options.DeleteBranch && !record.InPlace }, progress));
    }

    /// <summary>One sprout after another in one progress window, each step named for its sprout. One failing doesn't stop
    /// the rest; the summary says which failed and why.</summary>
    async Task RunBulk(string verb, IReadOnlyList<SproutRecord> records, Action<SproutRecord, IProgress<SproutEvent>> each)
    {
        if (records.Count == 0) return;
        var failed = new List<string>();
        await RunAsync($"{verb} {records.Count} sprout{(records.Count == 1 ? "" : "s")}", progress =>
        {
            foreach (var record in records)
            {
                var named = new NamedProgress(progress, $"{record.Trellis} / {record.Name}");
                try
                {
                    each(record, named);
                }
                catch (Exception e)
                {
                    failed.Add($"{record.Trellis} / {record.Name}: {e.Message.Split('\n')[0]}");
                }
            }
            if (failed.Count > 0)
                throw new InvalidOperationException($"{records.Count - failed.Count} of {records.Count} done. Not done:\n" + string.Join("\n", failed));
        }, $"All {records.Count} done.");
        ClearChecked();
        Refresh();
    }

    /// <summary>Leads each step's name with its sprout's, so several sprouts' steps read apart in one window.</summary>
    sealed class NamedProgress(IProgress<SproutEvent> inner, string name) : IProgress<SproutEvent>
    {
        public void Report(SproutEvent value) => inner.Report(value with { Step = value.Step.Length > 0 ? $"{name}: {value.Step}" : name });
    }
}
