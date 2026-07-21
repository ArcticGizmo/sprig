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

    [ObservableProperty]
    private PageViewModel _currentPage;

    /// <summary>Non-null when a newer version is available; drives the top notification bar.</summary>
    [ObservableProperty]
    private string? _updateNotice;

    public MainWindowViewModel(AppServices services)
    {
        var repos = new ReposViewModel(services);
        var stacks = new StacksViewModel(services);
        var workspaces = new WorkspacesViewModel(services);
        var home = new HomeViewModel(services, Navigate, repos, stacks, workspaces);

        Pages = [home, repos, stacks, workspaces];
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
