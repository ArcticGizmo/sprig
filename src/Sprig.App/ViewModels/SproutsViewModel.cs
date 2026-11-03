using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Definitions;
using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;
using Sprig.Engine.Sprouts;
using Sprig.Engine.Trellises;

namespace Sprig.App.ViewModels;

public sealed record SproutItem(SproutRecord Record)
{
    public string Title => $"{Record.Trellis} / {Record.Name}";
    public string Branch => Record.Branch ?? "repos in place";
    public string State => Record.State.ToString().ToLowerInvariant();
    public bool IsUp => Record.State == SproutState.Up;
    public bool IsFailed => Record.State == SproutState.Failed;
}

/// <summary>One app or job of the selected sprout, with what the host says about it.</summary>
/// <param name="Key">The app as the host names it, <c>repo/app</c>.</param>
public sealed partial class SproutAppRow(string key, string name, string folder, string url, string values, bool job) : ObservableObject
{
    public string Key => key;
    public string Name => name;
    public string Folder => folder;
    public string Url => url;
    public string Values => values;
    public bool IsJob => job;

    [ObservableProperty] private string _state = "not running";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private bool _isGood;
    [ObservableProperty] private bool _isBad;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isAlive;

    public bool HasDetail => Detail.Length > 0;

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));

    public void MarkStopping()
    {
        (State, Detail, IsGood, IsBad, IsBusy) = ("stopping", "", false, false, true);
    }

    /// <summary>Show <paramref name="status"/>; null when the host has no runner for this app.</summary>
    public void Apply(RunnerStatus? status)
    {
        var restarts = status is { Restarts: > 0 } ? $"restarted {status.Restarts}×" : null;
        (State, Detail) = status?.State switch
        {
            null => ("not running", ""),
            RunnerState.Building => ("building", ""),
            RunnerState.Waiting => ("waiting", "for what it depends on"),
            RunnerState.Starting => ("starting", ""),
            RunnerState.Ready => ("ready", restarts ?? ""),
            RunnerState.Running => ("running", ""),
            RunnerState.Completed => ("finished", restarts is null ? "" : $"after {restarts}"),
            RunnerState.Restarting => ("restarting", status.Error ?? ""),
            RunnerState.Exited => ("exited", status.Error ?? $"code {status.Exit}"),
            RunnerState.Failed => ("failed", status.Error ?? ""),
            RunnerState.Stopping => ("stopping", ""),
            RunnerState.Stopped => ("stopped", status.Error ?? ""),
            _ => (status.State.ToString().ToLowerInvariant(), ""),
        };
        IsGood = status?.State is RunnerState.Ready or RunnerState.Completed;
        IsBad = status?.State is RunnerState.Failed or RunnerState.Exited;
        IsBusy = status?.State is RunnerState.Building or RunnerState.Waiting or RunnerState.Starting or RunnerState.Running
            or RunnerState.Restarting or RunnerState.Stopping;
        IsAlive = status is not null && (status.State.IsAlive() || status.State == RunnerState.Stopping);
    }
}

public sealed record SproutInfraRow(string Name, string Ports);

/// <summary>
/// The Sprouts page: every sprout with its state, the folders, urls and ports of the selected one, and a form to make
/// a new one. Up, down and remove run in the background and report to a progress window the view opens. The selected
/// sprout's apps can be started and stopped in the sprig host, each showing its state, with one app's log beside them.
/// </summary>
public partial class SproutsViewModel : PageViewModel
{
    readonly AppServices _services;
    Func<Task>? _afterApproval;

    public override string Title => "Sprouts";

    public ObservableCollection<SproutItem> Sprouts { get; } = [];
    public ObservableCollection<string> TrellisNames { get; } = [];
    public ObservableCollection<PendingTrustItem> PendingTrust { get; } = [];

