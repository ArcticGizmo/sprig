using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Updates;
using Sprig.Engine.Store;

namespace Sprig.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>Every navigable page, including the two pinned to the bottom of the nav.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Left-nav rows: page entries interleaved with section headers.</summary>
    public IReadOnlyList<object> NavItems { get; }

    public HomeViewModel Home { get; }
    public ComingSoonViewModel Trellises { get; }
    public ComingSoonViewModel Sprouts { get; }
    public EditorSpikeViewModel EditorSpike { get; }
    public SettingsViewModel Settings { get; }
    public AboutViewModel About { get; }

    [ObservableProperty]
    private PageViewModel _currentPage;

    /// <summary>Non-null when a newer version is available; drives the update entry in the left nav.</summary>
    [ObservableProperty]
    private string? _updateNotice;

    /// <summary>Held so "Update now" installs what the launch check found without checking again.</summary>
    UpdateCheckResult? _updateResult;

    public UpdateInstaller Updater { get; } = new();

    public bool ShowUpdateNotice => !string.IsNullOrEmpty(UpdateNotice);

    public bool IsDevInstance => AppProfile.IsDev;

    public static string WindowTitle => "Sprig (experimental)" + AppProfile.DisplaySuffix;

    /// <param name="checkForUpdates">Tests pass false so constructing the window never touches the network.</param>
    public MainWindowViewModel(AppServices services, bool checkForUpdates = true)
    {
        Home = new HomeViewModel();
        Trellises = new ComingSoonViewModel(
            title: "Trellises",
            heading: "Describe how your repos grow together",
            body: "A trellis is a C# file that puts repos together: which ones run, how their values connect, and whether they get worktrees and which ports. Each repo describes itself in a sprig.cs file, and the trellis joins them.",
            milestone: "M3");
        Sprouts = new ComingSoonViewModel(
            title: "Sprouts",
            heading: "Bring a trellis up in one step",
            body: "A sprout is one running copy of a trellis: its worktrees (or the repos in place), its ports, env files and containers, and later its apps, started in order.",
            milestone: "M4");
        EditorSpike = new EditorSpikeViewModel();
        Settings = new SettingsViewModel(services);
        About = new AboutViewModel(Updater);

        Pages = [Home, Trellises, Sprouts, EditorSpike, Settings, About];
        NavItems =
        [
            Home,
            new NavHeaderViewModel("DEFINE"),
            Trellises,
            new NavHeaderViewModel("RUN"),
            Sprouts,
            new NavHeaderViewModel("SPIKES"),
            EditorSpike,
        ];

        _currentPage = Home;
        Home.IsActive = true;

        if (checkForUpdates) _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => CurrentPage = page;

    /// <summary>Install the available update, then restart. Only returns when the install failed.</summary>
    [RelayCommand]
    private async Task UpdateNow()
    {
        if (_updateResult is not { Availability: UpdateAvailability.Available }) return;
        if (!await Updater.InstallAsync(_updateResult))
            UpdateNotice = "Update failed to install. Try again from the About page.";
    }

    partial void OnUpdateNoticeChanged(string? value) => OnPropertyChanged(nameof(ShowUpdateNotice));

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        foreach (var page in Pages)
            page.IsActive = ReferenceEquals(page, value);
    }

    async Task CheckForUpdatesAsync()
    {
        var result = await UpdateChecker.CheckDetailedAsync();
        _updateResult = result;

        if (result.Availability != UpdateAvailability.Available)
            return;

        UpdateNotice = $"Update available: v{result.AvailableVersion} (you have v{result.CurrentVersion})";
    }
}

/// <summary>A non-interactive section label in the left nav.</summary>
public sealed class NavHeaderViewModel(string label)
{
    public string Label { get; } = label;
}
