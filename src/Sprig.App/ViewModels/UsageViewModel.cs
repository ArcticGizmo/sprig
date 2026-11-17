using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Hosting;
using Sprig.Engine.Workspaces;

namespace Sprig.App.ViewModels;

public enum UsageSort { Workspace, Cpu, Memory, Apps, Infra }

/// <summary>A figure's last ten minutes, as a sparkline draws it and its tooltip says it.</summary>
public abstract partial class UsageTrendView : ObservableObject
{
    /// <summary>Scaled to 0..1 for the sparkline (see <see cref="UsageViewModel.ScaleCpu"/>).</summary>
    [ObservableProperty] private IReadOnlyList<double?>? _cpuTrend;
    [ObservableProperty] private IReadOnlyList<double?>? _memoryTrend;
    [ObservableProperty] private string _cpuTrendTip = "";
    [ObservableProperty] private string _memoryTrendTip = "";

    internal void ShowTrend(UsageTrend trend)
    {
        CpuTrend = UsageViewModel.ScaleCpu(trend.Cpu);
        MemoryTrend = UsageViewModel.ScaleMemory(trend.Memory);
        CpuTrendTip = trend.PeakCpu is { } peak ? $"Last 10 minutes: CPU peaked at {ResourceUsage.CpuText(peak)}" : "Last 10 minutes: no CPU figure yet";
        MemoryTrendTip = trend.PeakMemory is { } most && trend.Memory.Min() is { } least
            ? $"Last 10 minutes: memory between {ResourceUsage.MemoryText((long)least)} and {ResourceUsage.MemoryText((long)most)}"
            : "Last 10 minutes: no memory figure yet";
    }
}

/// <summary>One app or container of a workspace on the Usage page, kept from one refresh to the next.</summary>
/// <param name="key">Its series in <see cref="UsageHistory"/>.</param>
public sealed partial class UsagePart(string key) : UsageTrendView
{
    public string Key => key;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _cpu = "";
    [ObservableProperty] private string _memory = "";
    [ObservableProperty] private string _tip = "";

    /// <summary>Its memory as a width, against the workspace's heaviest part.</summary>
    [ObservableProperty] private double _bar;

    internal ResourceUsage Usage { get; set; } = new(null, 0);
}

/// <summary>One workspace on the Usage page, kept from one refresh to the next so it stays open and doesn't flicker.</summary>
public sealed partial class UsageRow(string key, Action<UsageRow> toggled) : UsageTrendView
{
    public string Key => key;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _branch = "";
    [ObservableProperty] private string _cpu = "";
    [ObservableProperty] private string _memory = "";
    [ObservableProperty] private string _apps = "";
    [ObservableProperty] private string _infra = "";

    /// <summary>Its memory as a width, against the heaviest workspace's.</summary>
    [ObservableProperty] private double _bar;

    public ObservableCollection<UsagePart> Parts { get; } = [];

    internal Dictionary<string, UsagePart> PartsByKey { get; } = new(StringComparer.Ordinal);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded;

    public string Chevron => IsExpanded ? "▾" : "▸";

    /// <summary>Its record is in the store, so it can be opened on the Workspaces page.</summary>
    public bool CanOpen { get; set; }

    internal ResourceUsage Total { get; set; } = new(null, 0);
    internal int AppCount { get; set; }
    internal int InfraCount { get; set; }

    partial void OnIsExpandedChanged(bool value) => toggled(this);

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// What every workspace's apps and containers are using, side by side and sorted, against the machine as a whole: for
/// finding the worktree that's weighing the machine down. Apps' figures come from the host's poll, containers' from
/// docker, and the machine's from Windows, all refreshed while the page shows.
/// </summary>
public partial class UsageViewModel : PageViewModel
{
    /// <summary>Widths, in pixels, of the bars: the machine's, a workspace's and a part's.</summary>
    public const double MachineBarWidth = 420, RowBarWidth = 90, PartBarWidth = 60;

    /// <summary>Width of a sparkline: ten minutes in 300 slots, so about four to a pixel.</summary>
    public const double SparkWidth = 72;

    readonly AppServices _services;
    readonly MachineUsage _machine = new();
    MachineSnapshot? _heldMachine;
    MachineSnapshot? _lastMachine;
    IReadOnlyList<WorkspaceRecord> _records = [];
    readonly Dictionary<string, UsageRow> _rows = new(StringComparer.Ordinal);
    readonly HashSet<string> _expanded = new(StringComparer.Ordinal);

