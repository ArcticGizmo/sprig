using Sprig.App;
using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

public class MainWindowViewModelTests
{
    [Fact]
    public void Opens_on_Home_and_navigating_moves_the_active_highlight()
    {
        using var store = new TempStore();
        var vm = new MainWindowViewModel(new AppServices(store.Root), checkForUpdates: false);

        Assert.Same(vm.Home, vm.CurrentPage);
        Assert.True(vm.Home.IsActive);

        vm.NavigateCommand.Execute(vm.Repos);

        Assert.Same(vm.Repos, vm.CurrentPage);
        Assert.True(vm.Repos.IsActive);
        Assert.False(vm.Home.IsActive);
        Assert.Single(vm.Pages, p => p.IsActive);
    }

    [Fact]
    public void Orchestrations_is_listed_but_not_ready_so_the_nav_leaves_it_shut()
    {
        using var store = new TempStore();
        var vm = new MainWindowViewModel(new AppServices(store.Root), checkForUpdates: false);

        vm.NavigateCommand.Execute(vm.Orchestrations);

        Assert.Contains(vm.Orchestrations, vm.NavItems);
        Assert.False(vm.Orchestrations.IsReady);
        Assert.Same(vm.Home, vm.CurrentPage);
    }

    [Fact]
    public void The_nav_lists_the_pages_in_workflow_order_with_settings_and_about_pinned_below()
    {
        using var store = new TempStore();
        var vm = new MainWindowViewModel(new AppServices(store.Root), checkForUpdates: false);

        Assert.Equal(["Home", "Repos", "Workspaces", "Usage", "Orchestrations"], vm.NavItems.Select(p => p.Title));
        Assert.Contains(vm.Workspaces, vm.NavItems);
        Assert.DoesNotContain(vm.Settings, vm.NavItems);
        Assert.DoesNotContain(vm.About, vm.NavItems);
    }

    [Fact]
    public void Workspaces_in_the_nav_opens_the_list_of_them()
    {
        using var store = new TempStore();
        var vm = new MainWindowViewModel(new AppServices(store.Root), checkForUpdates: false);

        vm.NavigateCommand.Execute(vm.Workspaces);

        Assert.Same(vm.Workspaces, vm.CurrentPage);
        Assert.Null(vm.Workspaces.Selected);
        Assert.Equal("running copies", vm.Workspaces.NavSubtitle);
    }

    [Fact]
    public void Home_shows_the_first_steps_until_there_is_a_repo_and_nothing_running_on_an_empty_store()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);
        var vm = new MainWindowViewModel(services, checkForUpdates: false);

        vm.Home.Refresh();
        Assert.True(vm.Home.ShowSteps);
        Assert.True(vm.Home.NothingRunning);

        services.Stored.AddRepo("shop-web", Path.Combine(store.Root, "shop-web"), inRepo: false, "// web");
        vm.Home.Refresh();
        Assert.False(vm.Home.ShowSteps);

        vm.Home.StartAddRepoCommand.Execute(null);
        Assert.Same(vm.Repos, vm.CurrentPage);
    }
}
