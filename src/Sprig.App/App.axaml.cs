using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sprig.App.Changelog;
using Sprig.App.ViewModels;
using Sprig.App.Views;
using Sprig.Core.Changelog;

namespace Sprig.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            var main = new MainWindow { DataContext = new MainWindowViewModel(services) };
            desktop.MainWindow = main;
            MaybeShowChangelog(services, main);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// On the first launch after an update, pop a "what's new" window listing the changelog entries
    /// newer than the version that last ran here. Records the running version as last-seen so it only
    /// shows once per update; honours the user's "don't show" preference.
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

        // Remember this version regardless, so the popup fires once per update, not every launch.
        if (settings.LastSeenVersion != current)
        {
            settings.LastSeenVersion = current;
            services.Settings.Save(settings);
        }
    }
}
