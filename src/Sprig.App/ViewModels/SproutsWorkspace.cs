using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Logs;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Sprouts;

namespace Sprig.App.ViewModels;

public enum AppStateFilter { All, Ready, Problems, Stopped }

/// <summary>The list's first row: every app's log, interleaved.</summary>
public sealed partial class AllAppsEntry : ObservableObject
{
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private int _count;
}

/// <summary>A group heading in the app list: Pinned, or a repo.</summary>
/// <param name="repo">Null for the Pinned heading, which doesn't fold.</param>
public sealed partial class AppListHeader(string title, string? repo) : ObservableObject
{
    public string Title => title;
    public string? Repo => repo;
    public bool CanFold => repo is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isOpen = true;

    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private bool _captionIsBad;

    public string Chevron => !CanFold ? "" : IsOpen ? "▾" : "▸";
}

/// <summary>An app in the list; a pinned app shows twice, once under Pinned with its repo named.</summary>
public sealed record AppListEntry(SproutAppRow Row, bool InPinned);

/// <summary>A count of the sprout's apps in one state, which filters the list to them.</summary>
public sealed partial class StateChip(AppStateFilter filter, IRelayCommand<StateChip> pick) : ObservableObject
{
    public AppStateFilter Filter => filter;
    public IRelayCommand<StateChip> PickCommand => pick;
    public bool IsAll => filter == AppStateFilter.All;
    public bool IsReadyKind => filter == AppStateFilter.Ready;
    public bool IsProblemKind => filter == AppStateFilter.Problems;
    public bool IsStoppedKind => filter == AppStateFilter.Stopped;

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private bool _isOn;
}

/// <summary>What the details panel shows for the picked app.</summary>
/// <param name="ValuesIn">Where its values are written, e.g. <c>.env.local</c>.</param>
/// <param name="Settings">Each value, secrets masked, with where it came from.</param>
public sealed record AppDetails(string Repo, string Name, string Url, string Folder, string ValuesIn, string StartsAfter,
    string ReadyWhen, string LogFile, IReadOnlyList<SettingRow> Settings)
{
    public bool HasUrl => Url.Length > 0;
    public bool HasSettings => Settings.Count > 0;
}

public enum SproutTab { Apps, Infra }

/// <param name="IsWarn">Something to look at before removing the sprout: changes, unpushed commits, or a missing folder.</param>
public sealed record WorktreeRow(string Repo, string Folder, string Status, bool IsWarn);

/// <param name="IsWarn">The repo doesn't ignore it, so it could be committed by mistake.</param>
public sealed record WrittenFileRow(string Path, string What, string Status, bool IsWarn);

/// <summary>
/// The selected sprout's workspace: the app list (filtered, grouped by repo, with pins), the log it picks, and the
/// details of the picked app.
/// </summary>
public partial class SproutsViewModel
{
    readonly HashSet<string> _closedRepos = new(StringComparer.Ordinal);
    List<string> _pins = [];
    string _trellis = "";
    string _listShape = "";

    public AllAppsEntry AllApps { get; } = new();

    /// <summary>Flat, so it scrolls as one list: the All apps row, headings and app entries.</summary>
    public ObservableCollection<object> ListRows { get; } = [];

    public ObservableCollection<StateChip> Chips { get; } = [];

    [ObservableProperty] private string _appFilter = "";
    [ObservableProperty] private AppStateFilter _stateFilter = AppStateFilter.All;

    /// <summary>The app whose details show; the last one picked, even while every app's log is showing.</summary>
    [ObservableProperty] private SproutAppRow? _pickedApp;

    [ObservableProperty] private AppDetails? _details;
    [ObservableProperty] private bool _detailsOpen = true;

