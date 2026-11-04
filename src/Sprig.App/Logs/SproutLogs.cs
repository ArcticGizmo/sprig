using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sprig.Engine.Hosting;

namespace Sprig.App.Logs;

/// <summary>One line of one app's log, as shown.</summary>
/// <param name="Seq">Its place in the order lines reached this store, across every app, so a view can hold its place or count
/// what's arrived since it paused.</param>
/// <param name="Number">Its place in its app's log, from 1, so a filtered view still says where each line was.</param>
/// <param name="Text">Without colour codes.</param>
/// <param name="Time">For ordering across apps: the host's time, or for a line written without one, the time of the line
/// before it in the same log.</param>
public sealed record SproutLogLine(long Seq, string App, int Number, DateTimeOffset Time, bool Timed, string Text,
    IReadOnlyList<AnsiRun> Runs, LineLevel Level)
{
    public bool IsSprig => Text.StartsWith("sprig: ", StringComparison.Ordinal);
    public bool IsProblem => Level is LineLevel.Warning or LineLevel.Error;
}

/// <summary>What one poll changed.</summary>
/// <param name="Added">New lines, in the order they now sit in <see cref="SproutLogs.Merged"/>.</param>
/// <param name="Restarted">Apps whose log was started afresh: their earlier lines are gone.</param>
/// <param name="InOrder">The added lines all went on the end of the merged list (nothing was slotted in earlier, trimmed
/// or removed), so a view of it can append rather than rebuild.</param>
/// <param name="TrimmedApps">Apps whose own list lost its oldest lines to the cap.</param>
public sealed record SproutLogChange(IReadOnlyList<SproutLogLine> Added, IReadOnlySet<string> Restarted, bool InOrder,
    IReadOnlySet<string> TrimmedApps)
{
    public bool IsEmpty => Added.Count == 0 && Restarted.Count == 0;
}

/// <summary>
/// Every app's log for one sprout, read as they grow: a list per app, and one list of every app's lines in time order.
/// Both are capped (the files keep everything), since twenty apps at a chatty dev server's rate would otherwise fill
/// memory. Files are read off the UI thread; the lists change only on the thread that awaits <see cref="PollAsync"/>.
/// </summary>
public sealed class SproutLogs
{
    public const int PerAppCap = 5_000;
    public const int MergedCap = 20_000;
    public const int MaxLinesPerRead = 2_000;

    sealed class Source(string app, string file)
    {
        public string App => app;
        public LogTail Tail { get; } = new(file) { MaxLinesPerRead = SproutLogs.MaxLinesPerRead };
        public List<SproutLogLine> Lines { get; } = [];
        public int Read;
        public DateTimeOffset LastTime = DateTimeOffset.MinValue;
    }

    readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    readonly List<SproutLogLine> _merged = [];
    readonly int _perAppCap;
    readonly int _mergedCap;
    long _seq;
    bool _polling;

    public SproutLogs(int perAppCap = PerAppCap, int mergedCap = MergedCap)
    {
        _perAppCap = perAppCap;
        _mergedCap = mergedCap;
    }

    public IReadOnlyList<SproutLogLine> Merged => _merged;

    /// <summary>How many lines arrived in all, trimmed ones included.</summary>
    public long Total => _seq;

    public IReadOnlyCollection<string> Apps => _sources.Keys;

    public IReadOnlyList<SproutLogLine> For(string app) => _sources.TryGetValue(app, out var s) ? s.Lines : [];

    /// <summary>How many lines an app's log has had, including any trimmed from its list.</summary>
    public int ReadCount(string app) => _sources.TryGetValue(app, out var s) ? s.Read : 0;

    public string? FileOf(string app) => _sources.TryGetValue(app, out var s) ? s.Tail.File : null;

