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
/// Apps run in the sprig host, not in this window, so closing the window while they run would leave them going with
/// nothing on screen to stop them. Instead the window hides and sprig stays in the notification area, where it can be
/// opened again, or quit with or without stopping the apps. Closed with nothing running, sprig ends the host too.
/// </summary>
public sealed class Tray
{
    readonly IClassicDesktopStyleApplicationLifetime _desktop;
    readonly Window _main;
    readonly AppServices _services;
    readonly TrayIcon _icon;
    readonly NativeMenuItem _status = new() { IsEnabled = false };
    readonly NativeMenuItem _stopAll = new("Stop all apps and quit");
    readonly NativeMenuItem _quit = new("Quit");
    bool _quitting;
    int _alive;

    public Tray(Application app, IClassicDesktopStyleApplicationLifetime desktop, Window main, AppServices services)
    {
        _desktop = desktop;
        _main = main;
        _services = services;

        var open = new NativeMenuItem("Open sprig");
        open.Click += (_, _) => Show();
        _stopAll.Click += (_, _) => StopAllAndQuit();
        _quit.Click += (_, _) => Quit();
        _icon = new TrayIcon
        {
            Icon = main.Icon,
            ToolTipText = "sprig",
            Menu = new NativeMenu { Items = { open, _status, new NativeMenuItemSeparator(), _stopAll, _quit } },
        };
        _icon.Clicked += (_, _) => Show();
        TrayIcon.SetIcons(app, [_icon]);

        services.HostStatus.Changed += Apply;
        Apply(services.HostStatus.Runners);
        main.Closing += OnClosing;
        desktop.Exit += (_, _) =>
        {
            _icon.IsVisible = false;
            _icon.Dispose();
            services.Host.ShutdownIfIdle();
        };
        SingleInstance.ShowRequested += () => Dispatcher.UIThread.Post(Show);
    }

    void Apply(IReadOnlyList<RunnerStatus>? runners)
    {
        var alive = runners?.Where(r => r.State.IsAlive() || r.State == RunnerState.Stopping).ToArray() ?? [];
        _alive = alive.Length;
        var sprouts = alive.Select(r => r.Sprout).Distinct().Count();
        var summary = _alive == 0 ? "No apps running"
            : $"{_alive} app{(_alive == 1 ? "" : "s")} running in {sprouts} sprout{(sprouts == 1 ? "" : "s")}";
        _status.Header = summary;
        _icon.ToolTipText = $"sprig · {summary.ToLowerInvariant()}";
        _stopAll.IsEnabled = _alive > 0;
        _quit.Header = _alive > 0 ? "Quit, leave apps running" : "Quit";
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown || _alive == 0) return;
        e.Cancel = true;
        _main.Hide();
    }

    public void Show()
    {
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    async void StopAllAndQuit()
    {
        _stopAll.IsEnabled = false;
        _quit.IsEnabled = false;
        _status.Header = "Stopping every app…";
        _icon.ToolTipText = "sprig · stopping every app…";
        await AppServices.RunAsync(() => _services.Host.StopAllAndShutdown());
        Quit();
    }

    void Quit()
    {
        _quitting = true;
        _desktop.Shutdown();
    }
}
