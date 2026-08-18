using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Maps;
using Sprig.Core.Store;

namespace Sprig.Tests.App;

/// <summary>
/// The create-workspace form's repo checklist — the UI half of partial workspaces on the map model.
/// Nothing here creates a workspace (that's covered in Core); these pin what the form tells the user
/// before they commit — which repos are dropped and any needs the remaining slice can no longer meet.
/// </summary>
public class PartialWorkspaceViewModelTests
{
    /// <summary>web NEEDS 'api'; api PROVIDES it. A map of both. No git — the form only resolves configs.</summary>
    static void SeedWebApiMap(TempStore s, AppServices services)
    {
        services.Repos.Add(WriteRepo(s.Root, "api", """
            { "schema":1, "name":"api", "modules":[
              { "name":"main", "provides":[ { "capability":"api", "outputs":{ "port":{"port":true} } } ] } ] }
            """));
        services.Repos.Add(WriteRepo(s.Root, "web", """
            { "schema":1, "name":"web", "modules":[
              { "name":"main", "needs":[ { "capability":"api" } ] } ] }
            """));
        services.Maps.Save(new MapDefinition
        {
            Name = "web+api",
            Repos = [MapRepo.Local("api"), MapRepo.Local("web")],
        });
    }

    static string WriteRepo(string root, string name, string json)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), json);
        return dir;
    }

    [Fact]
    public async Task The_create_form_lists_the_maps_repos_all_selected()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApiMap(s, services);

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
    public async Task Dropping_the_provider_leaves_the_consumer_with_an_unmet_need()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApiMap(s, services);

        var vm = new WorkspacesViewModel(services, new Navigator());
        await vm.NewWorkspaceCommand.ExecuteAsync(null);
        vm.NewRepos.First(r => r.Name == "api").Included = false;   // web still needs 'api'

        Assert.True(vm.IsPartialSelection);
        Assert.Contains("api", vm.PartialHint);
        Assert.Contains("unmet", vm.PartialHint);   // the need 'api' has no provider left in the slice
    }

    [Fact]
    public async Task Dropping_the_consumer_leaves_no_unmet_need()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApiMap(s, services);

        var vm = new WorkspacesViewModel(services, new Navigator());
        await vm.NewWorkspaceCommand.ExecuteAsync(null);
        vm.NewRepos.First(r => r.Name == "web").Included = false;   // api is a lone provider, no needs

        Assert.True(vm.IsPartialSelection);
        Assert.Contains("web", vm.PartialHint);
        Assert.DoesNotContain("unmet", vm.PartialHint);
    }

    [Fact]
    public async Task Unticking_everything_asks_for_at_least_one_repo()
    {
        using var s = new TempStore();
        var services = new AppServices(s.Root);
        SeedWebApiMap(s, services);

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
            Map = "web+api",
            Repos = [new InstanceRepo { Name = "api", SourcePath = "C:/api", WorktreePath = "C:/api--backend-only" }],
            SelectedRepos = ["api"],
            ExcludedRepos = ["web"],
        };

        var item = new WorkspaceItemViewModel(record);

        Assert.True(item.IsPartial);
        Assert.Equal("without web", item.PartialSummary);
        Assert.Equal("", new WorkspaceItemViewModel(record with { ExcludedRepos = [] }).PartialSummary);
    }
}
