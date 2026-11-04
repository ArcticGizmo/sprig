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

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            var vm = new MainWindowViewModel(services);
            var main = new MainWindow
            {
                DataContext = vm,
                Title = MainWindowViewModel.WindowTitle,
            };

            desktop.MainWindow = main;
            // The language host is a child process; don't leave it running after the app closes.
            desktop.Exit += (_, _) =>
            {
                vm.EditorSpike.Host.Dispose();
                services.Language.Dispose();
            };
            _ = new Tray(this, desktop, main, services);
            MaybeShowChangelog(services, main);
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
                owner.Opened += (_, _) => window.Show(owner);
            }
        }

        if (settings.LastSeenVersion != current)
        {
            settings.LastSeenVersion = current;
            services.Settings.Save(settings);
        }
    }
}