    public override string Title => "Usage";
    public override string? NavSubtitle => "CPU and memory";

    /// <summary>Open a workspace, by its key, on the Workspaces page.</summary>
    public Action<string>? OpenWorkspace { get; set; }

    /// <summary>Workspaces using anything, in the order picked.</summary>
    public ObservableCollection<UsageRow> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;
    public bool NoRows => Rows.Count == 0;

    /// <summary>Up, but with nothing of theirs running, so left out of the table.</summary>
    [ObservableProperty] private string _idleNote = "";

    [ObservableProperty] private string _memoryLine = "";
    [ObservableProperty] private string _cpuLine = "";
    [ObservableProperty] private double _memoryOtherWidth;
    [ObservableProperty] private double _memorySprigWidth;
    [ObservableProperty] private double _cpuOtherWidth;
    [ObservableProperty] private double _cpuSprigWidth;

    [ObservableProperty] private UsageSort _sortBy = UsageSort.Memory;
    [ObservableProperty] private bool _sortDescending = true;

    public string WorkspaceHeading => Heading("WORKSPACE", UsageSort.Workspace);
    public string CpuHeading => Heading("CPU", UsageSort.Cpu);
    public string MemoryHeading => Heading("MEMORY", UsageSort.Memory);
    public string AppsHeading => Heading("APPS", UsageSort.Apps);
    public string InfraHeading => Heading("INFRA", UsageSort.Infra);

    string Heading(string text, UsageSort sort) => sort != SortBy ? text : text + (SortDescending ? " ▼" : " ▲");

    public UsageViewModel(AppServices services)
    {
        _services = services;
        FollowPolls(services, _ => RefreshIfShown(), RefreshIfShown);
        _records = _services.Workspaces.List();
    }

    protected override void OnActivated()
    {
        _records = _services.Workspaces.List();
        Refresh();
    }

    protected override void OnWindowFocused() => OnActivated();

    void RefreshIfShown()
    {
        if (IsActive) Refresh();
    }

    /// <summary>For a rendered sample or a test: the machine reads this, and isn't asked.</summary>
    public void HoldMachine(MachineSnapshot snapshot) => _heldMachine = snapshot;

