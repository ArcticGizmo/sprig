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

        vm.NavigateCommand.Execute(vm.Orchestrations);

        Assert.Same(vm.Orchestrations, vm.CurrentPage);
        Assert.True(vm.Orchestrations.IsActive);
        Assert.False(vm.Home.IsActive);
        Assert.Single(vm.Pages, p => p.IsActive);
    }

    [Fact]
    public void The_nav_lists_the_pages_in_workflow_order_with_settings_and_about_pinned_below()
    {
        using var store = new TempStore();
        var vm = new MainWindowViewModel(new AppServices(store.Root), checkForUpdates: false);

        Assert.Equal(["Home", "Repos", "Workspaces", "Orchestrations"], vm.NavItems.Select(p => p.Title));
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
    public void Exactly_one_milestone_is_marked_next()
    {
        var home = new HomeViewModel();

        Assert.Single(home.Milestones, m => m.IsNext);
    }
}
