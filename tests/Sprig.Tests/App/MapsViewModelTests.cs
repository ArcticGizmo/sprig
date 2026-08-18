using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Maps;

namespace Sprig.Tests.App;

/// <summary>M8 â€” the Maps page lists maps and grows a workspace from a selected slice, driven entirely by
/// the repos' own provides/needs (no binding editor).</summary>
public class MapsViewModelTests
{
    static TempGitRepo CommitConfig(string name, string json)
    {
        var repo = new TempGitRepo(name);
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), json);
        repo.Git("add", "-A");
        repo.Git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "cfg");
        return repo;
    }

    [Fact]
    public async Task Lists_maps_and_creates_a_workspace_from_the_selection()
    {
        using var store = new TempStore();
        using var repo = CommitConfig("solo", """
            { "schema": 1, "name": "solo",
              "provides": [ { "capability": "api", "ports": { "port": true } } ],
              "env": [ { "file": ".env", "set": { "PORT": "${sprig.api.port}" } } ] }
            """);
        var services = new AppServices(store.Root);
        services.Repos.Add(repo.Path);
        services.Maps.Save(new MapDefinition { Name = "dev", Repos = [MapRepo.Local("solo")] });

        var vm = new MapsViewModel(services);
        Assert.True(vm.HasMaps);
        Assert.Equal("dev", vm.Selected!.Name);
        Assert.Equal("solo", Assert.Single(vm.RepoChoices).Name);

        vm.NewWorkspaceName = "feat";
        await vm.CreateWorkspaceCommand.ExecuteAsync(null);

        Assert.False(vm.StatusIsError);
        Assert.Equal("dev", services.Workspaces.Get("feat")!.Map);
    }

    [Fact]
    public async Task An_unmet_need_surfaces_as_an_error_status_and_creates_nothing()
    {
        using var store = new TempStore();
        using var repo = CommitConfig("solo", """
            { "schema": 1, "name": "solo",
              "needs": [ { "value": "ghost" } ],
              "env": [ { "file": ".env", "set": { "X": "${sprig.ghost.url}" } } ] }
            """);
        var services = new AppServices(store.Root);
        services.Repos.Add(repo.Path);
        services.Maps.Save(new MapDefinition { Name = "dev", Repos = [MapRepo.Local("solo")] });

        var vm = new MapsViewModel(services) { NewWorkspaceName = "feat" };
        await vm.CreateWorkspaceCommand.ExecuteAsync(null);

        Assert.True(vm.StatusIsError);
        Assert.Contains("ghost", vm.Status!);
        Assert.Null(services.Workspaces.Get("feat"));
    }

    [Fact]
    public void New_map_composes_and_saves_from_selected_registered_repos()
    {
        using var store = new TempStore();
        using var a = CommitConfig("alpha", """{ "schema": 1, "name": "alpha" }""");
        using var b = CommitConfig("beta", """{ "schema": 1, "name": "beta" }""");
        var services = new AppServices(store.Root);
        services.Repos.Add(a.Path);
        services.Repos.Add(b.Path);

        var vm = new MapsViewModel(services);
        vm.NewMapCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.Equal(["alpha", "beta"], vm.EditRepos.Select(r => r.Name));   // every registered repo, unchecked

        vm.EditName = "world";
        vm.EditRepos.Single(r => r.Name == "alpha").IsSelected = true;
        vm.SaveMapCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Null(vm.EditError);
        var saved = services.Maps.Get("world")!;
        Assert.Equal(["alpha"], saved.Repos.Select(r => r.Name));
        Assert.Equal("world", vm.Selected!.Name);
    }

    [Fact]
    public void Editing_a_map_updates_its_repos_and_a_rename_removes_the_old()
    {
        using var store = new TempStore();
        using var a = CommitConfig("alpha", """{ "schema": 1, "name": "alpha" }""");
        var services = new AppServices(store.Root);
        services.Repos.Add(a.Path);
        services.Maps.Save(new MapDefinition { Name = "old", Repos = [MapRepo.Local("alpha")] });

        var vm = new MapsViewModel(services);
        vm.EditSelectedCommand.Execute(null);
        Assert.Equal("old", vm.EditName);
        Assert.True(vm.EditRepos.Single(r => r.Name == "alpha").IsSelected);   // membership reflected

        vm.EditName = "renamed";
        vm.SaveMapCommand.Execute(null);

        Assert.Null(services.Maps.Get("old"));            // old file removed on rename
        Assert.NotNull(services.Maps.Get("renamed"));
    }

    [Fact]
    public void Saving_an_invalid_map_surfaces_the_error_inline()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);

        var vm = new MapsViewModel(services);
        vm.NewMapCommand.Execute(null);
        vm.EditName = "empty";                            // no repos selected
        vm.SaveMapCommand.Execute(null);

        Assert.True(vm.IsEditing);                        // stays open
        Assert.NotNull(vm.EditError);
        Assert.Empty(services.Maps.List());
    }

    [Fact]
    public async Task Deselecting_a_repo_leaves_it_out_of_the_checkout()
    {
        using var store = new TempStore();
        using var web = CommitConfig("web", """{ "schema": 1, "name": "web" }""");
        using var api = CommitConfig("api", """
            { "schema": 1, "name": "api",
              "provides": [ { "capability": "api", "ports": { "port": true } } ] }
            """);
        var services = new AppServices(store.Root);
        services.Repos.Add(web.Path);
        services.Repos.Add(api.Path);
        services.Maps.Save(new MapDefinition { Name = "dev", Repos = [MapRepo.Local("web"), MapRepo.Local("api")] });

        var vm = new MapsViewModel(services) { NewWorkspaceName = "slice" };
        vm.RepoChoices.Single(c => c.Name == "api").IsSelected = false;   // web only
        await vm.CreateWorkspaceCommand.ExecuteAsync(null);

        Assert.False(vm.StatusIsError);
        Assert.Equal(["web"], services.Workspaces.Get("slice")!.SelectedRepos);
    }
}
