using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Workspaces;

namespace Sprig.App.ViewModels;

public partial class WorkspacesViewModel : PageViewModel
{
    protected readonly AppServices Services;

    public WorkspacesViewModel(AppServices services)
    {
        Services = services;
        _ = RefreshAsync();
    }

    public override string Title => "Workspaces";

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(DownCommand), nameof(ResetCommand),
        nameof(ReconcileCommand), nameof(RepairCommand), nameof(OpenCommand), nameof(RemoveCommand))]
    private WorkspaceItemViewModel? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand), nameof(DownCommand), nameof(ResetCommand),
        nameof(ReconcileCommand), nameof(RepairCommand), nameof(OpenCommand), nameof(RemoveCommand),
        nameof(RefreshCommand), nameof(NewWorkspaceCommand))]
    private bool _busy;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _statusMessage;

    // Remove confirmation state.
    [ObservableProperty] private bool _confirmingRemove;
    [ObservableProperty] private bool _removeForce;

    // Create-workspace flow.
    [ObservableProperty] private bool _isCreating;
    [ObservableProperty] private string? _newStack;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string? _createError;
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
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RefreshAsync()
    {
        var previouslySelected = Selected?.Name;
        await Guard(async () =>
        {
            var records = await AppServices.RunAsync(() => Services.Workspaces.List());
            Workspaces.Clear();
            foreach (var r in records.OrderBy(r => r.Workspace))
                Workspaces.Add(new WorkspaceItemViewModel(r));
            OnPropertyChanged(nameof(HasWorkspaces));
            Selected = Workspaces.FirstOrDefault(w => w.Name == previouslySelected) ?? Workspaces.FirstOrDefault();
        }, status: null);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task NewWorkspace()
    {
        CreateError = null;
        NewName = "";
        NewStack = null;
        var names = await AppServices.RunAsync(() => Services.Stacks.List().Select(s => s.Name).ToList());
        AvailableStacks.Clear();
        foreach (var n in names) AvailableStacks.Add(n);
        NewStack = AvailableStacks.FirstOrDefault();
        IsCreating = true;
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
            await AppServices.RunAsync(() =>
            {
                var resolved = Services.StackResolver.Resolve(stack);
                Services.Workspaces.Create(resolved, name);
            });
            IsCreating = false;
            await RefreshCore();
            Selected = Workspaces.FirstOrDefault(w => w.Name == name) ?? Selected;
            StatusMessage = $"created '{name}'";
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

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Open()
    {
        var path = Selected?.Repos.FirstOrDefault()?.WorktreePath;
        if (string.IsNullOrEmpty(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { Error = ex.Message; }
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
        if (report is null) { item.Drift = "no record"; return; }
        foreach (var line in item.Repos)
        {
            var state = report.Repos.FirstOrDefault(r => r.WorktreePath == line.WorktreePath)?.State;
            line.State = state?.ToString() ?? "?";
        }
        item.Drift = report.IsHealthy ? "healthy" : report.HasDrift ? "DRIFT" : "gone";
    }

    async Task RefreshCore()
    {
        var keep = Selected?.Name;
        var records = await AppServices.RunAsync(() => Services.Workspaces.List());
        Workspaces.Clear();
        foreach (var r in records.OrderBy(r => r.Workspace))
            Workspaces.Add(new WorkspaceItemViewModel(r));
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