    [ObservableProperty] private SproutItem? _selected;
    [ObservableProperty] private IReadOnlyList<SproutAppRow> _apps = [];
    [ObservableProperty] private IReadOnlyList<SproutInfraRow> _infra = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private IReadOnlyList<ProblemRow> _warnings = [];

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty] private string? _newTrellis;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newBranch = "";
    [ObservableProperty] private string _formMessage = "";
    [ObservableProperty] private bool _formFailed;
    [ObservableProperty] private bool _hasPendingTrust;

    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _confirmingRemove;
    [ObservableProperty] private bool _deleteBranch;

    /// <summary>The view opens a progress window for each operation it's handed.</summary>
    public event Action<OperationProgressViewModel>? ProgressStarted;

    public bool IsEmpty => Sprouts.Count == 0;
    public bool HasSelection => Selected is not null;
    public bool HasTrellises => TrellisNames.Count > 0;
    public bool CanDeleteBranch => Selected?.Record.InPlace == false;

    public SproutsViewModel(AppServices services)
    {
        _services = services;
        Refresh();
    }

    protected override void OnActivated() => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        var keep = Selected?.Record.Key;
        Sprouts.Clear();
        foreach (var record in _services.Sprouts.List()) Sprouts.Add(new SproutItem(record));
        NavCount = Sprouts.Count(s => s.IsUp);

        var trellis = NewTrellis;
        TrellisNames.Clear();
        foreach (var entry in _services.Trellises.List()) TrellisNames.Add(entry.Name);
        NewTrellis = TrellisNames.Contains(trellis ?? "") ? trellis : TrellisNames.FirstOrDefault();

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasTrellises));
        Selected = Sprouts.FirstOrDefault(s => s.Record.Key == keep) ?? Sprouts.FirstOrDefault();
    }

    partial void OnSelectedChanged(SproutItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDeleteBranch));
        ConfirmingRemove = false;
        DeleteBranch = false;
        ShowDetails(value?.Record);
    }

    void ShowDetails(SproutRecord? record)
    {
        var plan = record is null ? null : _services.Sprouts.LastPlan(record.Trellis, record.Name);
        if (Log.IsOpen && (record is null || !Log.File!.StartsWith(LogFolder(record), StringComparison.OrdinalIgnoreCase))) Log.CloseCommand.Execute(null);
        AppsMessage = "";
        if (record is null || plan is null)
        {
            Apps = [];
            Infra = [];
            Warnings = [];
            return;
        }

        Apps = plan.Apps.Select(app =>
        {
            var repo = record.Repos.First(r => r.RepoPath == app.RepoPath);
            var dir = Path.GetFullPath(Path.Combine(repo.Dir, app.Path));
            var port = plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name)?.Port;
            var files = record.Files.Where(f => f.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(dir, f.Path).Replace('\\', '/')).ToArray();
            return new SproutAppRow(app.Key, $"{repo.Name} / {app.Name}", dir.Replace('\\', '/'), port is null ? "" : $"http://localhost:{port}",
                files.Length > 0 ? string.Join(", ", files) : $"none; sprig env {record.Trellis} {record.Name} {app.Name}", app.Job);
        }).ToArray();
        ApplyRunners(_lastRunners);

        Infra = plan.Containers.Select(c => new SproutInfraRow($"{c.Owner} / {c.Name}", string.Join(", ", c.Ports)))
            .Concat(plan.Compose.SelectMany(c => c.Services.Select(s => new SproutInfraRow($"{c.Owner} / {s.Name}", string.Join(", ", s.Mappings)))))
            .ToArray();

        Warnings = PlanView.Rows(plan.Problems.Where(p => p.Severity == ProblemSeverity.Warning), Path.GetDirectoryName(record.TrellisPath)!).ToArray();
    }

    // ---- running the selected sprout's apps ----

    IReadOnlyList<RunnerStatus>? _lastRunners;
    bool _polling;

    /// <summary>The selected app's log, read as it grows.</summary>
    public LogPaneViewModel Log { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsMessage))]
    private string _appsMessage = "";

    [ObservableProperty] private bool _appsMessageIsBad;

    public bool HasAppsMessage => AppsMessage.Length > 0;

    string LogFolder(SproutRecord record) => Path.GetDirectoryName(HostProtocol.LogFile(_services.Paths, record.Key, "x/x"))!;

    /// <summary>Show what the host says about each app of the selected sprout; null when no host is running.</summary>
    public void ApplyRunners(IReadOnlyList<RunnerStatus>? runners)
    {
        _lastRunners = runners;
        var key = Selected?.Record.Key;
        foreach (var row in Apps)
            row.Apply(runners?.FirstOrDefault(r => r.Sprout == key && r.Name == row.Key));
    }

    /// <summary>Ask the host how the apps are doing and read the open log. The view calls this every second while the
    /// page shows; a call still waiting on the host makes the next one a no-op.</summary>
    public async Task PollAsync()
    {
        if (_polling || Selected is null) return;
        _polling = true;
        try
        {
            var answer = await AppServices.RunAsync(() => _services.Host.TrySend(new HostRequest("list")));
            ApplyRunners(answer?.Runners);
            await Log.PollAsync();
        }
        finally
        {
            _polling = false;
        }
    }

    [RelayCommand]
    private async Task StartApps() => await StartAsync(null);

    [RelayCommand]
    private async Task StartApp(SproutAppRow row) => await StartAsync(row);

    /// <summary>Every app (null), leaving alone what's already going; or one app, started afresh, after what it
    /// depends on.</summary>
    async Task StartAsync(SproutAppRow? row)
    {
        if (Selected?.Record is not { } record) return;
        if (record.State != SproutState.Up)
        {
            SayApps("Bring the sprout up first: its apps need its infra and env files.", bad: true);
            return;
        }
        if (_services.Sprouts.LastPlan(record.Trellis, record.Name) is not { } plan) return;
        try
        {
            var names = row is null ? null : new[] { row.Key };
            await AppServices.RunAsync(() =>
            {
                var specs = RunnerSpecs.For(record, plan, names);
                var answer = _services.Host.Send(new HostRequest("start", specs, Restart: names ?? []));
                if (!answer.Ok) throw new InvalidOperationException(answer.Error ?? "the host wouldn't start the apps");
            });
            SayApps(row is null ? "Starting the apps, each after what it depends on." : $"Starting {row.Name}.", bad: false);
            if (row is not null) ShowLog(row);
            else if (!Log.IsOpen && Apps.FirstOrDefault(a => !a.IsJob) is { } first) ShowLog(first);
            await PollAsync();
        }
        catch (Exception e)
        {
            SayApps(e.Message, bad: true);
        }
    }

    [RelayCommand]
    private async Task StopApps() => await StopAsync(null);

    [RelayCommand]
    private async Task StopApp(SproutAppRow row) => await StopAsync(row);

    async Task StopAsync(SproutAppRow? row)
    {
        if (Selected?.Record is not { } record) return;
        SayApps(row is null ? "Stopping the apps…" : $"Stopping {row.Name}…", bad: false);
        // Shown at once; the stop itself can take up to the grace period.
        foreach (var r in Apps.Where(a => (row is null || a == row) && a.IsAlive)) r.MarkStopping();
        var stopped = await AppServices.RunAsync(() => _services.Host.StopAndWait(record.Key, row?.Key));
        var forced = stopped.Where(s => s.Error is not null).Select(s => $"{s.Name}: {s.Error}").ToArray();
        SayApps(forced.Length > 0 ? string.Join("; ", forced)
            : stopped.Count == 0 ? "Nothing was running." : row is null ? "The apps have stopped. The infra is still up." : $"{row.Name} has stopped.",
            bad: forced.Length > 0);
        await PollAsync();
    }

    [RelayCommand]
    private void ShowLog(SproutAppRow row)
    {
        if (Selected?.Record is not { } record) return;
        Log.Open(row.Name, HostProtocol.LogFile(_services.Paths, record.Key, row.Key));
        _ = Log.PollAsync();
    }

    void SayApps(string message, bool bad)
    {
        AppsMessage = message;
        AppsMessageIsBad = bad;
    }

    // ---- making and changing sprouts ----

    [RelayCommand]
    private async Task Create()
    {
        FormMessage = "";
        FormFailed = false;
        if (NewTrellis is not { } name || _services.Trellises.Resolve(name) is not { } path)
        {
            Fail("Choose a trellis. Add one on the Trellises page first.");
            return;
        }

        var branch = NewBranch.Trim() is { Length: > 0 } b ? b : null;
        var sprout = NewName.Trim() is { Length: > 0 } n ? n : branch is null ? null : SproutService.NameFromBranch(branch);

        var loaded = await LoadAsync(path, Create);
        if (loaded is null) return;

        sprout ??= loaded.Trellis!.Worktrees is null ? "local" : null;
        if (sprout is null)
        {
            Fail($"{loaded.Trellis!.Name} gives each sprout its own worktrees, so name the sprout (or give a branch).");
            return;
        }
        if (SproutService.CheckName(sprout) is { } bad)
        {
            Fail(bad);
            return;
        }

        var ok = await RunAsync($"Sprouting {loaded.Trellis!.Name} / {sprout}",
            progress => _services.Sprouts.Up(loaded, sprout, branch, progress),
            $"{loaded.Trellis.Name} / {sprout} is up.");
        if (ok)
        {
            NewName = "";
            NewBranch = "";
        }
        Refresh();
        Selected = Sprouts.FirstOrDefault(s => s.Record.Trellis == loaded.Trellis.Name && s.Record.Name == sprout) ?? Selected;
    }

    [RelayCommand]
    private async Task Up()
    {
        if (Selected?.Record is not { } record) return;
        var loaded = await LoadAsync(record.TrellisPath, Up);
        if (loaded is null) return;
        await RunAsync($"Bringing {record.Trellis} / {record.Name} up",
            progress => _services.Sprouts.Up(loaded, record.Name, record.Branch, progress), $"{record.Trellis} / {record.Name} is up.");
        Refresh();
    }

    [RelayCommand]
    private async Task Down()
    {
        if (Selected?.Record is not { } record) return;
        await RunAsync($"Stopping {record.Trellis} / {record.Name}",
            progress => _services.Sprouts.Down(record.Trellis, record.Name, progress),
            $"{record.Trellis} / {record.Name} is down. Its data, worktrees and ports are kept.");
        Refresh();
    }

    [RelayCommand]
    private void AskRemove() => ConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    [RelayCommand]
    private async Task Remove()
    {
        if (Selected?.Record is not { } record) return;
        ConfirmingRemove = false;
        var options = new RemoveOptions(DeleteBranch && !record.InPlace);
        await RunAsync($"Removing {record.Trellis} / {record.Name}",
            progress => _services.Sprouts.Remove(record.Trellis, record.Name, options, progress),
            $"{record.Trellis} / {record.Name} is gone.");
        Refresh();
    }

    [RelayCommand]
    private async Task ApproveAll()
    {
        foreach (var item in PendingTrust) _services.Trust.Approve(item.Path, item.Sha256);
        PendingTrust.Clear();
        HasPendingTrust = false;
        if (_afterApproval is { } next)
        {
            _afterApproval = null;
            await next();
        }
    }

    /// <summary>Load a trellis, asking for approval first when a definition needs it. Null when it can't be used yet;
    /// the form says why.</summary>
    async Task<LoadedTrellis?> LoadAsync(string path, Func<Task> retry)
    {
        IsWorking = true;
        try
        {
            var loaded = await AppServices.RunAsync(() => _services.Loader.Load(path));
            if (loaded.Untrusted.Count > 0)
            {
                PendingTrust.Clear();
                foreach (NeedsTrust p in loaded.Untrusted)
                    PendingTrust.Add(new PendingTrustItem(p.Path.Replace('\\', '/'), p.Sha256, p.ApprovedSha256 is not null));
                HasPendingTrust = true;
                _afterApproval = retry;
                return null;
            }
            if (loaded.Trellis is null || loaded.Problems.Count > 0)
            {
                Fail("The trellis can't be planned: " + string.Join("; ", loaded.Problems.Take(3).Select(p => p.Message)) +
                     ". The Trellises page shows the details.");
                return null;
            }
            return loaded;
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Run one operation off the UI thread, reporting into a progress window.</summary>
    async Task<bool> RunAsync(string heading, Action<IProgress<SproutEvent>> work, string success)
    {
        var window = new OperationProgressViewModel(heading);
        var progress = new Progress<SproutEvent>(window.Apply);
        ProgressStarted?.Invoke(window);
        IsWorking = true;
        try
        {
            await AppServices.RunAsync(() => work(progress));
            // Progress<T> posts its reports, so let the last ones land before the summary.
            await Task.Yield();
            window.Finish(success, ProgressState.Done);
            return true;
        }
        catch (Exception e)
        {
            var problems = e is SproutException se ? se.Problems.Where(p => p.Severity == ProblemSeverity.Error).Take(4).Select(p => p.Message) : [];
            window.Finish(string.Join("\n", new[] { e.Message }.Concat(problems)), ProgressState.Error);
            return false;
        }
        finally
        {
            IsWorking = false;
        }
    }

    void Fail(string message)
    {
        FormFailed = true;
        FormMessage = message;
    }
}
