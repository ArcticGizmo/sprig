using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Sprig.App.Updates;
using Sprig.App.ViewModels;
using Sprig.App.Views;
using Sprig.Engine.Hosting;
using Sprig.Interop;

namespace Sprig.App;

/// <summary>
/// Every sprig window in this process, and what they share. Someone who keeps a project per virtual desktop wants
/// sprig on each, and one process with several windows keeps one host subscription, one tray and one quit that stops
/// every app, where a process per window would each have their own.
/// </summary>
public sealed class AppWindows
{
    readonly IClassicDesktopStyleApplicationLifetime _desktop;
    readonly AppServices _services;
    readonly List<MainWindow> _windows = [];
    UpdateCheckResult? _update;
    bool _checkedForUpdates;
    bool _quitting;

    /// <summary>What the last window to close was showing, so opening sprig again from the tray goes back to it.</summary>
    string? _lastShown;

    public AppWindows(IClassicDesktopStyleApplicationLifetime desktop, AppServices services)
    {
        _desktop = desktop;
        _services = services;
        // Closing the last window leaves sprig in the tray while apps run; quitting is always asked for.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Hidden in the tray it still follows the host, so the tray's count of running apps stays true.
        _services.HostStatus.Follow(apply => Dispatcher.UIThread.Post(apply));
    }

    public IReadOnlyList<MainWindow> All => _windows;

    /// <summary>Raised when the last window has closed with apps still running, so sprig carries on in the tray.</summary>
    public event Action? AllClosed;

    /// <summary>A new window, on a workspace by its key, or on Home.</summary>
    public MainWindow Open(string? workspace = null)
    {
        // Only the first window checks for an update; later ones are told what it found.
        var vm = new MainWindowViewModel(_services, checkForUpdates: !_checkedForUpdates);
        if (!_checkedForUpdates)
        {
            _checkedForUpdates = true;
            vm.UpdateChecked += result =>
            {
                _update = result;
                foreach (var other in _windows.Select(w => w.DataContext).OfType<MainWindowViewModel>().Where(o => o != vm)) other.ApplyUpdate(result);
            };
        }
        else if (_update is { } update) vm.ApplyUpdate(update);
        vm.NewWindow = key => Open(key);
        if (workspace is not null) vm.ShowWorkspace(workspace);

        var window = new MainWindow { DataContext = vm };
        // Each new window steps down and right of the one in front, so it doesn't land exactly on top of it.
        if (_windows.OrderByDescending(w => w.LastActivated).FirstOrDefault() is { } front && front.IsShown && OnCurrentDesktop(front) != false)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new Avalonia.PixelPoint(front.Position.X + 32, front.Position.Y + 32);
        }
        _windows.Add(window);
        _desktop.MainWindow ??= window;
        window.ShownChanged += Follow;
        window.Closed += (_, _) => OnClosed(window, vm);
        window.Show();
        window.Activate();
        Follow();
        return window;
    }

    /// <summary>
    /// Sprig asked for again (launched, or from the tray): a window on the desktop being looked at comes forward, the
    /// one showing <paramref name="workspace"/> when one is named; failing that, a new window opens here rather than
    /// sending the user off to another desktop. Where the desktop can't be told, any window will do.
    /// </summary>
    public void ShowHere(string? workspace = null)
    {
        if (_quitting) return;
        var here = _windows.Where(w => OnCurrentDesktop(w) ?? true).OrderByDescending(w => w.LastActivated).ToList();
        var pick = workspace is null
            ? here.FirstOrDefault()
            : here.FirstOrDefault(w => (w.DataContext as MainWindowViewModel)?.ShownWorkspace == workspace);
        if (pick is null)
        {
            Open(workspace ?? (_windows.Count == 0 ? _lastShown : null));
            return;
        }
        if (pick.WindowState == WindowState.Minimized) pick.WindowState = WindowState.Normal;
        pick.Show();
        pick.Activate();
    }

    /// <summary>Sprig is quitting: windows close without leaving it in the tray.</summary>
    public void Quitting() => _quitting = true;

    void OnClosed(MainWindow window, MainWindowViewModel vm)
    {
        _lastShown = vm.ShownWorkspace;
        _windows.Remove(window);
        window.ShownChanged -= Follow;
        vm.Dispose();
        if (ReferenceEquals(_desktop.MainWindow, window)) _desktop.MainWindow = _windows.FirstOrDefault();
        Follow();
        if (_windows.Count > 0 || _quitting) return;
        if (AnyAlive()) AllClosed?.Invoke();
        else _desktop.Shutdown();
    }

    /// <summary>Docker is only asked while a window shows.</summary>
    void Follow() => _services.InfraUsage.WindowShown = _windows.Any(w => w.IsShown);

    static bool? OnCurrentDesktop(Window window) => VirtualDesktops.IsOnCurrent(window.TryGetPlatformHandle()?.Handle ?? 0);

    bool AnyAlive() => _services.HostStatus.Runners?.Any(r => r.State.IsAlive() || r.State == RunnerState.Stopping) == true;
}
