using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Hosting;
using Sprig.Engine.Sprouts;

namespace Sprig.App.ViewModels;

/// <summary>One sprout in the nav.</summary>
public sealed partial class SproutNavItem(string key, string name, IRelayCommand<SproutNavItem> open) : ObservableObject
{
    public string Key => key;
    public string Name => name;
    public IRelayCommand<SproutNavItem> OpenCommand => open;

    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private bool _isUp;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isCurrent;
}

/// <summary>One trellis's sprouts in the nav: those up, starting or failed, then the rest folded into one row.</summary>
public sealed partial class SproutNavGroup(string trellis, SproutsNavViewModel owner) : ObservableObject
{
    public string Trellis => trellis;
    public string Label => trellis.ToUpperInvariant();

    public ObservableCollection<SproutNavItem> Shown { get; } = [];
    public ObservableCollection<SproutNavItem> Down { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isOpen = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownLabel))]
    private bool _isDownOpen;

    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private bool _captionIsBad;
    [ObservableProperty] private bool _captionIsGood;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownLabel))]
    private bool _hasDown;

    public string Chevron => IsOpen ? "▾" : "▸";
    public string DownLabel => IsDownOpen ? $"Hide {Down.Count} down" : $"{Down.Count} down";

    [RelayCommand]
    private void Toggle()
    {
        IsOpen = !IsOpen;
        owner.Remember(this);
    }

    [RelayCommand]
    private void ToggleDown()
    {
        IsDownOpen = !IsDownOpen;
        owner.Remember(this);
    }

    internal void CountsChanged() => OnPropertyChanged(nameof(DownLabel));
}

/// <summary>
/// The sprouts under Sprouts in the nav, grouped by trellis. Rebuilt from the store and the host's answer each poll, but
/// items are kept and updated in place so the nav doesn't flicker or lose its hover.
/// </summary>
public sealed partial class SproutsNavViewModel : ObservableObject
{
    readonly AppServices _services;
    readonly SproutsViewModel _sprouts;
    readonly Action _showSprouts;
    readonly Dictionary<string, (DateTimeOffset UpdatedAt, int Apps)> _appCounts = [];
    readonly Func<DateTimeOffset> _now;

    public ObservableCollection<SproutNavGroup> Groups { get; } = [];

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _overviewIsCurrent;

    public SproutsViewModel Page => _sprouts;