    /// <summary>Follow these apps' logs: ones already followed keep what they've read, ones no longer listed are
    /// dropped along with their lines.</summary>
    public void SetApps(IEnumerable<(string App, string File)> apps)
    {
        var wanted = apps.ToDictionary(a => a.App, a => a.File, StringComparer.Ordinal);
        var dropped = _sources.Where(s => !wanted.TryGetValue(s.Key, out var file) || file != s.Value.Tail.File).Select(s => s.Key).ToHashSet();
        foreach (var app in dropped) _sources.Remove(app);
        if (dropped.Count > 0) _merged.RemoveAll(l => dropped.Contains(l.App));
        foreach (var (app, file) in wanted)
            if (!_sources.ContainsKey(app)) _sources[app] = new Source(app, file);
    }

    /// <summary>Read what every log has gained. A call while another is still reading returns nothing.</summary>
    public async Task<SproutLogChange> PollAsync()
    {
        if (_polling) return new SproutLogChange([], new HashSet<string>(), true, new HashSet<string>());
        _polling = true;
        try
        {
            var sources = _sources.Values.ToArray();
            var reads = await Task.Run(() => sources.Select(s =>
            {
                var generation = s.Tail.Generation;
                var entries = s.Tail.ReadNew();
                return (Source: s, Restarted: s.Tail.Generation != generation,
                    Parsed: entries.Select(e => (e, AnsiText.Strip(e.Text), AnsiText.Runs(e.Text))).ToArray());
            }).ToArray());
            return Apply(reads.Where(r => _sources.TryGetValue(r.Source.App, out var now) && ReferenceEquals(now, r.Source))
                .Select(r => (r.Source, r.Restarted, r.Parsed)).ToArray());
        }
        finally
        {
            _polling = false;
        }
    }

    SproutLogChange Apply(IReadOnlyList<(Source Source, bool Restarted, (LogEntry Entry, string Text, IReadOnlyList<AnsiRun> Runs)[] Lines)> reads)
    {
        var restarted = new HashSet<string>(StringComparer.Ordinal);
        var trimmed = new HashSet<string>(StringComparer.Ordinal);
        var inOrder = true;

        foreach (var read in reads.Where(r => r.Restarted))
        {
            restarted.Add(read.Source.App);
            read.Source.Lines.Clear();
            read.Source.Read = 0;
            read.Source.LastTime = DateTimeOffset.MinValue;
        }
        if (restarted.Count > 0 && _merged.RemoveAll(l => restarted.Contains(l.App)) > 0) inOrder = false;

        var fresh = new List<SproutLogLine>();
        foreach (var (source, _, lines) in reads)
        {
            foreach (var (entry, text, runs) in lines)
            {
                var time = entry.Time ?? source.LastTime;
                source.LastTime = time;
                var line = new SproutLogLine(++_seq, source.App, ++source.Read, time, entry.Time is not null, text, runs, LogLevels.Of(text, runs));
                source.Lines.Add(line);
                fresh.Add(line);
            }
            if (source.Lines.Count > _perAppCap)
            {
                // Trimmed in one go with room to spare, so a busy log isn't trimmed on every read.
                source.Lines.RemoveRange(0, source.Lines.Count - _perAppCap + _perAppCap / 10);
                trimmed.Add(source.App);
            }
        }

        // Each log is in order already; across logs, order by time, keeping arrival order for equal times.
        fresh.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Seq.CompareTo(b.Seq));
        foreach (var line in fresh)
        {
            if (_merged.Count == 0 || _merged[^1].Time <= line.Time)
            {
                _merged.Add(line);
                continue;
            }
            inOrder = false;
            var at = _merged.Count - 1;
            while (at > 0 && _merged[at - 1].Time > line.Time) at--;
            _merged.Insert(at, line);
        }
        if (_merged.Count > _mergedCap)
        {
            _merged.RemoveRange(0, _merged.Count - _mergedCap + _mergedCap / 10);
            inOrder = false;
        }

        return new SproutLogChange(fresh, restarted, inOrder, trimmed);
    }
}
