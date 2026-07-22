using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Updates;

namespace Sprig.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>The navigable pages, in workflow order: Home, then Repos, Stacks, Workspaces.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Left-nav rows: page entries interleaved with section headers ("Set up" / "Run").</summary>
    public IReadOnlyList<object> NavItems { get; }

    /// <summary>The guided "Set up sprig" strip (opt-in from Home).</summary>
    public SetupGuideViewModel Guide { get; }

    /// <summary>The About page — pinned to the bottom of the nav, outside the workflow sequence.</summary>
    public AboutViewModel About { get; }

    [ObservableProperty]
    private PageViewModel _currentPage;

    /// <summary>Non-null when a newer version is available; drives the top notification bar.</summary>
    [ObservableProperty]
    private string? _updateNotice;

    public MainWindowViewModel(AppServices services)
    {
        var nav = new Navigator();
        var repos = new ReposViewModel(services);
        var stacks = new StacksViewModel(services, nav);
        var workspaces = new WorkspacesViewModel(services, nav);
        var home = new HomeViewModel(services, nav);
        nav.Configure(Navigate, home, repos, stacks, workspaces);

        Guide = new SetupGuideViewModel(services, nav);
        nav.SetGuideLauncher(Guide.Start);

        About = new AboutViewModel();

        // About is navigable (so it gets active-state highlighting) but lives in the bottom
        // nav slot rather than the workflow list, so it's not in NavItems.
        Pages = [home, repos, stacks, workspaces, About];
        NavItems =
        [
            home,
            new NavHeaderViewModel("Set up"),
            repos,
            stacks,
            new NavHeaderViewModel("Run"),
            workspaces,
        ];

        // Land on Home (the front door), not on the last step of the pipeline.
        _currentPage = home;
        home.IsActive = true;
        _ = CheckForUpdatesAsync();
    }

    [RelayCommand]
    private void Navigate(PageViewModel page) => CurrentPage = page;

    [RelayCommand]
    private void DismissUpdateNotice() => UpdateNotice = null;

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        foreach (var page in Pages)
            page.IsActive = ReferenceEquals(page, value);
    }

    async Task CheckForUpdatesAsync() => UpdateNotice = await UpdateChecker.CheckAsync();
}

/// <summary>A non-interactive section label in the left nav (e.g. "Set up", "Run").</summary>
public sealed class NavHeaderViewModel(string label)
{
    public string Label { get; } = label;
}