    /// <param name="showSprouts">Brings the Sprouts page to the front.</param>
    /// <param name="now">The clock for "3 days ago"; tests pass their own.</param>
    public SproutsNavViewModel(AppServices services, SproutsViewModel sprouts, Action showSprouts, Func<DateTimeOffset>? now = null)
    {
        _services = services;
        _sprouts = sprouts;
        _showSprouts = showSprouts;
        _now = now ?? (() => DateTimeOffset.Now);
        _services.HostStatus.Changed += _ => Refresh();
        _sprouts.SproutsChanged += Refresh;
        _sprouts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SproutsViewModel.Selected) or nameof(SproutsViewModel.IsActive)) MarkCurrent();
        };
        Refresh();
    }

    [RelayCommand]
    private void Open(SproutNavItem item)
    {
        _showSprouts();
        _sprouts.Show(item.Key);
    }

    [RelayCommand]
    private void OpenOverview()
    {
        _showSprouts();
        _sprouts.Show(null);
    }

    [RelayCommand]
    private void Add()
    {
        _showSprouts();
        _sprouts.OpenAddCommand.Execute(null);
    }

    public void Refresh()
    {
        var records = _services.Sprouts.List();
        var runners = _services.HostStatus.Runners ?? [];
        Count = records.Count;

        var byTrellis = records.GroupBy(r => r.Trellis, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        Sync(Groups, byTrellis.Select(g => g.Key).ToArray(), g => g.Trellis, trellis => new SproutNavGroup(trellis, this)
        {
            IsOpen = !_services.Ui.IsCollapsed(trellis),
            IsDownOpen = _services.Ui.IsDownOpen(trellis),
        });

        foreach (var group in Groups)
        {
            var mine = byTrellis.First(g => g.Key == group.Trellis).ToArray();
            var shown = mine.Where(r => r.State != SproutState.Down)
                .OrderBy(r => r.State switch { SproutState.Failed => 0, SproutState.Up => 1, _ => 2 }).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            var down = mine.Where(r => r.State == SproutState.Down).OrderByDescending(r => r.UpdatedAt).ToArray();
            SyncItems(group.Shown, shown, runners);
            SyncItems(group.Down, down, runners);

            var up = mine.Count(r => r.State == SproutState.Up);
            var failed = mine.Count(r => r.State == SproutState.Failed);
            group.Caption = failed > 0 ? $"{up} up · {failed} failed" : up > 0 ? $"{up} up · {mine.Length}" : $"{mine.Length} down";
            group.CaptionIsBad = failed > 0;
            group.CaptionIsGood = failed == 0 && up > 0;
            group.HasDown = down.Length > 0;
            group.CountsChanged();
        }
        MarkCurrent();
    }

    internal void Remember(SproutNavGroup group)
    {
        _services.Ui.SetCollapsed(group.Trellis, !group.IsOpen);
        _services.Ui.SetDownOpen(group.Trellis, group.IsDownOpen);
    }

    void SyncItems(ObservableCollection<SproutNavItem> items, IReadOnlyList<SproutRecord> records, IReadOnlyList<RunnerStatus> runners)
    {
        Sync(items, records.Select(r => r.Key).ToArray(), i => i.Key, key => new SproutNavItem(key, key[(key.IndexOf('/') + 1)..], OpenCommand));
        foreach (var (item, record) in items.Zip(records))
        {
            item.IsUp = record.State == SproutState.Up;
            item.IsFailed = record.State == SproutState.Failed;
            item.Sub = Sub(record, runners);
        }
    }

    string Sub(SproutRecord record, IReadOnlyList<RunnerStatus> runners)
    {
        var place = record.InPlace ? "in place · " : "";
        switch (record.State)
        {
            case SproutState.Failed:
                var first = (record.Error ?? "").Split('\n', 2)[0].Trim();
                return first.Length > 0 ? $"failed: {first}" : "failed";
            case SproutState.Starting:
                return place + "coming up";
            case SproutState.Down:
                return Ago(record.UpdatedAt);
        }
        var apps = Apps(record);
        var running = runners.Count(r => r.Sprout == record.Key && r.State.IsAlive());
        return apps == 0 ? place + "up" : running == 0 ? place + "apps stopped" : $"{place}{running} of {apps} running";
    }

    /// <summary>The apps that keep running (jobs finish), cached per record version so the plan isn't read every poll.</summary>
    int Apps(SproutRecord record)
    {
        if (_appCounts.TryGetValue(record.Key, out var cached) && cached.UpdatedAt == record.UpdatedAt) return cached.Apps;
        var count = _services.Sprouts.LastPlan(record.Trellis, record.Name)?.Apps.Count(a => !a.Job) ?? 0;
        _appCounts[record.Key] = (record.UpdatedAt, count);
        return count;
    }

    string Ago(DateTimeOffset when) => "down · " + ViewModels.Ago.Text(when, _now());

    void MarkCurrent()
    {
        var key = _sprouts.IsActive ? _sprouts.Selected?.Record.Key : null;
        foreach (var item in Groups.SelectMany(g => g.Shown.Concat(g.Down))) item.IsCurrent = item.Key == key;
        OverviewIsCurrent = _sprouts.IsActive && _sprouts.Selected is null;
    }

    /// <summary>Make <paramref name="items"/> hold exactly <paramref name="keys"/>, in order, keeping the objects it already has.</summary>
    static void Sync<T>(ObservableCollection<T> items, IReadOnlyList<string> keys, Func<T, string> keyOf, Func<string, T> make)
    {
        for (var i = items.Count - 1; i >= 0; i--)
            if (!keys.Contains(keyOf(items[i]))) items.RemoveAt(i);
        for (var i = 0; i < keys.Count; i++)
        {
            var at = -1;
            for (var j = i; j < items.Count; j++)
                if (keyOf(items[j]) == keys[i]) { at = j; break; }
            if (at == i) continue;
            if (at > i) items.Move(at, i);
            else items.Insert(i, make(keys[i]));
        }
    }
}
