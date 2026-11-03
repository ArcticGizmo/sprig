using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Hosting;

namespace Sprig.App.ViewModels;

/// <param name="Number">The line's place in the log, from 1, so a filtered view still says where each line was.</param>
public sealed record LogLine(int Number, string Text, IReadOnlyList<AnsiRun> Runs)
{
    public bool IsSprig => Text.StartsWith("sprig: ", StringComparison.Ordinal);
}

/// <summary>
/// One runner's log, read as it grows. Keeps the last <see cref="MaxLines"/> lines, since a chatty dev server can write
/// millions; the file on disk keeps everything. A search shows only the lines containing it.
/// </summary>
public sealed partial class LogPaneViewModel : ViewModelBase
{
    public const int MaxLines = 20_000;

    readonly List<LogLine> _all = [];
    LogTail? _tail;
    int _read;

    /// <summary>What's shown: every kept line, or those matching <see cref="Search"/>.</summary>
    public ObservableCollection<LogLine> Lines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private string? _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileShown))]
    private string? _file;

    /// <summary>With forward slashes, like every path the app shows.</summary>
    public string? FileShown => File?.Replace('\\', '/');
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _follow = true;
    [ObservableProperty] private string _summary = "";

    public bool IsOpen => Title is not null;

    /// <summary>Raised after lines are added to <see cref="Lines"/>, so the view can keep the newest in sight.</summary>
    public event Action? LinesAdded;

    public void Open(string title, string file)
    {
        if (File == file) return;
        Title = title;
        File = file;
        _tail = new LogTail(file);
        _all.Clear();
        _read = 0;
        Lines.Clear();
        UpdateSummary();
    }

    [RelayCommand]
    private void Close()
    {
        Title = null;
        File = null;
        _tail = null;
        _all.Clear();
        Lines.Clear();
    }

    /// <summary>Read what the log has gained; the file is read off the UI thread.</summary>
    public async Task PollAsync()
    {
        if (_tail is not { } tail) return;
        var generation = tail.Generation;
        var lines = await Task.Run(tail.ReadNew);
        if (!ReferenceEquals(tail, _tail)) return;
        if (tail.Generation != generation)
        {
            // The runner was started afresh: its log now holds only the new run.
            _all.Clear();
            _read = 0;
            Lines.Clear();
        }
        Append(lines);
    }

    public void Append(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        var added = new List<LogLine>(lines.Count);
        foreach (var text in lines)
        {
            _read++;
            added.Add(new LogLine(_read, AnsiText.Strip(text), AnsiText.Runs(text)));
        }
        _all.AddRange(added);

        if (_all.Count > MaxLines)
        {
            // Trimmed in one go, with room to spare, so a busy log isn't trimmed on every read.
            _all.RemoveRange(0, _all.Count - MaxLines + MaxLines / 10);
            Rebuild();
        }
        else
        {
            foreach (var line in added.Where(Matches)) Lines.Add(line);
        }
        UpdateSummary();
        LinesAdded?.Invoke();
    }

    partial void OnSearchChanged(string value)
    {
        Rebuild();
        UpdateSummary();
    }

    bool Matches(LogLine line) => Search.Length == 0 || line.Text.Contains(Search, StringComparison.OrdinalIgnoreCase);

    void Rebuild()
    {
        Lines.Clear();
        foreach (var line in _all.Where(Matches)) Lines.Add(line);
    }

    void UpdateSummary()
    {
        var kept = _all.Count < _read ? $" (the last {_all.Count:N0} kept here; the file has them all)" : "";
        Summary = Search.Length == 0
            ? $"{_read:N0} lines{kept}"
            : $"{Lines.Count:N0} of {_all.Count:N0} lines match{kept}";
    }
}