    /// <summary>Measure the machine, and ask docker about the containers on its own slower beat. The view calls this every
    /// second while the page shows; the app-wide host poll brings the apps.</summary>
    /// <param name="usage">Ask docker; not while the window is hidden.</param>
    public Task PollAsync(bool usage = true)
    {
        _lastMachine = _heldMachine ?? _machine.Sample();
        // Not awaited: docker takes a second or two, and the machine's figures shouldn't wait for it.
        if (usage) _ = _services.InfraUsage.PollAsync(_records);
        Refresh();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Sort(UsageSort by)
    {
        if (by == SortBy) SortDescending = !SortDescending;
        else (SortBy, SortDescending) = (by, by != UsageSort.Workspace);
        Refresh();
    }

    [RelayCommand]
    private void Open(UsageRow row)
    {
        if (row.CanOpen) OpenWorkspace?.Invoke(row.Key);
    }

    partial void OnSortByChanged(UsageSort value) => NotifyHeadings();
    partial void OnSortDescendingChanged(bool value) => NotifyHeadings();

    void NotifyHeadings()
    {
        OnPropertyChanged(nameof(WorkspaceHeading));
        OnPropertyChanged(nameof(CpuHeading));
        OnPropertyChanged(nameof(MemoryHeading));
        OnPropertyChanged(nameof(AppsHeading));
        OnPropertyChanged(nameof(InfraHeading));
    }

    void Toggled(UsageRow row)
    {
        if (row.IsExpanded) _expanded.Add(row.Key);
        else _expanded.Remove(row.Key);
        // Closed, its parts' sparklines weren't kept up, so they're drawn now rather than at the next refresh.
        if (row.IsExpanded)
            foreach (var part in row.Parts) part.ShowTrend(_services.History.Of(part.Key));
    }

    /// <summary>Gather every workspace's figures, sort them, and bring the table and the machine's bars up to date.</summary>
    public void Refresh()
    {
        var runners = _services.HostStatus.Runners ?? [];
        var infra = _services.InfraUsage.Usage;
        var history = _services.History;
        var records = _records.ToDictionary(r => r.Key, StringComparer.Ordinal);
        // A runner of a workspace the list hasn't heard of (made from a terminal since) gets its row all the same.
        var keys = records.Keys.Concat(runners.Where(r => r.Usage is not null).Select(r => r.Workspace)).Distinct(StringComparer.Ordinal);

        var shown = new List<UsageRow>();
        var idle = 0;
        foreach (var key in keys)
        {
            var record = records.GetValueOrDefault(key);
            var apps = runners.Where(r => r.Workspace == key && r.Usage is not null).ToArray();
            var projects = record is null ? [] : WorkspaceService.InfraProjects(record).ToHashSet(StringComparer.Ordinal);
            var containers = infra.Where(u => projects.Contains(u.Key.Project)).ToArray();
            if (apps.Length + containers.Length == 0)
            {
                if (record?.State == WorkspaceState.Up) idle++;
                continue;
            }

            if (!_rows.TryGetValue(key, out var row)) _rows[key] = row = new UsageRow(key, Toggled) { IsExpanded = _expanded.Contains(key) };
            row.CanOpen = record is not null;
            row.Name = record is null ? key : WorkspaceItem.Display(record);
            row.Branch = record is null ? "" : WorkspaceItem.BranchOf(record);
            row.Total = ResourceUsage.Sum(apps.Select(a => a.Usage).Concat(containers.Select(c => (ResourceUsage?)c.Value)))!;
            row.AppCount = apps.Length;
            row.InfraCount = containers.Length;
            row.Cpu = ResourceUsage.CpuText(row.Total.Cpu);
            row.Memory = ResourceUsage.MemoryText(row.Total.Memory);
            row.Apps = apps.Length == 0 ? "—" : $"{apps.Length} running";
            row.Infra = record?.IsMain == true ? "yours" : containers.Length == 0 ? "—" : $"{containers.Length} running";

            row.ShowTrend(history.OfWorkspace(key, projects));

            var parts = apps.Select(a => (Key: UsageHistory.AppId(key, a.Name), Name: a.Name[(a.Name.IndexOf('/') + 1)..],
                    Kind: $"app · {a.Name[..Math.Max(0, a.Name.IndexOf('/'))]}", Usage: a.Usage!))
                .Concat(containers.Select(c => (Key: UsageHistory.InfraId(c.Key.Project, c.Key.Service), Name: c.Key.Service, Kind: "container", Usage: c.Value)))
                .ToArray();
            var heaviest = Math.Max(1, parts.Max(p => p.Usage.Memory));
            var built = new List<UsagePart>();
            foreach (var p in parts)
            {
                if (!row.PartsByKey.TryGetValue(p.Key, out var part)) row.PartsByKey[p.Key] = part = new UsagePart(p.Key);
                part.Name = p.Name;
                part.Kind = p.Kind;
                part.Usage = p.Usage;
                part.Cpu = ResourceUsage.CpuText(p.Usage.Cpu);
                part.Memory = ResourceUsage.MemoryText(p.Usage.Memory);
                part.Bar = Math.Max(2, PartBarWidth * p.Usage.Memory / heaviest);
                part.Tip = p.Usage.Line;
                // Only an open row's sparklines are seen, so only theirs are worked out.
                if (row.IsExpanded) part.ShowTrend(history.Of(p.Key));
                built.Add(part);
            }
            foreach (var gone in row.PartsByKey.Keys.Except(built.Select(p => p.Key)).ToArray()) row.PartsByKey.Remove(gone);
            Sync(row.Parts, SortBy == UsageSort.Cpu ? Order(built, p => p.Usage.Cpu ?? 0, p => p.Name) : Order(built, p => p.Usage.Memory, p => p.Name));
            shown.Add(row);
        }

        var heaviestRow = Math.Max(1, shown.Count == 0 ? 1 : shown.Max(r => r.Total.Memory));
        foreach (var row in shown) row.Bar = Math.Max(2, RowBarWidth * row.Total.Memory / heaviestRow);
        Sync(Rows, SortBy switch
        {
            UsageSort.Workspace => SortDescending ? shown.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : shown.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            UsageSort.Cpu => Order(shown, r => r.Total.Cpu ?? 0, r => r.Name),
            UsageSort.Apps => Order(shown, r => r.AppCount, r => r.Name),
            UsageSort.Infra => Order(shown, r => r.InfraCount, r => r.Name),
            _ => Order(shown, r => r.Total.Memory, r => r.Name),
        });
        foreach (var gone in _rows.Keys.Except(shown.Select(r => r.Key)).ToArray()) _rows.Remove(gone);
        IdleNote = idle == 0 ? "" : idle == 1 ? "1 more is ready with nothing running." : $"{idle} more are ready with nothing running.";
        ShowMachine(ResourceUsage.Sum(shown.Select(r => (ResourceUsage?)r.Total)));
    }

    /// <summary>By <paramref name="key"/> in the picked direction, then by name so rows with the same figure hold still.</summary>
    List<T> Order<T>(IEnumerable<T> items, Func<T, double> key, Func<T, string> name)
        => (SortDescending ? items.OrderByDescending(key) : items.OrderBy(key)).ThenBy(name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Move the items into <paramref name="order"/> rather than rebuilding the list, so an open row, the
    /// pointer's hover and a tooltip survive each second's refresh.</summary>
    void Sync<T>(ObservableCollection<T> list, IReadOnlyList<T> order) where T : class
    {
        for (var i = list.Count - 1; i >= 0; i--)
            if (!order.Contains(list[i])) list.RemoveAt(i);
        for (var i = 0; i < order.Count; i++)
        {
            var at = list.IndexOf(order[i]);
            if (at < 0) list.Insert(i, order[i]);
            else if (at != i) list.Move(at, i);
        }
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(NoRows));
    }

    /// <summary>CPU from nothing up to its peak, at least 1%, so an idle app lies flat along the bottom rather than
    /// showing its noise at full height.</summary>
    public static double?[] ScaleCpu(double?[] cpu)
    {
        var top = Math.Max(1, cpu.Max() ?? 0);
        return cpu.Select(v => v / top).ToArray();
    }

    /// <summary>Memory between its low and its peak, so growth shows; the span is at least a tenth of the peak, so
    /// memory that holds steady doesn't blow its small wobbles up to full height.</summary>
    public static double?[] ScaleMemory(double?[] memory)
    {
        if (memory.Max() is not { } peak || peak <= 0) return memory.Select(v => v is null ? null : (double?)0).ToArray();
        var span = Math.Max(peak - (memory.Min() ?? 0), peak * 0.1);
        var floor = peak - span;
        return memory.Select(v => (v - floor) / span).ToArray();
    }

    /// <summary>The machine's memory and CPU, with sprig's share of each drawn inside what's in use.</summary>
    void ShowMachine(ResourceUsage? sprig)
    {
        var machine = _lastMachine;
        var mine = sprig?.Memory ?? 0;
        if (machine is { MemoryUsed: { } used, MemoryTotal: > 0 and var total })
        {
            var share = Math.Min(mine, used);
            MemorySprigWidth = MachineBarWidth * share / total;
            MemoryOtherWidth = MachineBarWidth * (used - share) / total;
            MemoryLine = $"{GigaBytes(used)} of {GigaBytes(total)} in use · {ResourceUsage.MemoryText(mine)} of it what sprig runs";
        }
        else
        {
            (MemorySprigWidth, MemoryOtherWidth) = (machine is { MemoryTotal: > 0 } m ? MachineBarWidth * mine / m.MemoryTotal : 0, 0);
            MemoryLine = $"{ResourceUsage.MemoryText(mine)} for what sprig runs";
        }

        var sprigCpu = sprig?.Cpu ?? 0;
        if (machine?.Cpu is { } cpu)
        {
            var share = Math.Min(sprigCpu, cpu);
            CpuSprigWidth = MachineBarWidth * share / 100;
            CpuOtherWidth = MachineBarWidth * (cpu - share) / 100;
            CpuLine = $"{ResourceUsage.CpuText(cpu)} busy · {ResourceUsage.CpuText(sprigCpu)} of it what sprig runs";
        }
        else
        {
            (CpuSprigWidth, CpuOtherWidth) = (MachineBarWidth * sprigCpu / 100, 0);
            CpuLine = $"{ResourceUsage.CpuText(sprigCpu)} for what sprig runs";
        }
    }

    /// <summary>Always in GB, so the machine's two figures read alike.</summary>
    static string GigaBytes(long bytes) => (bytes / (1024d * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB";
}