    /// <summary>Whether All apps streams only the pinned apps rather than the ones the list shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamingList))]
    private bool _streamPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnAppsTab), nameof(OnInfraTab))]
    private SproutTab _tab = SproutTab.Apps;

    [ObservableProperty] private string _listCaption = "";
    [ObservableProperty] private string _foldLabel = "Collapse repos";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPins))]
    private int _pinnedCount;

    public bool HasPins => PinnedCount > 0;
    public bool StreamingList => !StreamPinned;
    public bool OnAppsTab => Tab == SproutTab.Apps;
    public bool OnInfraTab => Tab == SproutTab.Infra;
    public string WarningsBadge => Warnings.Count == 1 ? "1 warning" : $"{Warnings.Count} warnings";

    public string LogTitle => Logs.IsAll ? "All apps" : Apps.FirstOrDefault(a => a.Key == Logs.Key)?.Name ?? Logs.Key;
    public string LogSub => Logs.IsAll
        ? StreamPinned ? $"{PinnedCount} pinned apps. Click a name to open its own log." : $"{AllApps.Count} apps, the ones the list shows. Click a name to open its own log."
        : Apps.FirstOrDefault(a => a.Key == Logs.Key)?.StateLine ?? "";
    public SproutAppRow? LogApp => Logs.IsAll ? null : Apps.FirstOrDefault(a => a.Key == Logs.Key);

    void LoadWorkspace(SproutRecord? record, Plan? plan)
    {
        if (Chips.Count == 0)
            foreach (var filter in Enum.GetValues<AppStateFilter>()) Chips.Add(new StateChip(filter, PickChipCommand));

        _trellis = record?.Trellis ?? "";
        _pins = record is null ? [] : [.. _services.Ui.Pins(_trellis)];
        _closedRepos.Clear();
        _listShape = "";
        AppFilter = "";
        StateFilter = AppStateFilter.All;
        Tab = SproutTab.Apps;
        Worktrees = [];
        WrittenFiles = [];
        foreach (var row in Apps) row.IsPinned = _pins.Contains(row.Key);
        PinnedCount = Apps.Count(a => a.IsPinned);
        StreamPinned = false;

        Logs.SetApps(record is null ? [] : Apps.Select(a => (a.Key, HostProtocol.LogFile(_services.Paths, record.Key, a.Key))).ToArray());
        Logs.SetStream(InStream);
        Logs.Show(SproutLogPane.All);
        PickedApp = Apps.FirstOrDefault(a => a.IsProblem) ?? Apps.FirstOrDefault(a => !a.IsJob) ?? Apps.FirstOrDefault();
        MarkSelected();
        RebuildList();
    }

    bool InStream(string app) => StreamPinned ? _pins.Contains(app) : Visible(Apps.FirstOrDefault(a => a.Key == app));

    bool Visible(SproutAppRow? row) => row is not null
        && (AppFilter.Length == 0 || row.Key.Contains(AppFilter, StringComparison.OrdinalIgnoreCase))
        && StateFilter switch
        {
            AppStateFilter.Ready => row.IsReady,
            AppStateFilter.Problems => row.IsProblem,
            AppStateFilter.Stopped => row.IsStoppedOrDone,
            _ => true,
        };

    /// <summary>After the host's answer: counts change every time, the list only when an app moved in or out of view.</summary>
    void StatesChanged()
    {
        if (Chips.Count == 0) return;
        UpdateCounts();
        if (Shape() != _listShape) RebuildList();
        OnPropertyChanged(nameof(LogSub));
    }

    string Shape() => string.Join("|", Apps.Where(Visible).Select(a => a.Key)) + $"#{string.Join(",", _closedRepos)}#{string.Join(",", _pins)}";

    void UpdateCounts()
    {
        foreach (var chip in Chips)
        {
            var n = chip.Filter switch
            {
                AppStateFilter.Ready => Apps.Count(a => a.IsReady),
                AppStateFilter.Problems => Apps.Count(a => a.IsProblem),
                AppStateFilter.Stopped => Apps.Count(a => a.IsStoppedOrDone),
                _ => Apps.Count,
            };
            chip.Label = chip.Filter switch
            {
                AppStateFilter.Ready => $"{n} ready",
                AppStateFilter.Problems => n == 1 ? "1 needs attention" : $"{n} need attention",
                AppStateFilter.Stopped => $"{n} stopped or done",
                _ => $"All {n}",
            };
            chip.IsOn = chip.Filter == StateFilter;
        }
        foreach (var header in ListRows.OfType<AppListHeader>().Where(h => h.Repo is not null)) CaptionRepo(header);
    }

    void CaptionRepo(AppListHeader header)
    {
        var mine = Apps.Where(a => a.Repo == header.Repo).ToArray();
        var bad = mine.Count(a => a.IsProblem);
        header.Caption = bad > 0 ? $"{bad} need{(bad == 1 ? "s" : "")} attention" : $"{mine.Count(a => a.IsReady)} of {mine.Length} ready";
        header.CaptionIsBad = bad > 0;
    }

    void RebuildList()
    {
        _listShape = Shape();
        var visible = Apps.Where(Visible).ToArray();
        ListRows.Clear();
        AllApps.Count = StreamPinned ? PinnedCount : visible.Length;
        AllApps.Caption = StreamPinned ? "pinned apps, interleaved" : "every app the list shows, interleaved";
        ListRows.Add(AllApps);

        var pinned = _pins.Select(k => Apps.FirstOrDefault(a => a.Key == k)).OfType<SproutAppRow>().ToArray();
        if (pinned.Length > 0)
        {
            ListRows.Add(new AppListHeader("PINNED", null) { Caption = pinned.Length.ToString() });
            foreach (var row in pinned) ListRows.Add(new AppListEntry(row, InPinned: true));
        }

        foreach (var repo in visible.Select(a => a.Repo).Distinct())
        {
            var header = new AppListHeader(repo.ToUpperInvariant(), repo) { IsOpen = !_closedRepos.Contains(repo) || AppFilter.Length > 0 };
            CaptionRepo(header);
            ListRows.Add(header);
            if (!header.IsOpen) continue;
            foreach (var row in visible.Where(a => a.Repo == repo)) ListRows.Add(new AppListEntry(row, InPinned: false));
        }

        var what = StateFilter == AppStateFilter.All ? "" : $" ({Chips.First(c => c.Filter == StateFilter).Label.Split(' ', 2)[^1]})";
        ListCaption = $"Showing {visible.Length} of {Apps.Count} apps{what}";
        FoldLabel = _closedRepos.Count > 0 ? "Expand repos" : "Collapse repos";
        UpdateCounts();
        Logs.SetStream(InStream);
        OnPropertyChanged(nameof(LogSub));
    }

    partial void OnAppFilterChanged(string value) => RebuildList();

    partial void OnStateFilterChanged(AppStateFilter value) => RebuildList();

    partial void OnStreamPinnedChanged(bool value) => RebuildList();

    partial void OnPickedAppChanged(SproutAppRow? value) => Details = value is null || _plan is null || Selected is null ? null : DetailsOf(value, Selected.Record, _plan);

    AppDetails? DetailsOf(SproutAppRow row, SproutRecord record, Plan plan)
    {
        if (plan.Apps.FirstOrDefault(a => a.Key == row.Key) is not { } app) return null;
        var after = (app.DependsOn ?? []).Concat(app.WaitFor.Select(w => w.Contains('/') ? w : $"{app.Owner}/{w}")).Distinct()
            .Select(k => k[(k.IndexOf('/') + 1)..]).ToArray();
        var ready = (app.Health ?? []).Select(h => $"{h.Endpoint} answers {h.Path}")
            .Concat(app.ReadyLog is { } log ? [$"a log line matches {log}"] : []).ToArray();
        return new AppDetails(row.Repo, row.App, row.Url, row.Folder, row.Values,
            after.Length > 0 ? string.Join(", ", after) : "nothing",
            ready.Length > 0 ? string.Join(" and ", ready) : app.Job ? "it finishes" : row.Port.Length > 0 && row.Port != "job" ? "its port opens" : "it starts",
            HostProtocol.LogFile(_services.Paths, record.Key, row.Key).Replace('\\', '/'),
            app.Env.Select(e => new SettingRow(e.Key, e.File, e.Value.Value is { } v ? ValueMask.Apply(v) : "unresolved",
                string.Join(" → ", e.Value.Via), e.Value.Value is null)).ToArray());
    }

    void MarkSelected()
    {
        foreach (var row in Apps) row.IsSelected = !Logs.IsAll && row.Key == Logs.Key;
        AllApps.IsSelected = Logs.IsAll;
        OnPropertyChanged(nameof(LogTitle));
        OnPropertyChanged(nameof(LogSub));
        OnPropertyChanged(nameof(LogApp));
    }

    /// <param name="topNow">The log row at the top of the view now, so this log's place is kept.</param>
    public void OpenApp(SproutAppRow row, LogRow? topNow = null)
    {
        Tab = SproutTab.Apps;
        Logs.Show(row.Key, topNow);
        PickedApp = row;
        MarkSelected();
    }

    public void OpenAll(LogRow? topNow = null)
    {
        Tab = SproutTab.Apps;
        Logs.Show(SproutLogPane.All, topNow);
        MarkSelected();
    }

    [RelayCommand]
    private void OpenAppRow(SproutAppRow row) => OpenApp(row);

    [RelayCommand]
    private void OpenAllApps() => OpenAll();

    /// <summary>From a name in the interleaved log.</summary>
    [RelayCommand]
    private void OpenAppOfLine(LogRow line)
    {
        if (Apps.FirstOrDefault(a => a.Key == line.App) is { } row) OpenApp(row);
    }

    [RelayCommand]
    private void TogglePin(SproutAppRow row)
    {
        if (!_pins.Remove(row.Key)) _pins.Add(row.Key);
        row.IsPinned = _pins.Contains(row.Key);
        PinnedCount = _pins.Count;
        if (_trellis.Length > 0) _services.Ui.SetPins(_trellis, _pins);
        if (PinnedCount == 0) StreamPinned = false;
        RebuildList();
    }

    [RelayCommand]
    private void ToggleRepo(AppListHeader header)
    {
        if (header.Repo is not { } repo) return;
        if (!_closedRepos.Remove(repo)) _closedRepos.Add(repo);
        RebuildList();
    }

    [RelayCommand]
    private void ToggleAllRepos()
    {
        if (_closedRepos.Count > 0) _closedRepos.Clear();
        else foreach (var repo in Apps.Select(a => a.Repo).Distinct()) _closedRepos.Add(repo);
        RebuildList();
    }

    [RelayCommand]
    private void PickChip(StateChip chip) => StateFilter = chip.Filter;

    [RelayCommand]
    private void StreamList() => StreamPinned = false;

    [RelayCommand]
    private void StreamPins()
    {
        if (PinnedCount > 0) StreamPinned = true;
    }

    [RelayCommand]
    private void ToggleDetails() => DetailsOpen = !DetailsOpen;

    [RelayCommand]
    private void ShowAppsTab() => Tab = SproutTab.Apps;

    [RelayCommand]
    private void ShowInfraTab() => Tab = SproutTab.Infra;

    [ObservableProperty] private IReadOnlyList<WorktreeRow> _worktrees = [];
    [ObservableProperty] private IReadOnlyList<WrittenFileRow> _writtenFiles = [];
    [ObservableProperty] private bool _infraLoading;

    partial void OnTabChanged(SproutTab value)
    {
        if (value == SproutTab.Infra) _ = LoadInfraAsync();
    }

    /// <summary>Ask git about each worktree and written file, off the UI thread: a few git calls per repo, each a few
    /// hundred milliseconds on Windows, so only when the tab is opened.</summary>
    public async System.Threading.Tasks.Task LoadInfraAsync()
    {
        if (Selected?.Record is not { } record) return;
        InfraLoading = true;
        try
        {
            var (trees, files) = await AppServices.RunAsync(() => (_services.Sprouts.WorktreeStatus(record), _services.Sprouts.FileStatus(record)));
            if (Selected?.Record.Key != record.Key) return;
            Worktrees = trees.Select(t => new WorktreeRow(t.Repo, t.Dir.Replace('\\', '/'), WorktreeText(t), !t.Exists || t.Dirty || t.Unpushed > 0)).ToArray();
            var root = record.Folder ?? Path.GetDirectoryName(record.TrellisPath)!;
            WrittenFiles = files.Select(f => new WrittenFileRow(Path.GetRelativePath(root, f.Path).Replace('\\', '/'), f.Kind,
                f.Ignored ? "ignored by the repo" : f.Tracked ? "tracked: a commit would include it" : f.Repo is null ? "outside the repos" : "not ignored",
                !f.Ignored && f.Repo is not null)).ToArray();
        }
        finally
        {
            InfraLoading = false;
        }
    }

    static string WorktreeText(WorktreeStatus t)
    {
        if (!t.Exists) return "folder missing";
        var parts = new List<string>();
        if (t.Dirty) parts.Add("changes not committed");
        if (t.Unpushed > 0) parts.Add(t.Unpushed == 1 ? "1 unpushed commit" : $"{t.Unpushed} unpushed commits");
        return parts.Count > 0 ? string.Join(" · ", parts) : t.Worktree ? "clean" : "in place, clean";
    }

    /// <summary>Move the log to the app above or below the one showing, in list order.</summary>
    public void Step(int by, LogRow? topNow = null)
    {
        var order = ListRows.OfType<AppListEntry>().Where(e => !e.InPinned).Select(e => e.Row).ToList();
        if (order.Count == 0) return;
        var at = Logs.IsAll ? -1 : order.FindIndex(r => r.Key == Logs.Key);
        var next = at + by;
        if (next < 0) OpenAll(topNow);
        else OpenApp(order[Math.Min(next, order.Count - 1)], topNow);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Details?.Folder is not { Length: > 0 } folder || !Directory.Exists(folder)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder.Replace('/', Path.DirectorySeparatorChar)) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>The picked app's values as <c>KEY=value</c> lines, secrets masked, for the view to put on the clipboard.</summary>
    public string EnvText() => Details is null ? "" : string.Join(Environment.NewLine, Details.Settings.Select(v => $"{v.Key}={v.Value}"));
}
