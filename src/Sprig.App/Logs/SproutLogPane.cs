using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Controls;
using Sprig.App.ViewModels;
using Sprig.Engine.Hosting;

namespace Sprig.App.Logs;

/// <summary>A log line as the pane shows it.</summary>
/// <param name="Label">The app's short name, shown before each line when every app is interleaved.</param>
/// <param name="LabelBrush">The app's colour.</param>
public sealed record LogRow(SproutLogLine Line, string Label, IBrush LabelBrush) : IAnsiLine
{
    public string App => Line.App;
    public long Seq => Line.Seq;
    public string Time => Line.Timed ? Line.Time.LocalDateTime.ToString("HH:mm:ss.fff") : "";
    public string Text => Line.Text;
    public IReadOnlyList<AnsiRun> Runs => Line.Runs;
    public bool IsSprig => Line.IsSprig;
    public bool IsError => Line.Level == LineLevel.Error;
    public bool IsWarning => Line.Level == LineLevel.Warning;
}

/// <summary>What one log was left showing: put back when it's shown again.</summary>
public sealed class LogViewState
{
    public string Search { get; set; } = "";
    public bool ProblemsOnly { get; set; }
    public bool Follow { get; set; } = true;

    /// <summary>The line at the top of the view when it was left, while not following.</summary>
    public long? AnchorSeq { get; set; }
}

/// <summary>
/// The log pane: every app's lines interleaved (<see cref="All"/>), or one app's. Each log keeps its own search, filter,
/// pause and place, so going to another and coming back finds it as it was.
/// </summary>
public sealed partial class SproutLogPane : ViewModelBase
{
    public const string All = "";

    static readonly IBrush[] Colours =
        [.. new[] { "#60A5FA", "#4ADE80", "#FBBF24", "#F472B6", "#A78BFA", "#22D3EE", "#FB923C", "#A3E635", "#E879F9", "#2DD4BF" }
            .Select(c => (IBrush)new ImmutableSolidColorBrush(Color.Parse(c)))];

    readonly SproutLogs _logs;
    readonly Dictionary<string, LogViewState> _states = new(StringComparer.Ordinal);
    IReadOnlyDictionary<string, string> _labels = new Dictionary<string, string>();
    IReadOnlyDictionary<string, IBrush> _colours = new Dictionary<string, IBrush>();
    Func<string, bool> _inStream = _ => true;
    bool _loading;

    public SproutLogPane(SproutLogs? logs = null) => _logs = logs ?? new SproutLogs();

    /// <summary>Replaced whole on a rebuild, so the list redraws once rather than once per line; appended to otherwise.</summary>
    [ObservableProperty] private ObservableCollection<LogRow> _lines = [];

