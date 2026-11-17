using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Sprig.Engine.Hosting;

namespace Sprig.App;

/// <summary>
/// Apps run in the sprig host, not in a window, so closing the last window while they run would leave them going with
/// nothing on screen to stop them. Instead sprig stays in the notification area, where a window can be opened again or
/// sprig quit. Quitting always stops the apps first: apps left running with no sprig to show them are orphans nobody
/// asked for.
/// </summary>
public sealed class Tray
{
    readonly IClassicDesktopStyleApplicationLifetime _desktop;
    readonly AppWindows _windows;
    readonly AppServices _services;
    readonly TrayIcon _icon;
    readonly NativeMenuItem _status = new() { IsEnabled = false };
    readonly NativeMenuItem _quit = new("Quit");
    bool _quitting;
    int _alive;

    public Tray(Application app, IClassicDesktopStyleApplicationLifetime desktop, AppWindows windows, AppServices services, WindowIcon? icon)
    {
        _desktop = desktop;
        _windows = windows;
        _services = services;

        var open = new NativeMenuItem("Open sprig");
        open.Click += (_, _) => _windows.ShowHere();
        var another = new NativeMenuItem("New window");
        another.Click += (_, _) => _windows.Open();
        _quit.Click += (_, _) => Quit();
        _icon = new TrayIcon
        {
            Icon = icon,
            ToolTipText = "sprig",
            Menu = new NativeMenu { Items = { open, another, _status, new NativeMenuItemSeparator(), _quit } },
        };
        _icon.Clicked += (_, _) => _windows.ShowHere();
        TrayIcon.SetIcons(app, [_icon]);

        services.HostStatus.Changed += Apply;
        Apply(services.HostStatus.Runners);
        desktop.Exit += (_, _) =>
        {
            _icon.IsVisible = false;
            _icon.Dispose();
            services.Host.ShutdownIfIdle();
        };
        SingleInstance.OpenRequested += workspace => Dispatcher.UIThread.Post(() => _windows.ShowHere(workspace));
    }

    void Apply(IReadOnlyList<RunnerStatus>? runners)
    {
        var alive = runners?.Where(r => r.State.IsAlive() || r.State == RunnerState.Stopping).ToArray() ?? [];
        _alive = alive.Length;
        if (_quitting) return;
        var workspaces = alive.Select(r => r.Workspace).Distinct().Count();
        var summary = _alive == 0 ? "No apps running"
            : $"{_alive} app{(_alive == 1 ? "" : "s")} running in {workspaces} workspace{(workspaces == 1 ? "" : "s")}";
        _status.Header = summary;
        _icon.ToolTipText = $"sprig · {summary.ToLowerInvariant()}";
        _quit.Header = _alive > 0 ? "Stop all apps and quit" : "Quit";
    }

    /// <summary>Stop every app, then the host, then quit. The app count can lag the host by a poll, so the host is
    /// always asked rather than trusting it.</summary>
    public async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _windows.Quitting();
        _quit.IsEnabled = false;
        if (_alive > 0)
        {
            _status.Header = "Stopping every app…";
            _icon.ToolTipText = "sprig · stopping every app…";
        }
        await AppServices.RunAsync(() => _services.Host.StopAllAndShutdown());
        _desktop.Shutdown();
    }
}
