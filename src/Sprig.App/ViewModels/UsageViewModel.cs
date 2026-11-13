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

/// <summary>One app or container of a workspace on the Usage page.</summary>
/// <param name="Bar">Its memory as a width, against the workspace's heaviest part.</param>
public sealed record UsagePart(string Name, string Kind, string Cpu, string Memory, double Bar, string Tip);

/// <summary>One workspace on the Usage page, kept from one refresh to the next so it stays open and doesn't flicker.</summary>
public sealed partial class UsageRow(string key, Action<UsageRow> toggled) : ObservableObject
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

    [ObservableProperty] private IReadOnlyList<UsagePart> _parts = [];

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
        _services.HostStatus.Changed += _ => RefreshIfShown();
        _services.InfraUsage.Changed += RefreshIfShown;
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
    }

    /// <summary>Gather every workspace's figures, sort them, and bring the table and the machine's bars up to date.</summary>
    public void Refresh()
    {
        var runners = _services.HostStatus.Runners ?? [];
        var infra = _services.InfraUsage.Usage;
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

            var parts = apps.Select(a => (Name: a.Name[(a.Name.IndexOf('/') + 1)..], Kind: $"app · {a.Name[..Math.Max(0, a.Name.IndexOf('/'))]}", Usage: a.Usage!))
                .Concat(containers.Select(c => (Name: c.Key.Service, Kind: "container", Usage: c.Value)))
                .ToArray();
            var heaviest = Math.Max(1, parts.Max(p => p.Usage.Memory));
            var ordered = SortBy == UsageSort.Cpu ? Order(parts, p => p.Usage.Cpu ?? 0, p => p.Name) : Order(parts, p => p.Usage.Memory, p => p.Name);
            var built = ordered.Select(p => new UsagePart(p.Name, p.Kind, ResourceUsage.CpuText(p.Usage.Cpu), ResourceUsage.MemoryText(p.Usage.Memory),
                Math.Max(2, PartBarWidth * p.Usage.Memory / heaviest), p.Usage.Line)).ToArray();
            if (!built.SequenceEqual(row.Parts)) row.Parts = built;
            shown.Add(row);
        }

        var heaviestRow = Math.Max(1, shown.Count == 0 ? 1 : shown.Max(r => r.Total.Memory));
        foreach (var row in shown) row.Bar = Math.Max(2, RowBarWidth * row.Total.Memory / heaviestRow);
        Sync(SortBy switch
        {
            UsageSort.Workspace => SortDescending ? shown.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : shown.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            UsageSort.Cpu => Order(shown, r => r.Total.Cpu ?? 0, r => r.Name),
            UsageSort.Apps => Order(shown, r => r.AppCount, r => r.Name),
            UsageSort.Infra => Order(shown, r => r.InfraCount, r => r.Name),
            _ => Order(shown, r => r.Total.Memory, r => r.Name),
        });
        foreach (var gone in _rows.Keys.Except(shown.Select(r => r.Key)).ToArray()) _rows.Remove(gone);
        IdleNote = idle == 0 ? "" : idle == 1 ? "1 more is up with nothing of its own running." : $"{idle} more are up with nothing of their own running.";
        ShowMachine(ResourceUsage.Sum(shown.Select(r => (ResourceUsage?)r.Total)));
    }

    /// <summary>By <paramref name="key"/> in the picked direction, then by name so rows with the same figure hold still.</summary>
    List<T> Order<T>(IEnumerable<T> items, Func<T, double> key, Func<T, string> name)
        => (SortDescending ? items.OrderByDescending(key) : items.OrderBy(key)).ThenBy(name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Move the rows into <paramref name="order"/> rather than rebuilding the list, so an open row and the
    /// pointer's hover survive each second's refresh.</summary>
    void Sync(IReadOnlyList<UsageRow> order)
    {
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!order.Contains(Rows[i])) Rows.RemoveAt(i);
        for (var i = 0; i < order.Count; i++)
        {
            var at = Rows.IndexOf(order[i]);
            if (at < 0) Rows.Insert(i, order[i]);
            else if (at != i) Rows.Move(at, i);
        }
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(NoRows));
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
