using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Logs;
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
/// <param name="key">The app as the host names it, <c>repo/app</c>.</param>
/// <param name="name">For messages: <c>repo / app</c>.</param>
/// <param name="repo">The repo's name.</param>
/// <param name="app">The app's own name.</param>
/// <param name="port">Its main port, or "" when it has none.</param>
public sealed partial class SproutAppRow(string key, string name, string folder, string url, string values, bool job,
    string repo = "", string app = "", string port = "") : ObservableObject
{
    public string Key => key;
    public string Name => name;
    public string Repo => repo;
    public string App => app.Length > 0 ? app : key[(key.IndexOf('/') + 1)..];
    public string Port => port.Length > 0 ? port : job ? "job" : "";
    public string Folder => folder;
    public string Url => url;
    public string Values => values;
    public bool IsJob => job;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLine), nameof(IsReady), nameof(IsStoppedOrDone), nameof(IsFinished))]
    private string _state = "not running";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLine))]
    private string _detail = "";

    [ObservableProperty] private bool _isGood;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblem), nameof(IsStoppedOrDone))]
    private bool _isBad;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStoppedOrDone))]
    private bool _isAlive;

    [ObservableProperty] private int _restarts;

    /// <summary>Its log is the one showing.</summary>
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isPinned;

    public bool HasDetail => Detail.Length > 0;
    public string StateLine => Detail.Length == 0 ? State
        : Detail.StartsWith(State, StringComparison.OrdinalIgnoreCase) ? Detail : $"{State} · {Detail}";
    public bool IsReady => State == "ready";
    public bool IsFinished => State == "finished";

    /// <summary>Exited, failed, or going round restarts: worth a look.</summary>
    public bool IsProblem => IsBad || State == "restarting";

    public bool IsStoppedOrDone => !IsAlive && !IsBad;

    partial void OnDetailChanged(string value) => OnPropertyChanged(nameof(HasDetail));

    partial void OnStateChanged(string value) => OnPropertyChanged(nameof(IsProblem));

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
        Restarts = status?.Restarts ?? 0;
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
    public override string? NavSubtitle => "running copies";

    public ObservableCollection<SproutItem> Sprouts { get; } = [];
    public ObservableCollection<string> TrellisNames { get; } = [];
    public ObservableCollection<PendingTrustItem> PendingTrust { get; } = [];

    /// <summary>The files waiting for approval, read and approved one at a time.</summary>
    public CodeReviewViewModel Review { get; }

    [ObservableProperty] private SproutItem? _selected;
    [ObservableProperty] private IReadOnlyList<SproutAppRow> _apps = [];
    [ObservableProperty] private IReadOnlyList<SproutInfraRow> _infra = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings), nameof(WarningsBadge))]
    private IReadOnlyList<ProblemRow> _warnings = [];

    public bool HasWarnings => Warnings.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrustInModal), nameof(TrustOnPage), nameof(MessageInModal), nameof(MessageOnPage))]
    private bool _isAddOpen;
    [ObservableProperty] private string? _newTrellis;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newBranch = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewHasWorktrees), nameof(NewNameHint))]
    private bool _newInPlace;
    [ObservableProperty] private string _newFrom = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewStablePorts))]
    private bool _newRandomPorts;

    // Each choice's other answer, for the second radio button of its pair.
    public bool NewHasWorktrees
    {
        get => !NewInPlace;
        set => NewInPlace = !value;
    }

    public bool NewStablePorts
    {
        get => !NewRandomPorts;
        set => NewRandomPorts = !value;
    }
    public string NewNameHint => NewInPlace ? "Default: local" : "e.g. feat-x";

    /// <summary>A trellis's sprouts start from the choices its last one was made with.</summary>
    partial void OnNewTrellisChanged(string? value)
    {
        if (value is null) return;
        var last = _services.Ui.SproutChoices(value);
        NewInPlace = last.InPlace;
        NewFrom = last.From ?? "";
        NewRandomPorts = last.RandomPorts;
    }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageInModal), nameof(MessageOnPage))]
    private string _formMessage = "";
    [ObservableProperty] private bool _formFailed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrustInModal), nameof(TrustOnPage))]
    private bool _hasPendingTrust;

    // Approvals and form messages belong to whichever is in front: the new-sprout modal, or the selected sprout.
    public bool TrustInModal => HasPendingTrust && IsAddOpen;
    public bool TrustOnPage => HasPendingTrust && !IsAddOpen;
    public bool MessageInModal => FormMessage.Length > 0 && IsAddOpen;
    public bool MessageOnPage => FormMessage.Length > 0 && !IsAddOpen;

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
        Review = new CodeReviewViewModel(services.Trust) { AllApproved = ApproveAll };
        _services.HostStatus.Changed += ApplyRunners;
        _lastRunners = _services.HostStatus.Runners;
        DetailsOpen = _services.Ui.DetailsOpen;
        Logs.ShowTimes = _services.Ui.LogTimes;
        Logs.Wrap = _services.Ui.LogWrap;
        Refresh();
    }

    protected override void OnActivated() => Refresh();

    /// <summary>Raised after the list of sprouts is read again, so the nav can follow.</summary>
    public event Action? SproutsChanged;

    [RelayCommand]
    private void Refresh()
    {
        var keep = Selected?.Record.Key;
        Sprouts.Clear();
        foreach (var record in _services.Sprouts.List()) Sprouts.Add(new SproutItem(record));
        NavCount = Sprouts.Count(s => s.IsUp);

        var trellis = NewTrellis;
        TrellisNames.Clear();
        foreach (var entry in _services.Trellises.List()) TrellisNames.Add(entry.Id);
        // A repo can sprout on its own; IDs are unique across trellises and repos, so the names don't collide.
        foreach (var repo in _services.Stored.Repos()) TrellisNames.Add(repo.Id);
        NewTrellis = TrellisNames.Contains(trellis ?? "") ? trellis : TrellisNames.FirstOrDefault();

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasTrellises));
        Selected = Sprouts.FirstOrDefault(s => s.Record.Key == keep);
        BuildOverview();
        if (Selected is null && IsActive) _ = LoadOverviewStatusAsync();
        SproutsChanged?.Invoke();
    }

    /// <summary>Show one sprout, by its key; null shows them all.</summary>
    public void Show(string? key)
    {
        Selected = key is null ? null : Sprouts.FirstOrDefault(s => s.Record.Key == key);
        if (Selected is null) _ = LoadOverviewStatusAsync();
    }

    /// <summary>The header offers the one that applies: take an up sprout down, or bring any other up.</summary>
    public bool ShowTakeDown => Selected?.IsUp == true;
    public bool ShowBringUp => !ShowTakeDown;

    partial void OnSelectedChanged(SproutItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanDeleteBranch));
        OnPropertyChanged(nameof(ShowTakeDown));
        OnPropertyChanged(nameof(ShowBringUp));
        ConfirmingRemove = false;
        DeleteBranch = false;
        ShowDetails(value?.Record);
    }

    void ShowDetails(SproutRecord? record)
    {
        var plan = record is null ? null : _services.Sprouts.LastPlan(record.Trellis, record.Name);
        _plan = plan;
        SproutLinks = plan?.Links ?? [];
        AppsMessage = "";
        if (record is null || plan is null)
        {
            Apps = [];
            Infra = [];
            Warnings = [];
            LoadWorkspace(null, null);
            return;
        }

        // A plan newer than the record (a repo added since the sprout last came up) names repos the record hasn't got;
        // those apps wait for the next bring-up rather than breaking the page.
        Apps = plan.Apps.Where(app => record.Repos.Any(r => r.RepoPath == app.RepoPath)).Select(app =>
        {
            var repo = record.Repos.First(r => r.RepoPath == app.RepoPath);
            var dir = Path.GetFullPath(Path.Combine(repo.Dir, app.Path));
            var port = plan.Ports.FirstOrDefault(p => p.Owner == app.Owner && p.Resource == app.Name)?.Port;
            var files = record.Files.Where(f => f.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetRelativePath(dir, f.Path).Replace('\\', '/')).ToArray();
            return new SproutAppRow(app.Key, $"{repo.Name} / {app.Name}", dir.Replace('\\', '/'), port is null ? "" : $"http://localhost:{port}",
                files.Length > 0 ? string.Join(", ", files) : $"none; sprig env {record.Trellis} {record.Name} {app.Name}", app.Job,
                repo.Name, app.Name, port?.ToString() ?? "");
        }).ToArray();
        ApplyRunners(_lastRunners);
        LoadWorkspace(record, plan);

        Infra = plan.Containers.Select(c => new SproutInfraRow($"{c.Owner} / {c.Name}", string.Join(", ", c.Ports)))
            .Concat(plan.Compose.SelectMany(c => c.Services.Select(s => new SproutInfraRow($"{c.Owner} / {s.Name}", string.Join(", ", s.Mappings)))))
            .ToArray();

        Warnings = PlanView.Rows(plan.Problems.Where(p => p.Severity == ProblemSeverity.Warning), Path.GetDirectoryName(record.TrellisPath)!).ToArray();
    }

    // ---- running the selected sprout's apps ----

    IReadOnlyList<RunnerStatus>? _lastRunners;
    Plan? _plan;
    bool _polling;

    /// <summary>The selected sprout's logs: every app interleaved, or one app's.</summary>
    public SproutLogPane Logs { get; } = new();

    /// <summary>The trellis's quick links, at this sprout's ports.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSproutLinks))]
    private IReadOnlyList<PlannedLink> _sproutLinks = [];

    public bool HasSproutLinks => SproutLinks.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsMessage))]
    private string _appsMessage = "";

    [ObservableProperty] private bool _appsMessageIsBad;

    public bool HasAppsMessage => AppsMessage.Length > 0;

    /// <summary>Show what the host says about each app of the selected sprout; null when no host is running.</summary>
    public void ApplyRunners(IReadOnlyList<RunnerStatus>? runners)
    {
        _lastRunners = runners;
        var key = Selected?.Record.Key;
        foreach (var row in Apps)
            row.Apply(runners?.FirstOrDefault(r => r.Sprout == key && r.Name == row.Key));
        StatesChanged();
        if (Selected is null) CountOverviewApps();
    }

    /// <summary>Read the logs. The view calls this every second while the page shows; the app-wide
    /// <see cref="HostStatus"/> poll brings the apps' states.</summary>
    public async Task PollAsync()
    {
        if (_polling || Selected is null) return;
        _polling = true;
        try
        {
            await Logs.PollAsync();
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Ask the host now rather than at the next tick, after something here changed what's running.</summary>
    async Task RefreshRunnersAsync()
    {
        await _services.HostStatus.PollAsync();
        await PollAsync();
    }

    [RelayCommand]
    private async Task StartApps() => await StartAsync(null);

    [RelayCommand]
    private async Task StartApp(SproutAppRow row) => await StartAsync(row);

    /// <summary>Every app (null), leaving alone what's already going; or one app, started afresh, after what it
    /// depends on. Starting means everything it needs too: a sprout that isn't up is brought up first.</summary>
    async Task StartAsync(SproutAppRow? row)
    {
        if (Selected?.Record is not { } record) return;
        if (record.State != SproutState.Up)
        {
            var key = row?.Key;
            if (!await UpAsync(retry: () => StartAsync(key is null ? null : Apps.FirstOrDefault(a => a.Key == key)))) return;
            if (Selected?.Record is not { State: SproutState.Up } up) return;
            record = up;
            // Bringing it up reads the sprout afresh, so the row is a new one.
            if (key is not null) row = Apps.FirstOrDefault(a => a.Key == key);
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
            if (row is not null) OpenApp(row);
            await RefreshRunnersAsync();
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
        await RefreshRunnersAsync();
    }

    void SayApps(string message, bool bad)
    {
        AppsMessage = message;
        AppsMessageIsBad = bad;
    }

    // ---- making and changing sprouts ----

    [RelayCommand]
    private void OpenAdd()
    {
        FormMessage = "";
        FormFailed = false;
        PendingTrust.Clear();
        HasPendingTrust = false;
        IsAddOpen = true;
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsAddOpen = false;
        FormMessage = "";
        PendingTrust.Clear();
        HasPendingTrust = false;
        _afterApproval = null;
    }

    [RelayCommand]
    private async Task Create()
    {
        FormMessage = "";
        FormFailed = false;
        if (NewTrellis is not { } name || _services.Trellises.Resolve(name) is not { } path)
        {
            Fail("Choose a trellis or a repo. Add one on the Trellises or Repos page first.");
            return;
        }

        var inPlace = NewInPlace;
        var branch = !inPlace && NewBranch.Trim() is { Length: > 0 } b ? b : null;
        var sprout = NewName.Trim() is { Length: > 0 } n ? n : branch is not null ? SproutService.NameFromBranch(branch) : inPlace ? "local" : null;
        if (sprout is null)
        {
            Fail("Name the sprout (or give a branch). It gets worktrees of its own on a branch of that name.");
            return;
        }
        if (SproutService.CheckName(sprout) is { } bad)
        {
            Fail(bad);
            return;
        }

        var loaded = await LoadAsync(path, Create, sprout);
        if (loaded is null) return;

        var options = new SproutOptions(inPlace, !inPlace && NewFrom.Trim() is { Length: > 0 } from ? from : null, NewRandomPorts);
        _services.Ui.SetSproutChoices(name, options);

        // The progress window takes over from here, and reports any failure.
        IsAddOpen = false;
        var ok = await RunAsync($"Sprouting {loaded.Id} / {sprout}",
            (progress, cancellation) => _services.Sprouts.Up(loaded, sprout, branch, options, progress, cancellation),
            $"{loaded.Id} / {sprout} is up.", retry: Create);
        if (ok)
        {
            NewName = "";
            NewBranch = "";
        }
        Refresh();
        Selected = Sprouts.FirstOrDefault(s => s.Record.Trellis == loaded.Id && s.Record.Name == sprout) ?? Selected;
    }

    [RelayCommand]
    private Task Up() => UpAsync(retry: Up);

    /// <summary>Bring the selected sprout up, asking for approval first when a definition needs it. True once it's up.</summary>
    /// <param name="retry">What to carry on with once the user approves.</param>
    async Task<bool> UpAsync(Func<Task> retry)
    {
        if (Selected?.Record is not { } record) return false;
        var loaded = await LoadAsync(record.TrellisPath, retry, record.Name);
        if (loaded is null) return false;
        var ok = await RunAsync($"Bringing {record.Trellis} / {record.Name} up",
            (progress, cancellation) => _services.Sprouts.Up(loaded, record.Name, record.Branch, progress: progress, cancellation: cancellation),
            $"{record.Trellis} / {record.Name} is up.",
            retry: retry);
        Refresh();
        return ok;
    }

    [RelayCommand]
    private async Task Down()
    {
        if (Selected?.Record is not { } record) return;
        await RunAsync($"Stopping {record.Trellis} / {record.Name}",
            (progress, cancellation) => _services.Sprouts.Down(record.Trellis, record.Name, progress, cancellation),
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
            (progress, cancellation) => _services.Sprouts.Remove(record.Trellis, record.Name, options, progress, cancellation),
            $"{record.Trellis} / {record.Name} is gone.");
        Refresh();
    }

    [RelayCommand]
    private async Task ApproveAll()
    {
        foreach (var item in PendingTrust) _services.Trust.Approve(item.TrustPath, item.Sha256);
        PendingTrust.Clear();
        HasPendingTrust = false;
        Review.Clear();
        if (_afterApproval is { } next)
        {
            _afterApproval = null;
            await next();
        }
    }

    /// <summary>Load a trellis, asking for approval first when a definition needs it. Null when it can't be used yet;
    /// the form says why.</summary>
    /// <param name="sprout">When it names a sprout with worktrees, the repos' definitions are read from those.</param>
    async Task<LoadedTrellis?> LoadAsync(string path, Func<Task> retry, string? sprout = null)
    {
        IsWorking = true;
        try
        {
            var loaded = await AppServices.RunAsync(() =>
                _services.Loader.Load(path, sprout is null ? null : _services.Sprouts.Worktrees(path, sprout)));
            if (loaded.Untrusted.Count > 0)
            {
                AskForTrust(loaded.Untrusted, retry);
                return null;
            }
            if (loaded.Trellis is null || loaded.Problems.Count > 0)
            {
                var page = loaded.Alone ? "Repos" : "Trellises";
                Fail("It can't be planned: " + string.Join("; ", loaded.Problems.Take(3).Select(p => p.Message)) +
                     $". The {page} page shows the details.");
                return null;
            }
            return loaded;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail($"It couldn't be read: {e.Message}");
            return null;
        }
        finally
        {
            IsWorking = false;
        }
    }

    void AskForTrust(IEnumerable<NeedsTrust> pending, Func<Task> then)
    {
        PendingTrust.Clear();
        foreach (var p in pending) PendingTrust.Add(PendingTrustItem.From(p));
        HasPendingTrust = PendingTrust.Count > 0;
        _afterApproval = then;
        Review.Load(PendingTrust);
        // Asked for because the user just started something, so the review opens straight away.
        Review.OpenCommand.Execute(null);
    }

    /// <summary>An error's first paragraph: what went wrong. The rest (a service's log, say) is already on the failed
    /// step, so the summary under the steps stays short.</summary>
    internal static string Headline(string message)
    {
        var end = message.IndexOf("\n\n", StringComparison.Ordinal);
        return (end < 0 ? message : message[..end]).Trim();
    }

    /// <summary>Run one operation off the UI thread, reporting into a progress window.</summary>
    /// <param name="retry">Run again once the user approves definitions the operation found unapproved.</param>
    async Task<bool> RunAsync(string heading, Action<IProgress<SproutEvent>, CancellationToken> work, string success, Func<Task>? retry = null)
    {
        var window = new OperationProgressViewModel(heading);
        var progress = new Progress<SproutEvent>(window.Apply);
        ProgressStarted?.Invoke(window);
        IsWorking = true;
        var running = AppServices.RunAsync(() => work(progress, window.Cancellation));
        try
        {
            await FinishedOrAbandoned(running, window.Cancellation);
            // Progress<T> posts its reports, so let the last ones land before the summary.
            await Task.Yield();
            window.Finish(success, ProgressState.Done);
            return true;
        }
        catch (OperationCanceledException)
        {
            await Task.Yield();
            window.Finish(running.IsCompleted
                ? "Cancelled part-way. What had finished stays done; run it again to finish the rest."
                : "Cancelled, but the step it was on hasn't let go and is still going in the background. Docker has probably stopped answering: restarting Docker Desktop frees it.",
                ProgressState.Warning);
            return false;
        }
        catch (SproutException e) when (e.Untrusted.Count > 0 && retry is not null)
        {
            AskForTrust(e.Untrusted, retry);
            window.Finish("The sprout's branch changes these definitions, so they need approving first:\n"
                + string.Join("\n", e.Untrusted.Select(u => u.Path.Replace('\\', '/')))
                + "\nReview them in the main window and it carries on.", ProgressState.Error);
            return false;
        }
        catch (Exception e)
        {
            var problems = e is SproutException se ? se.Problems.Where(p => p.Severity == ProblemSeverity.Error).Take(4).Select(p => p.Message) : [];
            window.Finish(string.Join("\n", new[] { Headline(e.Message) }.Concat(problems)), ProgressState.Error);
            return false;
        }
        finally
        {
            // Abandoned work keeps the page's up and down buttons off until it really ends, so two never overlap.
            if (running.IsCompleted) IsWorking = false;
            else _ = running.ContinueWith(_ => Dispatcher.UIThread.Post(() => IsWorking = false), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The work's own outcome; or, once it has been cancelled and still hasn't stopped a few seconds later, an
    /// <see cref="OperationCanceledException"/> without it. Whatever it was blocked on (a docker that has stopped
    /// answering, say) may never notice the cancel, and the window must not be held open by it.
    /// </summary>
    public static async Task FinishedOrAbandoned(Task running, CancellationToken cancellation, TimeSpan? patience = null)
    {
        var asked = Task.Delay(Timeout.Infinite, cancellation);
        if (await Task.WhenAny(running, asked) != running
            && await Task.WhenAny(running, Task.Delay(patience ?? TimeSpan.FromSeconds(5))) != running)
            throw new OperationCanceledException(cancellation);
        await running;
    }

    void Fail(string message)
    {
        FormFailed = true;
        FormMessage = message;
    }
}
