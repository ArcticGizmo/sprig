using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sprig.App.Changelog;
using Sprig.App.ViewModels;
using Sprig.App.Views;
using Sprig.Engine.Changelog;

namespace Sprig.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>The workspace the first window opens on, by its key (<c>sprig open storefront 2</c>); null for Home.</summary>
    public static string? StartWorkspace { get; set; }

    /// <summary>Start in the tray with no window, as at sign-in.</summary>
    public static bool StartInTray { get; set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            var windows = new AppWindows(desktop, services);
            // What's new waits for a window to show on, however long sprig sits in the tray first.
            windows.FirstOpened += main => MaybeShowChangelog(services, main);
            if (!StartInTray) windows.Open(StartWorkspace);
            var tray = new Tray(this, desktop, windows, services);
            TerminalStop.Listen(() => Avalonia.Threading.Dispatcher.UIThread.Post(tray.Quit));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// On the first launch after an update, show the changelog entries newer than the version that last ran
    /// here. Records the running version as last-seen so it shows once per update.
    /// </summary>
    static void MaybeShowChangelog(AppServices services, Window owner)
    {
        var settings = services.Settings.Get();
        var current = AboutViewModel.Current;

        if (settings.ShowChangelogOnUpdate && ChangelogMarkdown.LoadEmbedded() is { } markdown)
        {
            var unseen = ChangelogParser.UnseenSince(markdown, settings.LastSeenVersion, current);
            if (unseen.Count > 0)
            {
                var window = new ChangelogWindow("What's new in sprig", $"Updated to v{current}", unseen,
                    onSuppress: () =>
                    {
                        var s = services.Settings.Get();
                        s.ShowChangelogOnUpdate = false;
                        services.Settings.Save(s);
                    });
                // Wait for the main window so the popup can centre on it.
                if (owner.IsVisible) window.Show(owner);
                else owner.Opened += (_, _) => window.Show(owner);
            }
        }

        if (settings.LastSeenVersion != current)
        {
            settings.LastSeenVersion = current;
            services.Settings.Save(settings);
        }
    }
}
