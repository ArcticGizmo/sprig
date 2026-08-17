using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Maps;

namespace Sprig.Tests.App;

/// <summary>M8 — the Maps page lists maps and grows a workspace from a selected slice, driven entirely by
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
            { "schema": 3, "name": "solo",
              "provides": [ { "capability": "api", "outputs": { "port": { "port": true } } } ],
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
            { "schema": 3, "name": "solo",
              "needs": [ { "capability": "ghost" } ],
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
    public async Task Deselecting_a_repo_leaves_it_out_of_the_checkout()
    {
        using var store = new TempStore();
        using var web = CommitConfig("web", """{ "schema": 3, "name": "web" }""");
        using var api = CommitConfig("api", """
            { "schema": 3, "name": "api",
              "provides": [ { "capability": "api", "outputs": { "port": { "port": true } } } ] }
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
