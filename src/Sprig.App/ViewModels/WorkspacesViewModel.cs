using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Workspaces;

namespace Sprig.App.ViewModels;

public partial class WorkspacesViewModel : PageViewModel
{
    protected readonly AppServices Services;
    readonly Navigator _nav;

    public WorkspacesViewModel(AppServices services, Navigator nav)
    {
        Services = services;
        _nav = nav;
        _ = RefreshAsync();
    }

    public override string Title => "Workspaces";

    /// <summary>False when no stacks exist — a workspace can't be created without one (upstream empty state).</summary>
    [ObservableProperty] private bool _hasAnyStacks;

    /// <summary>Empty-state shortcut: jump to Stacks and open the builder.</summary>
    [RelayCommand] private void BuildStack() => _nav.NewStack();

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(DownCommand), nameof(ResetCommand),
        nameof(ReconcileCommand), nameof(RepairCommand), nameof(RemoveCommand),
        nameof(RefreshStatusCommand), nameof(OpenDockerCommand))]
    private WorkspaceItemViewModel? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(DownCommand), nameof(ResetCommand),
        nameof(ReconcileCommand), nameof(RepairCommand), nameof(RemoveCommand),
        nameof(RefreshCommand), nameof(NewWorkspaceCommand),
        nameof(RefreshStatusCommand), nameof(OpenDockerCommand))]
    private bool _busy;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _statusMessage;

    // Remove confirmation state.
    [ObservableProperty] private bool _confirmingRemove;
    [ObservableProperty] private bool _removeForce;

    // Create-workspace flow.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockerWarning))]
    private bool _isCreating;
    [ObservableProperty] private string? _newStack;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _createError;

    /// <summary>Bring the new workspace's infra up right after creating it (default on).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockerWarning))]
    private bool _startInfraOnCreate = true;

    /// <summary>Whether the Docker engine is reachable, probed while the create modal is open.
    /// Optimistically true until the probe answers, so the warning never flashes before we know.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDockerWarning))]
    private bool _dockerRunning = true;

    /// <summary>True while the engine probe is in flight (drives the "Checking…" affordance).</summary>
    [ObservableProperty] private bool _checkingDocker;

    /// <summary>Warn in the create modal when "start infra" is asked for but the engine isn't running —
    /// so the user finds out before hitting Create, not as an after-the-fact soft warning.</summary>
    public bool ShowDockerWarning => IsCreating && StartInfraOnCreate && !DockerRunning && !CheckingDocker;

    partial void OnCheckingDockerChanged(bool value) => OnPropertyChanged(nameof(ShowDockerWarning));

    /// <summary>Re-probe the engine when "start infra" is (re)ticked — it may have come up meanwhile.</summary>
    partial void OnStartInfraOnCreateChanged(bool value)
    {
        if (value && IsCreating) _ = ProbeDockerAsync();
    }

    /// <summary>Probe the Docker engine off the UI thread and update <see cref="DockerRunning"/>.</summary>
    async Task ProbeDockerAsync()
    {
        CheckingDocker = true;
        try { DockerRunning = await AppServices.RunAsync(() => Services.Docker.IsEngineRunning()); }
        catch { DockerRunning = false; }
        finally { CheckingDocker = false; }
    }

    /// <summary>Re-run the engine probe (the modal's "Recheck" affordance after starting Docker).</summary>
    [RelayCommand]
    private Task RecheckDocker() => ProbeDockerAsync();

    /// <summary>Launch Docker Desktop from the create modal (no workspace selected yet, unlike
    /// <see cref="OpenDockerCommand"/>).</summary>
    [RelayCommand]
    private void OpenDockerDesktop()
    {
        try
        {
            var exe = DockerDesktopPath();
            if (exe is null)
            {
                CreateError = "Docker Desktop wasn't found in its default location — start it manually, then Recheck.";
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }
        catch (Exception ex) { CreateError = $"couldn't open Docker Desktop: {ex.Message}"; }
    }

    public ObservableCollection<string> AvailableStacks { get; } = [];

    bool HasSelection => Selected is not null && !Busy;
    bool NotBusy => !Busy;

    /// <summary>Bound by the view to toggle the empty-state vs. detail panel.</summary>
    public bool HasSelected => Selected is not null;

    /// <summary>False when no workspaces exist at all — drives the first-run empty state.</summary>
    public bool HasWorkspaces => Workspaces.Count > 0;

    partial void OnSelectedChanged(WorkspaceItemViewModel? value)
    {
        ConfirmingRemove = false;
        Error = null;
        StatusMessage = null;
        OnPropertyChanged(nameof(HasSelected));
        if (value is not null) _ = LoadStatusAsync(value);
    }

    /// <summary>Probe docker for the workspace's live containers and fill its display flags.</summary>
    async Task LoadStatusAsync(WorkspaceItemViewModel item)
    {
        if (!item.HasInfra) { item.SetContainers([], dockerAvailable: true); return; }
        try
        {
            var list = await AppServices.RunAsync(() => Services.Workspaces.Status(item.Name));
            item.SetContainers(list, dockerAvailable: true);
        }
        catch (WorkspaceException)
        {
            // RequireWithInfra throws this when docker compose isn't available.
            item.SetContainers([], dockerAvailable: false);
        }
        catch (Exception)
        {
            item.SetContainers([], dockerAvailable: false);
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        var previouslySelected = Selected?.Name;
        await Guard(async () =>
        {
            var records = await AppServices.RunAsync(() => Services.Workspaces.List());
            HasAnyStacks = await AppServices.RunAsync(() => Services.Stacks.List().Count) > 0;
            Workspaces.Clear();
            foreach (var r in records.OrderBy(r => r.Workspace))
                Workspaces.Add(new WorkspaceItemViewModel(r));
            OnPropertyChanged(nameof(HasWorkspaces));
            NavCount = Workspaces.Count;
            Selected = Workspaces.FirstOrDefault(w => w.Name == previouslySelected) ?? Workspaces.FirstOrDefault();
        }, status: null);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task NewWorkspace()
    {
        CreateError = null;
        NewName = "";
        NewStack = null;
        StartInfraOnCreate = true;
        var names = await AppServices.RunAsync(() => Services.Stacks.List().Select(s => s.Name).ToList());
        AvailableStacks.Clear();
        foreach (var n in names) AvailableStacks.Add(n);
        NewStack = AvailableStacks.FirstOrDefault();
        DockerRunning = true;   // optimistic until the probe answers
        IsCreating = true;
        if (StartInfraOnCreate) _ = ProbeDockerAsync();
    }

    [RelayCommand]
    private void CancelCreate() => IsCreating = false;

    [RelayCommand]
    private async Task Create()
    {
        var stack = NewStack;
        var name = NewName.Trim();
        if (string.IsNullOrEmpty(stack)) { CreateError = "pick a stack (define one in the Stacks tab first)"; return; }
        if (string.IsNullOrEmpty(name)) { CreateError = "enter a workspace name"; return; }

        Busy = true;
        CreateError = null;
        try
        {
            var record = await AppServices.RunAsync(() =>
            {
                var resolved = Services.StackResolver.Resolve(stack);
                return Services.Workspaces.Create(resolved, name);
            });
            IsCreating = false;

            // Optionally bring the infra up straight away. A failure here (e.g. Docker not running)
            // is a soft warning — the workspace itself was created successfully.
            var hasInfra = record.Repos.Any(r => r.ComposePaths.Count > 0);
            var started = false;
            string? infraWarning = null;
            if (hasInfra && StartInfraOnCreate)
            {
                try
                {
                    await AppServices.RunAsync(() => Services.Workspaces.Up(name));
                    started = true;
                }
                catch (Exception ex) { infraWarning = ex.Message; }
            }

            await RefreshCore();
            Selected = Workspaces.FirstOrDefault(w => w.Name == name) ?? Selected;
            StatusMessage = started ? $"created '{name}' and started its infra" : $"created '{name}'";
            // Both a setup failure and an infra-start failure are soft warnings — the workspace itself
            // was created. Surface whichever happened (setup first, since it ran first).
            var setupWarning = SetupWarning.Summarize(record);
            Error = setupWarning is not null ? $"created '{name}', but {setupWarning} — finish setup manually in the worktree"
                : infraWarning is not null ? $"created '{name}', but couldn't start its infra: {infraWarning}"
                : null;
            Services.NotifyStoreChanged();
        }
        catch (Exception ex)
        {
            CreateError = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Up() => Lifecycle(ws => Services.Workspaces.Up(ws), "infra up");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Down() => Lifecycle(ws => Services.Workspaces.Down(ws), "infra down");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Reset() => Lifecycle(ws => Services.Workspaces.Reset(ws), "infra reset");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Reconcile()
    {
        var item = Selected;
        if (item is null) return;
        await Guard(async () =>
        {
            var report = await AppServices.RunAsync(() => Services.Reconciler.Inspect(item.Name));
            ApplyDrift(item, report);
        }, status: "reconciled");
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Repair()
    {
        var item = Selected;
        if (item is null) return;
        await Guard(async () =>
        {
            await AppServices.RunAsync(() => Services.Reconciler.Repair(item.Name));
            var report = await AppServices.RunAsync(() => Services.Reconciler.Inspect(item.Name));
            ApplyDrift(item, report);
        }, status: "repaired");
    }

    /// <summary>Open one repo's worktree folder in the OS file manager.</summary>
    [RelayCommand]
    private void OpenRepoInExplorer(RepoLineViewModel? repo)
        => LaunchRepo(repo, p => new ProcessStartInfo { FileName = p, UseShellExecute = true });

    /// <summary>Open one repo's worktree in VS Code (<c>code &lt;path&gt;</c> on the PATH).</summary>
    [RelayCommand]
    private void OpenRepoInVsCode(RepoLineViewModel? repo)
        => LaunchRepo(repo, p => new ProcessStartInfo { FileName = "code", Arguments = $"\"{p}\"", UseShellExecute = true });

    /// <summary>Open a terminal at one repo's worktree (Windows Terminal, else a shell).</summary>
    [RelayCommand]
    private void OpenRepoInTerminal(RepoLineViewModel? repo)
    {
        var path = repo?.WorktreePath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (OperatingSystem.IsWindows())
                // Prefer Windows Terminal; fall back to PowerShell if wt isn't installed.
                try { Process.Start(new ProcessStartInfo { FileName = "wt.exe", Arguments = $"-d \"{path}\"", UseShellExecute = true }); }
                catch { Process.Start(new ProcessStartInfo { FileName = "powershell.exe", WorkingDirectory = path, UseShellExecute = true }); }
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo { FileName = "open", Arguments = $"-a Terminal \"{path}\"" });
            else
                Process.Start(new ProcessStartInfo { FileName = "x-terminal-emulator", WorkingDirectory = path });
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    /// <summary>Launch a process for a repo's worktree path; surfaces any failure in <see cref="Error"/>.</summary>
    void LaunchRepo(RepoLineViewModel? repo, Func<string, ProcessStartInfo> build)
    {
        var path = repo?.WorktreePath;
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(build(path)); }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RefreshStatus()
    {
        var item = Selected;
        if (item is not null) await LoadStatusAsync(item);
    }

    /// <summary>Launch Docker Desktop so the user can inspect this workspace's <c>sprig-&lt;name&gt;</c> project.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenDocker()
    {
        try
        {
            var exe = DockerDesktopPath();
            if (exe is null)
            {
                Error = "Docker Desktop wasn't found in its default location. Open it manually to see the "
                      + $"'sprig-{Selected?.Name}' project.";
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
        }
        catch (Exception ex) { Error = $"couldn't open Docker Desktop: {ex.Message}"; }
    }

    /// <summary>Docker Desktop's exe in either Program Files location, or null if not installed there.</summary>
    static string? DockerDesktopPath()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root)) continue;
            var exe = Path.Combine(root, "Docker", "Docker", "Docker Desktop.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove() => ConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => ConfirmingRemove = false;

    [RelayCommand]
    private async Task ConfirmRemove()
    {
        var item = Selected;
        var force = RemoveForce;
        if (item is null) return;
        ConfirmingRemove = false;
        await Guard(async () =>
        {
            await AppServices.RunAsync(() => Services.Workspaces.Remove(item.Name, force));
            await RefreshCore();
            Services.NotifyStoreChanged();
        }, status: $"removed '{item.Name}'");
    }

    async Task Lifecycle(Action<string> action, string ok)
    {
        var name = Selected?.Name;
        if (name is null) return;
        await Guard(async () =>
        {
            await AppServices.RunAsync(() => action(name));
            await RefreshCore();
        }, status: ok);
    }

    static void ApplyDrift(WorkspaceItemViewModel item, WorkspaceReconcile? report)
    {
        if (report is null) { item.Drift = "not checked"; return; }
        foreach (var line in item.Repos)
            line.DriftState = report.Repos.FirstOrDefault(r => r.WorktreePath == line.WorktreePath)?.State;
        item.Drift = report.IsHealthy ? "in sync"
            : report.HasDrift ? "drift detected — run Repair"
            : "worktrees gone";
    }

    async Task RefreshCore()
    {
        var keep = Selected?.Name;
        var records = await AppServices.RunAsync(() => Services.Workspaces.List());
        HasAnyStacks = await AppServices.RunAsync(() => Services.Stacks.List().Count) > 0;
        Workspaces.Clear();
        foreach (var r in records.OrderBy(r => r.Workspace))
            Workspaces.Add(new WorkspaceItemViewModel(r));
        OnPropertyChanged(nameof(HasWorkspaces));
        NavCount = Workspaces.Count;
        Selected = Workspaces.FirstOrDefault(w => w.Name == keep) ?? Workspaces.FirstOrDefault();
    }

    async Task Guard(Func<Task> action, string? status)
    {
        Busy = true;
        Error = null;
        StatusMessage = null;
        try
        {
            await action();
            if (status is not null) StatusMessage = status;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
