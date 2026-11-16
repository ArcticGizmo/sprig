using System;
using System.Collections.Generic;
using System.Linq;
using Sprig.Engine.Hosting;

namespace Sprig.App;

/// <summary>One series' last <see cref="UsageHistory.Span"/>, a value per <see cref="UsageHistory.Step"/>, oldest first and
/// ending now; null where nothing was measured.</summary>
public sealed record UsageTrend(double?[] Cpu, double?[] Memory)
{
    public static readonly UsageTrend Empty = new(new double?[UsageHistory.Length], new double?[UsageHistory.Length]);

    /// <summary>Several series added up, slot by slot: a workspace from its apps and containers. A slot where none of
    /// them was measured stays empty. Each part's short gaps are bridged first (see <see cref="Bridged"/>), or a
    /// container measured every few steps would make the total jump down and back between its measures.</summary>
    public static UsageTrend Sum(IEnumerable<UsageTrend> parts)
    {
        var all = parts.ToArray();
        if (all.Length == 0) return Empty;
        static double?[] Add(IEnumerable<double?[]> lines)
        {
            var sum = new double?[UsageHistory.Length];
            foreach (var line in lines.Select(Bridged))
                for (var i = 0; i < sum.Length; i++)
                    if (line[i] is { } v) sum[i] = (sum[i] ?? 0) + v;
            return sum;
        }
        return new UsageTrend(Add(all.Select(p => p.Cpu)), Add(all.Select(p => p.Memory)));
    }

    /// <summary>A gap of up to <see cref="UsageHistory.Bridge"/> empty slots between two figures, filled with the one
    /// before it. A gap at either end is left: there it was starting or had stopped.</summary>
    static double?[] Bridged(double?[] line)
    {
        var filled = (double?[])line.Clone();
        int? last = null;
        for (var i = 0; i < filled.Length; i++)
        {
            if (line[i] is null) continue;
            if (last is { } before && i - before - 1 is > 0 and var gap && gap <= UsageHistory.Bridge)
                for (var j = before + 1; j < i; j++) filled[j] = line[before];
            last = i;
        }
        return filled;
    }

    public double? PeakCpu => Cpu.Max();
    public double? PeakMemory => Memory.Max();
}

/// <summary>
/// What each app and container used over the last ten minutes, kept in memory while sprig runs, for the Usage page's
/// sparklines. Apps are recorded with each host answer, which comes even while the window is hidden; containers only
/// when docker is asked, so their lines have gaps while no page measures them.
/// </summary>
public sealed class UsageHistory
{
    /// <summary>The host measures every 2 s, so a finer step would only repeat its figures.</summary>
    public static readonly TimeSpan Step = Supervisor.SampleEvery;

    public static readonly TimeSpan Span = TimeSpan.FromMinutes(10);

    public static readonly int Length = (int)(Span / Step);

    /// <summary>Empty slots between two figures still read as one line, not a break: docker is asked every 5 s against
    /// 2 s slots, and a hidden window hears from the host every 5 s.</summary>
    public const int Bridge = 4;

    sealed class Series
    {
        readonly (long Slot, double? Cpu, long Memory)[] _ring = Enumerable.Repeat((-1L, (double?)null, 0L), Length).ToArray();

        public long Newest { get; private set; } = -1;

        public void Put(long slot, ResourceUsage usage)
        {
            _ring[slot % Length] = (slot, usage.Cpu, usage.Memory);
            Newest = Math.Max(Newest, slot);
        }

        public UsageTrend Trend(long now)
        {
            var cpu = new double?[Length];
            var memory = new double?[Length];
            for (var i = 0; i < Length; i++)
            {
                var slot = now - Length + 1 + i;
                if (slot < 0) continue;
                var (at, c, m) = _ring[slot % Length];
                if (at != slot) continue;
                cpu[i] = c;
                memory[i] = m;
            }
            return new UsageTrend(cpu, memory);
        }
    }

    readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);
    readonly Func<DateTime> _now;

    /// <param name="now">For tests: the clock slots are counted by.</param>
    public UsageHistory(HostStatus host, InfraUsage infra, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        host.Changed += runners => Record((runners ?? []).Where(r => r.Usage is not null).Select(r => (AppId(r.Workspace, r.Name), r.Usage!)));
        infra.Changed += () => Record(infra.Usage.Select(u => (InfraId(u.Key.Project, u.Key.Service), u.Value)));
    }

    public static string AppId(string workspace, string name) => $"app\n{workspace}\n{name}";

    public static string InfraId(string project, string service) => $"infra\n{project}\n{service}";

    long Slot => _now().Ticks / Step.Ticks;

    /// <summary>Put each figure in this step's slot (a later one in the same step wins), and forget series that have had
    /// nothing for longer than the span.</summary>
    public void Record(IEnumerable<(string Id, ResourceUsage Usage)> figures)
    {
        var slot = Slot;
        foreach (var (id, usage) in figures)
        {
            if (!_series.TryGetValue(id, out var series)) _series[id] = series = new Series();
            series.Put(slot, usage);
        }
        foreach (var gone in _series.Where(s => slot - s.Value.Newest >= Length).Select(s => s.Key).ToArray()) _series.Remove(gone);
    }

    /// <summary>For a rendered sample: a whole span of made-up figures for one series, by steps before now; null leaves a
    /// step empty.</summary>
    public void Fill(string id, Func<int, ResourceUsage?> stepsAgo)
    {
        var slot = Slot;
        if (!_series.TryGetValue(id, out var series)) _series[id] = series = new Series();
        for (var ago = Length - 1; ago >= 0; ago--)
            if (stepsAgo(ago) is { } usage) series.Put(slot - ago, usage);
    }

    /// <summary>One app's or container's trend, by <see cref="AppId"/> or <see cref="InfraId"/>; empty when it has none.</summary>
    public UsageTrend Of(string id) => _series.TryGetValue(id, out var series) ? series.Trend(Slot) : UsageTrend.Empty;

    /// <summary>Everything a workspace ran in the span, stopped since or not: its apps' series and those of its compose
    /// projects' containers, added up.</summary>
    public UsageTrend OfWorkspace(string key, IReadOnlySet<string> projects)
    {
        var slot = Slot;
        return UsageTrend.Sum(_series.Where(s => s.Key.Split('\n') is [var kind, var owner, _]
                && (kind == "app" ? owner == key : projects.Contains(owner)))
            .Select(s => s.Value.Trend(slot)));
    }
}
