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
using Sprig.Engine.Workspaces;

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

    /// <summary>The caption in words, since it's only a count.</summary>
    [ObservableProperty] private string? _captionTip;

    public string Chevron => !CanFold ? "" : IsOpen ? "▾" : "▸";
}

/// <summary>An app in the list; a pinned app shows twice, once under Pinned with its repo named.</summary>
public sealed record AppListEntry(WorkspaceAppRow Row, bool InPinned);

/// <summary>A count of the workspace's apps in one state, which filters the list to them.</summary>
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
    string ReadyWhen, string LogFile, IReadOnlyList<SettingRow> Settings, IReadOnlyList<PlannedLink> Links)
{
    public bool HasUrl => Url.Length > 0;
    public bool HasSettings => Settings.Count > 0;
    public bool HasLinks => Links.Count > 0;
}

public enum WorkspaceTab { Apps, Graph, Infra }

/// <param name="IsWarn">Something to look at before removing the workspace: changes, unpushed commits, or a missing folder.</param>
public sealed record WorktreeRow(string Repo, string Folder, string Status, bool IsWarn);

/// <param name="IsWarn">The repo doesn't ignore it, so it could be committed by mistake.</param>
public sealed record WrittenFileRow(string Path, string What, string Status, bool IsWarn);

/// <summary>
/// The selected workspace's apps: the app list (filtered, grouped by repo, with pins), the log it picks, and the
/// details of the picked app.
/// </summary>
public partial class WorkspacesViewModel
{
    readonly HashSet<string> _closedRepos = new(StringComparer.Ordinal);
    List<string> _pins = [];
    string _orchestration = "";
    string _listShape = "";

    public AllAppsEntry AllApps { get; } = new();

    /// <summary>Flat, so it scrolls as one list: the All apps row, headings and app entries.</summary>
    public ObservableCollection<object> ListRows { get; } = [];

    public ObservableCollection<StateChip> Chips { get; } = [];

    [ObservableProperty] private string _appFilter = "";
    [ObservableProperty] private AppStateFilter _stateFilter = AppStateFilter.All;

