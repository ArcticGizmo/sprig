using System.Collections.Generic;
using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

namespace Sprig.Tests.App;

/// <summary>
/// The create-workspace form's repo checklist — the UI half of partial workspaces. Nothing here
/// creates a workspace (that's covered in Core); these pin what the form tells the user before they
/// commit, and the badge the workspace list shows afterwards.
/// </summary>
public class PartialWorkspaceViewModelTests
{
    /// <summary>api owns api_port; web consumes it and owns web_port. Same shape as the Core tests.</summary>
    static void SeedWebApiStack(TempStore s, AppServices services)
    {
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "api", ("port", "5000")));
        services.Repos.Add(ManagementViewModelTests.MakeRepoWithInputs(s.Root, "web",
            ("apiUrl", "http://localhost:5000"), ("devPort", "5173")));
        services.Stacks.Save(new StackDefinition
        {
            Name = "web+api",
            Repos = ["api", "web"],
            Ports = ["api_port", "web_port"],
            Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["api"] = new Dictionary<string, string> { ["port"] = "${sprig.ports.api_port}" },
                ["web"] = new Dictionary<string, string>
                {
                    ["apiUrl"] = "http://localhost:${sprig.ports.api_port}",
                    ["devPort"] = "${sprig.ports.web_port}",
                },
            },
        });
    }

    [Fact]
    public async Task The_create_form_lists_the_stacks_repos_all_selected()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApiStack(s, services);

        var vm = new WorkspacesViewModel(services, new Navigator());
        await vm.NewWorkspaceCommand.ExecuteAsync(null);

        Assert.Equal(["api", "web"], vm.NewRepos.Select(r => r.Name));
        Assert.All(vm.NewRepos, r => Assert.True(r.Included));
        Assert.True(vm.CanChooseRepos);
        // Nothing deselected: no partial state, no hint to explain.
        Assert.False(vm.IsPartialSelection);
        Assert.Null(vm.PartialHint);
    }

    [Fact]
    public async Task Unticking_a_repo_says_what_is_dropped_and_which_ports_go_unprovisioned()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApiStack(s, services);

        var vm = new WorkspacesViewModel(services, new Navigator());
        await vm.NewWorkspaceCommand.ExecuteAsync(null);
        vm.NewRepos.First(r => r.Name == "web").Included = false;

        Assert.True(vm.IsPartialSelection);
        Assert.Contains("web", vm.PartialHint);
        Assert.Contains("web_port", vm.PartialHint);      // orphaned by dropping web
        Assert.DoesNotContain("api_port", vm.PartialHint); // api still consumes it
    }

    [Fact]
    public async Task Dropping_the_api_keeps_the_port_the_web_still_points_at()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApiStack(s, services);

        var vm = new WorkspacesViewModel(services, new Navigator());
        await vm.NewWorkspaceCommand.ExecuteAsync(null);
        vm.NewRepos.First(r => r.Name == "api").Included = false;

        Assert.True(vm.IsPartialSelection);
        Assert.Contains("api", vm.PartialHint);
        Assert.DoesNotContain("won't be provisioned", vm.PartialHint); // nothing orphaned
    }

    [Fact]
    public async Task Unticking_everything_asks_for_at_least_one_repo()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApiStack(s, services);

        var vm = new WorkspacesViewModel(services, new Navigator());
        await vm.NewWorkspaceCommand.ExecuteAsync(null);
        foreach (var r in vm.NewRepos) r.Included = false;
        vm.NewName = "nothing";

        Assert.Equal("Pick at least one repo.", vm.PartialHint);
        await vm.CreateCommand.ExecuteAsync(null);
        Assert.Equal("pick at least one repo", vm.CreateError);
        Assert.True(vm.IsCreating);   // form stays open on a validation failure
    }

    [Fact]
    public void The_workspace_list_badges_a_partial_workspace_with_what_it_is_missing()
    {
        var record = new InstanceRecord
        {
            Workspace = "backend-only",
            Stack = "web+api",
            Repos = [new InstanceRepo { Name = "api", SourcePath = "C:/api", WorktreePath = "C:/api--backend-only" }],
            Ports = new Dictionary<string, int> { ["api_port"] = 5000 },
            ExcludedRepos = ["web"],
            SkippedPorts = ["web_port"],
        };

        var item = new WorkspaceItemViewModel(record);

        Assert.True(item.IsPartial);
        Assert.Equal("without web · ports not provisioned: web_port", item.PartialSummary);
        Assert.Equal("", new WorkspaceItemViewModel(record with { ExcludedRepos = [], SkippedPorts = [] }).PartialSummary);
    }
}