    /// <summary>"" for every app interleaved, else the app's <c>repo/app</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsOne), nameof(FileShown))]
    private string _key = All;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _problemsOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    private bool _follow = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PausedText))]
    private int _newCount;

    [ObservableProperty] private string _summary = "";

    public bool IsAll => Key == All;
    public bool IsOne => !IsAll;
    public bool IsPaused => !Follow;
    public string PausedText => NewCount == 1 ? "Paused · 1 new line · Jump to latest" : $"Paused · {NewCount:N0} new lines · Jump to latest";
    public string? FileShown => IsAll ? null : _logs.FileOf(Key)?.Replace('\\', '/');

    public string LabelOf(string app) => _labels.GetValueOrDefault(app, app);

    /// <summary>Set by the view: the row at the top of the log now, kept as a log's place when another is shown.</summary>
    public Func<LogRow?>? TopRow { get; set; }

    /// <summary>The view should show the end of the log.</summary>
    public event Action? ScrollToEnd;

    /// <summary>The view should bring this row to the top, putting back where a log was left.</summary>
    public event Action<LogRow>? ScrollTo;

    /// <summary>Follow these apps' logs, in this order (which sets their colours).</summary>
    public void SetApps(IReadOnlyList<(string App, string File)> apps)
    {
        _logs.SetApps(apps);
        _labels = LogLabels.Shortest(apps.Select(a => a.App));
        _colours = apps.Select((a, i) => (a.App, Colour: Colours[i % Colours.Length])).ToDictionary(x => x.App, x => x.Colour);
        foreach (var gone in _states.Keys.Where(k => k != All && !_labels.ContainsKey(k)).ToArray()) _states.Remove(gone);
        if (!IsAll && !_labels.ContainsKey(Key)) Show(All);
        else Rebuild();
    }

    /// <summary>Which apps the interleaved view shows.</summary>
    public void SetStream(Func<string, bool> inStream)
    {
        _inStream = inStream;
        if (IsAll) Rebuild();
    }

    /// <param name="topNow">The row at the top of the view now, kept as this log's place if it isn't following.</param>
    public void Show(string key, LogRow? topNow = null)
    {
        topNow ??= Follow ? null : TopRow?.Invoke();
        if (!Follow && topNow is not null) State(Key).AnchorSeq = topNow.Seq;
        var state = State(key);
        _loading = true;
        try
        {
            Key = key;
            Search = state.Search;
            ProblemsOnly = state.ProblemsOnly;
            Follow = state.Follow;
            NewCount = 0;
        }
        finally
        {
            _loading = false;
        }
        Rebuild();
        if (!state.Follow && state.AnchorSeq is { } seq && Lines.FirstOrDefault(r => r.Seq >= seq) is { } row) ScrollTo?.Invoke(row);
        else ScrollToEnd?.Invoke();
    }

    /// <summary>Read what the logs have gained and show what this view wants of it.</summary>
    public async Task PollAsync()
    {
        var change = await _logs.PollAsync();
        if (change.IsEmpty) return;
        var mine = IsAll
            ? change.InOrder && change.Restarted.Count == 0
            : !change.Restarted.Contains(Key) && !change.TrimmedApps.Contains(Key);
        if (!mine)
        {
            Rebuild();
            if (Follow) ScrollToEnd?.Invoke();
            return;
        }
        var added = 0;
        foreach (var line in change.Added.Where(Wanted))
        {
            Lines.Add(Row(line));
            added++;
        }
        if (added == 0) return;
        UpdateSummary();
        if (Follow) ScrollToEnd?.Invoke();
        else NewCount += added;
    }

    /// <summary>The user scrolled up from the end (pause) or back down to it (follow again).</summary>
    public void SetFollowing(bool following, LogRow? topNow = null)
    {
        if (following == Follow) return;
        Follow = following;
        if (!following && topNow is not null) State(Key).AnchorSeq = topNow.Seq;
    }

    [RelayCommand]
    private void JumpToLatest()
    {
        Follow = true;
        ScrollToEnd?.Invoke();
    }

    partial void OnSearchChanged(string value)
    {
        if (_loading) return;
        State(Key).Search = value;
        Rebuild();
    }

    partial void OnProblemsOnlyChanged(bool value)
    {
        if (_loading) return;
        State(Key).ProblemsOnly = value;
        Rebuild();
    }

    partial void OnFollowChanged(bool value)
    {
        if (_loading) return;
        var state = State(Key);
        state.Follow = value;
        if (value)
        {
            NewCount = 0;
            state.AnchorSeq = null;
        }
    }

    LogViewState State(string key)
    {
        if (!_states.TryGetValue(key, out var state)) _states[key] = state = new LogViewState();
        return state;
    }

    bool Wanted(SproutLogLine line)
        => (IsAll ? _inStream(line.App) : line.App == Key)
           && (!ProblemsOnly || line.IsProblem)
           && (Search.Length == 0 || line.Text.Contains(Search, StringComparison.OrdinalIgnoreCase));

    LogRow Row(SproutLogLine line) => new(line, LabelOf(line.App), _colours.GetValueOrDefault(line.App, Colours[0]));

    void Rebuild()
    {
        Lines = new ObservableCollection<LogRow>((IsAll ? _logs.Merged : _logs.For(Key)).Where(Wanted).Select(Row));
        UpdateSummary();
    }

    void UpdateSummary()
    {
        var filtered = Search.Length > 0 || ProblemsOnly;
        if (IsAll)
        {
            var apps = _labels.Keys.Count(_inStream);
            Summary = filtered
                ? $"{Lines.Count:N0} of {_logs.Merged.Count(l => _inStream(l.App)):N0} lines match, from {apps} apps"
                : $"{Lines.Count:N0} lines from {apps} apps (the newest {SproutLogs.MergedCap:N0} are kept here; each file has them all)";
            return;
        }
        var read = _logs.ReadCount(Key);
        var kept = _logs.For(Key).Count;
        var note = kept < read ? $" (the last {kept:N0} kept here; the file has them all)" : "";
        Summary = filtered ? $"{Lines.Count:N0} of {kept:N0} lines match{note}" : $"{read:N0} lines{note}";
    }
}