    /// <summary>The app whose details show; the last one picked, even while every app's log is showing.</summary>
    [ObservableProperty] private WorkspaceAppRow? _pickedApp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GraphShowsAppDetails))]
    private AppDetails? _details;
    [ObservableProperty] private bool _detailsOpen = true;

    /// <summary>Whether All apps streams only the pinned apps rather than the ones the list shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StreamingList))]
    private bool _streamPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnAppsTab), nameof(OnInfraTab), nameof(OnGraphTab))]
    private WorkspaceTab _tab = WorkspaceTab.Apps;

    [ObservableProperty] private string _listCaption = "";
    [ObservableProperty] private string _foldLabel = "Collapse repos";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPins))]
    private int _pinnedCount;

    public bool HasPins => PinnedCount > 0;
    public bool StreamingList => !StreamPinned;
    public bool OnAppsTab => Tab == WorkspaceTab.Apps;
    public bool OnInfraTab => Tab == WorkspaceTab.Infra;
    public string WarningsBadge => Warnings.Count == 1 ? "1 warning" : $"{Warnings.Count} warnings";

    public string LogTitle => Logs.IsAll ? "All apps"
        : Apps.FirstOrDefault(a => a.Key == Logs.Key)?.Name
          ?? (Graph?.Find(Logs.Key) is { } node ? $"{node.Owner} / {node.Name}" : Logs.Key);
    public string LogSub => Logs.IsAll
        ? StreamPinned ? $"{PinnedCount} pinned apps. Click a name to open its own log." : $"{AllApps.Count} apps, the ones the list shows. Click a name to open its own log."
        : Apps.FirstOrDefault(a => a.Key == Logs.Key)?.StateLine
          ?? (Graph?.Find(Logs.Key) is { } node ? $"container · {node.StateLine}" : "");
    public WorkspaceAppRow? LogApp => Logs.IsAll ? null : Apps.FirstOrDefault(a => a.Key == Logs.Key);

    void LoadWorkspace(WorkspaceRecord? record, Plan? plan)
    {
        if (Chips.Count == 0)
            foreach (var filter in Enum.GetValues<AppStateFilter>()) Chips.Add(new StateChip(filter, PickChipCommand));

        // The same workspace read afresh (after bringing it up, say) keeps where the user was; another one starts clean.
        // A slot keeps its key from one branch to the next, so a new branch in it counts as another workspace.
        var same = record is not null && record.Key == _workspaceKey && record.Branch == _workspaceBranch;
        _workspaceKey = record?.Key;
        _workspaceBranch = record?.Branch;
        // Its logs are read from the same files, so the lines already read would otherwise stay.
        if (!same) Logs.SetApps([]);
        _orchestration = record?.Orchestration ?? "";
        _pins = record is null ? [] : [.. _services.Ui.Pins(_orchestration)];
        _listShape = "";
        if (!same)
        {
            _closedRepos.Clear();
            AppFilter = "";
            StateFilter = AppStateFilter.All;
            Tab = WorkspaceTab.Apps;
            Worktrees = [];
            WrittenFiles = [];
        }
        if (!same) ClearMarks();
        foreach (var row in Apps) row.IsPinned = _pins.Contains(row.Key);
        PinnedCount = Apps.Count(a => a.IsPinned);
        if (!same || PinnedCount == 0) StreamPinned = false;

        BuildGraph(record, plan, same);
        Logs.SetApps(record is null ? [] : Apps.Select(a => (a.Key, HostProtocol.LogFile(_services.Paths, record.Key, a.Key))).ToArray(), InfraLogSources());
        Logs.SetStream(InStream);
        var picked = PickedApp?.Key;
        if (!same) Logs.Show(WorkspaceLogPane.All);
        PickedApp = (same ? Apps.FirstOrDefault(a => a.Key == picked) : null)
            ?? Apps.FirstOrDefault(a => a.IsProblem) ?? Apps.FirstOrDefault(a => !a.IsJob) ?? Apps.FirstOrDefault();
        MarkSelected();
        RebuildList();
    }

    string? _workspaceKey;
    string? _workspaceBranch;

    bool InStream(string app) => StreamPinned ? _pins.Contains(app) : Visible(Apps.FirstOrDefault(a => a.Key == app));

    bool Visible(WorkspaceAppRow? row) => row is not null
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
        var ready = mine.Count(a => a.IsReady);
        header.Caption = $"{ready}/{mine.Length}";
        header.CaptionIsBad = bad > 0;
        header.CaptionTip = $"{ready} of {mine.Length} ready" + (bad > 0 ? $", {bad} need{(bad == 1 ? "s" : "")} attention" : "");
    }

    void RebuildList()
    {
        _listShape = Shape();
        var visible = Apps.Where(Visible).ToArray();
        ListRows.Clear();
        AllApps.Count = StreamPinned ? PinnedCount : visible.Length;
        AllApps.Caption = StreamPinned ? "pinned apps, interleaved" : "every app the list shows, interleaved";
        ListRows.Add(AllApps);

        var pinned = _pins.Select(k => Apps.FirstOrDefault(a => a.Key == k)).OfType<WorkspaceAppRow>().ToArray();
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
        _showingCaption = $"Showing {visible.Length} of {Apps.Count} apps{what}";
        // An app filtered or folded out of sight can't be seen to be marked, so a menu shouldn't act on it.
        var listed = ListRows.OfType<AppListEntry>().Select(e => e.Row.Key).ToHashSet(StringComparer.Ordinal);
        _marked.RemoveAll(key => !listed.Contains(key));
        ShowMarks();
        FoldLabel = _closedRepos.Count > 0 ? "Expand repos" : "Collapse repos";
        UpdateCounts();
        Logs.SetStream(InStream);
        OnPropertyChanged(nameof(LogSub));
    }

    partial void OnAppFilterChanged(string value) => RebuildList();

    partial void OnStateFilterChanged(AppStateFilter value) => RebuildList();

    partial void OnStreamPinnedChanged(bool value) => RebuildList();

    partial void OnPickedAppChanged(WorkspaceAppRow? value) => Details = value is null || _plan is null || Selected is null ? null : DetailsOf(value, Selected.Record, _plan);

    AppDetails? DetailsOf(WorkspaceAppRow row, WorkspaceRecord record, Plan plan)
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
            app.Env.Select(e => new SettingRow(e.Key, e.File is { } file ? app.FileLabel(file) : null, e.Value.Value is { } v ? SecretValues.Show(plan, e, v) : "unresolved",
                string.Join(" → ", e.Value.Via), e.Value.Value is null)).ToArray(),
            Heard(app.Links ?? []));
    }

    /// <summary>Links with a main checkout's app ports filled in, as the apps said them; one still waiting for an app
    /// to say is left out until it does.</summary>
    IReadOnlyList<PlannedLink> Heard(IReadOnlyList<PlannedLink> links)
        => links.Any(l => HeardPort.In(l.Url))
            ? links.Select(l => !HeardPort.In(l.Url) ? l
                    : HeardPort.Fill(l.Url, key => Apps.FirstOrDefault(a => a.Key == key)?.HeardPort) is { } url ? l with { Url = url } : null)
                .OfType<PlannedLink>().ToArray()
            : links;

    /// <summary>What the app's menu offers to open: its own url, then the quick links its details list.</summary>
    public IReadOnlyList<PlannedLink> LinksOf(WorkspaceAppRow row)
    {
        var links = Heard(_plan?.Apps.FirstOrDefault(a => a.Key == row.Key)?.Links ?? []);
        if (row.Url.Length == 0) return links;
        var label = Uri.TryCreate(row.Url, UriKind.Absolute, out var uri) ? uri.Authority : row.Url;
        return [new PlannedLink(label, row.Url), .. links];
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
    public void OpenApp(WorkspaceAppRow row, LogRow? topNow = null)
    {
        Tab = WorkspaceTab.Apps;
        Logs.Show(row.Key, topNow);
        PickedApp = row;
        MarkSelected();
    }

    public void OpenAll(LogRow? topNow = null)
    {
        Tab = WorkspaceTab.Apps;
        Logs.Show(WorkspaceLogPane.All, topNow);
        MarkSelected();
    }

    /// <summary>A plain click, which lets go of any marked apps, as it would in a file list.</summary>
    [RelayCommand]
    private void OpenAppRow(WorkspaceAppRow row)
    {
        ClearMarks();
        OpenApp(row);
    }

    [RelayCommand]
    private void OpenAllApps()
    {
        ClearMarks();
        OpenAll();
    }

    // ---- marking several apps, for one menu to act on them all ----

    readonly List<string> _marked = [];
    string? _markAnchor;
    string _showingCaption = "";

    /// <summary>In the order they were marked; empty when none are.</summary>
    public IReadOnlyList<WorkspaceAppRow> MarkedApps => _marked.Select(k => Apps.FirstOrDefault(a => a.Key == k)).OfType<WorkspaceAppRow>().ToArray();

    public bool HasMarks => _marked.Count > 0;

    /// <summary>Ctrl+click. The first one takes the app whose log is open with it, since that row already looks picked.</summary>
    public void ToggleMark(WorkspaceAppRow row)
    {
        if (_marked.Count == 0 && LogApp is { } open && open.Key != row.Key) _marked.Add(open.Key);
        if (!_marked.Remove(row.Key)) _marked.Add(row.Key);
        _markAnchor = row.Key;
        ShowMarks();
    }

    /// <summary>Shift+click: every app listed from the one last clicked to <paramref name="to"/>.</summary>
    public void MarkRange(AppListEntry to)
    {
        var entries = ListRows.OfType<AppListEntry>().ToList();
        var end = entries.IndexOf(to);
        var anchor = _markAnchor ?? LogApp?.Key;
        // A pinned app is listed twice; the range runs from its entry in the same part of the list as the click.
        var start = entries.FindIndex(e => e.Row.Key == anchor && e.InPinned == to.InPinned);
        if (start < 0) start = entries.FindIndex(e => e.Row.Key == anchor);
        if (start < 0 || end < 0)
        {
            ToggleMark(to.Row);
            return;
        }
        _marked.Clear();
        foreach (var entry in entries.GetRange(Math.Min(start, end), Math.Abs(end - start) + 1))
            if (!_marked.Contains(entry.Row.Key)) _marked.Add(entry.Row.Key);
        ShowMarks();
    }

    public void ClearMarks()
    {
        _marked.Clear();
        _markAnchor = null;
        ShowMarks();
    }

    void ShowMarks()
    {
        foreach (var row in Apps) row.IsMarked = _marked.Contains(row.Key);
        ListCaption = _marked.Count > 0 ? $"{_marked.Count} marked · right-click for all, Esc clears" : _showingCaption;
        OnPropertyChanged(nameof(HasMarks));
        OnPropertyChanged(nameof(MarkedApps));
    }

    /// <summary>The marked apps that aren't going, each started after what it depends on.</summary>
    [RelayCommand]
    private Task StartMarked() => StartAsync(MarkedApps.Where(a => !a.IsAlive).Select(a => a.Key).ToArray());

    [RelayCommand]
    private Task RestartMarked() => StartAsync(MarkedApps.Where(a => a.IsAlive).Select(a => a.Key).ToArray());

    [RelayCommand]
    private Task StopMarked() => StopAsync(MarkedApps.Where(a => a.IsAlive).ToArray());

    /// <summary>Pins every marked app, or, when they're all pinned already, unpins them.</summary>
    [RelayCommand]
    private void PinMarked()
    {
        var marked = MarkedApps;
        if (marked.Any(a => !a.IsPinned))
            _pins.AddRange(marked.Where(a => !_pins.Contains(a.Key)).Select(a => a.Key));
        else
            _pins.RemoveAll(key => marked.Any(a => a.Key == key));
        PinsChanged();
    }

    /// <summary>From a name in the interleaved log.</summary>
    [RelayCommand]
    private void OpenAppOfLine(LogRow line)
    {
        if (Apps.FirstOrDefault(a => a.Key == line.App) is { } row) OpenApp(row);
    }

    /// <summary>From a line in one app's log: what every app was doing when it was written.</summary>
    public void OpenAllAt(LogRow line, LogRow? topNow = null)
    {
        Tab = WorkspaceTab.Apps;
        Logs.ShowAt(WorkspaceLogPane.All, line, topNow);
        MarkSelected();
    }

    /// <summary>From a line in the interleaved log: the same line in its app's own log, with what came either side of it.</summary>
    public void OpenAppAt(LogRow line, LogRow? topNow = null)
    {
        if (Apps.FirstOrDefault(a => a.Key == line.App) is not { } row) return;
        Tab = WorkspaceTab.Apps;
        Logs.ShowAt(row.Key, line, topNow);
        PickedApp = row;
        MarkSelected();
    }

    [RelayCommand]
    private void ToggleLogTimes() => _services.Ui.LogTimes = Logs.ShowTimes = !Logs.ShowTimes;

    [RelayCommand]
    private void ToggleLogWrap() => _services.Ui.LogWrap = Logs.Wrap = !Logs.Wrap;

    [RelayCommand]
    private void TogglePin(WorkspaceAppRow row)
    {
        if (!_pins.Remove(row.Key)) _pins.Add(row.Key);
        PinsChanged();
    }

    void PinsChanged()
    {
        foreach (var row in Apps) row.IsPinned = _pins.Contains(row.Key);
        PinnedCount = _pins.Count;
        if (_orchestration.Length > 0) _services.Ui.SetPins(_orchestration, _pins);
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

    bool _narrow;

    /// <summary>Whether the details are wanted with room for them. Never kept: a folded-away panel is easily forgotten,
    /// so each workspace opens with them showing.</summary>
    bool _detailsWanted = true;

    [RelayCommand]
    private void ToggleDetails()
    {
        DetailsOpen = !DetailsOpen;
        if (!_narrow) _detailsWanted = DetailsOpen;
    }

    /// <summary>Show the details, as when a node is double-clicked in the graph.</summary>
    void OpenDetails()
    {
        DetailsOpen = true;
        if (!_narrow) _detailsWanted = true;
    }

    /// <summary>Each workspace opens with its details showing, room allowing.</summary>
    void ResetDetails()
    {
        _detailsWanted = true;
        DetailsOpen = !_narrow;
    }

    /// <summary>A window too narrow for the details beside the log folds them away, and opening them there (over the
    /// log) is only for now; widening it again puts back what was chosen with room for both.</summary>
    public void FitDetails(bool narrow)
    {
        _narrow = narrow;
        DetailsOpen = !narrow && _detailsWanted;
    }

    [RelayCommand]
    private void ShowAppsTab() => Tab = WorkspaceTab.Apps;

    [RelayCommand]
    private void ShowInfraTab() => Tab = WorkspaceTab.Infra;

    [ObservableProperty] private IReadOnlyList<WorktreeRow> _worktrees = [];
    [ObservableProperty] private IReadOnlyList<WrittenFileRow> _writtenFiles = [];
    [ObservableProperty] private bool _infraLoading;

    partial void OnTabChanged(WorkspaceTab value)
    {
        if (value == WorkspaceTab.Infra) _ = LoadInfraAsync();
    }

    /// <summary>Ask git about each worktree and written file, off the UI thread: a few git calls per repo, each a few
    /// hundred milliseconds on Windows, so only when the tab is opened.</summary>
    public async System.Threading.Tasks.Task LoadInfraAsync()
    {
        if (Selected?.Record is not { } record) return;
        InfraLoading = true;
        try
        {
            var (trees, files) = await AppServices.RunAsync(() => (_services.Workspaces.WorktreeStatus(record), _services.Workspaces.FileStatus(record)));
            if (Selected?.Record.Key != record.Key) return;
            Worktrees = trees.Select(t => new WorktreeRow(t.Repo, t.Dir.Replace('\\', '/'), WorktreeText(t), !t.Exists || t.Dirty || t.Unpushed > 0)).ToArray();
            var root = record.Folder ?? Path.GetDirectoryName(record.OrchestrationPath)!;
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
}
