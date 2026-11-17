using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Engine.Hosting;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.App;

public class MultiWindowTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.FromHours(11));

    static AppServices WithWorkspace(TempStore temp)
    {
        var services = new AppServices(temp.Root);
        new WorkspaceStore(services.Paths).Save(new WorkspaceRecord("storefront", @"C:\tb\storefront.orchestration.yaml", "feat-a", "feat-a", "stable",
            [], [], [], WorkspaceState.Up, Now, Now));
        return services;
    }

    [Theory]
    [InlineData(null, "show")]
    [InlineData("", "show")]
    [InlineData("storefront/feat-a", "open storefront/feat-a")]
    public void A_launch_asks_the_running_sprig_for_any_window_or_one_workspace(string? workspace, string message)
    {
        Assert.Equal(message, SingleInstance.Message(workspace));
        Assert.True(SingleInstance.TryRead(message, out var read));
        Assert.Equal(string.IsNullOrEmpty(workspace) ? null : workspace, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("opened storefront")]
    public void Anything_else_on_the_pipe_is_ignored(string? line) => Assert.False(SingleInstance.TryRead(line, out _));

    [Fact]
    public void The_workspace_to_open_is_the_word_after_workspace()
    {
        Assert.Equal("storefront/feat-a", global::Sprig.App.Program.WorkspaceArg(["--workspace", "storefront/feat-a"]));
        Assert.Null(global::Sprig.App.Program.WorkspaceArg([]));
        Assert.Null(global::Sprig.App.Program.WorkspaceArg(["--workspace"]));
        Assert.Null(global::Sprig.App.Program.WorkspaceArg(["--workspace", " "]));
    }

    [Fact]
    public void A_window_on_a_workspace_names_it_in_its_title_and_says_which_it_shows()
    {
        using var temp = new TempStore();
        using var vm = new MainWindowViewModel(WithWorkspace(temp), checkForUpdates: false);
        Assert.Equal(MainWindowViewModel.WindowTitle, vm.Title);
        Assert.Null(vm.ShownWorkspace);

        vm.ShowWorkspace("storefront/feat-a");

        Assert.Equal("storefront/feat-a", vm.ShownWorkspace);
        Assert.Contains("feat-a", vm.Title);
        Assert.EndsWith(MainWindowViewModel.WindowTitle, vm.Title);

        vm.NavigateCommand.Execute(vm.Home);
        Assert.Null(vm.ShownWorkspace);
        Assert.Equal(MainWindowViewModel.WindowTitle, vm.Title);
    }

    [Fact]
    public void Open_in_a_new_window_hands_the_selected_workspace_to_whoever_keeps_the_windows()
    {
        using var temp = new TempStore();
        using var vm = new MainWindowViewModel(WithWorkspace(temp), checkForUpdates: false);
        var asked = new List<string?>();
        vm.NewWindow = asked.Add;

        vm.ShowWorkspace("storefront/feat-a");
        vm.Workspaces.OpenSelectedInNewWindowCommand.Execute(null);
        vm.OpenNewWindowCommand.Execute(null);

        Assert.Equal(new string?[] { "storefront/feat-a", null }, asked);
    }

    [Fact]
    public void A_closed_window_stops_hearing_the_polls()
    {
        using var temp = new TempStore();
        var services = WithWorkspace(temp);
        var vm = new MainWindowViewModel(services, checkForUpdates: false);
        var runners = new[] { new RunnerStatus("storefront/feat-a", "shop/api", RunnerState.Ready, 1, 8000, null, "a.log", Now) };
        services.HostStatus.Apply(runners);
        Assert.Single(vm.Home.Running);

        vm.Dispose();
        vm.Home.Running = [];
        services.HostStatus.Apply(runners);

        Assert.Empty(vm.Home.Running);
    }

    [Fact]
    public void A_later_window_shows_the_update_the_first_one_found()
    {
        using var temp = new TempStore();
        var services = new AppServices(temp.Root);
        using var first = new MainWindowViewModel(services, checkForUpdates: false);
        using var later = new MainWindowViewModel(services, checkForUpdates: false);
        var found = new Sprig.App.Updates.UpdateCheckResult
        {
            Availability = Sprig.App.Updates.UpdateAvailability.Available,
            CurrentVersion = "0.9.4",
            AvailableVersion = "0.9.5",
        };

        later.ApplyUpdate(found);

        Assert.True(later.ShowUpdateNotice);
        Assert.Contains("0.9.5", later.UpdateNotice);
        Assert.False(first.ShowUpdateNotice);
    }
}
