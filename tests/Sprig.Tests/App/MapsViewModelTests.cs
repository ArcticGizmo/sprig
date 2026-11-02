using Sprig.App;
using Sprig.App.Maps;
using Sprig.App.ViewModels;
using Sprig.Core.Maps;

namespace Sprig.Tests.App;

/// <summary>The Maps page: maps are named here and composed on their wiring graph.</summary>
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
    public void Lists_maps_and_shows_the_first_ones_wiring()
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
        Assert.Equal("dev", vm.Wiring.MapName);
        Assert.True(vm.Wiring.IsReady);
    }

    [Fact]
    public void A_new_map_is_named_then_opens_empty_with_every_registered_repo_ready_to_add()
    {
        using var store = new TempStore();
        using var a = CommitConfig("alpha", """{ "schema": 1, "name": "alpha" }""");
        using var b = CommitConfig("beta", """{ "schema": 1, "name": "beta" }""");
        var services = new AppServices(store.Root);
        services.Repos.Add(a.Path);
        services.Repos.Add(b.Path);

        var vm = new MapsViewModel(services);
        vm.NewMapCommand.Execute(null);
        Assert.True(vm.IsNaming);
        vm.NewMapName = "world";
        vm.CreateMapCommand.Execute(null);

        Assert.False(vm.IsNaming);
        Assert.Equal("world", vm.Selected!.Name);
        Assert.True(vm.Wiring.IsEmpty);
        Assert.Equal(["alpha", "beta"], vm.Wiring.AvailableRepos);
    }

    [Fact]
    public void Adding_a_repo_from_the_graph_saves_it_and_updates_the_list()
    {
        using var store = new TempStore();
        using var a = CommitConfig("alpha", """
            { "schema": 1, "name": "alpha", "provides": [ { "capability": "api", "ports": { "port": true } } ] }
            """);
        var services = new AppServices(store.Root);
        services.Repos.Add(a.Path);
        services.Maps.Save(new MapDefinition { Name = "world" });
        var vm = new MapsViewModel(services);

        vm.Wiring.AddRepoCommand.Execute("alpha");

        Assert.Equal(["alpha"], services.Maps.Get("world")!.Repos.Select(r => r.Name));
        Assert.Equal(["alpha"], vm.Selected!.Repos.Select(r => r.Name));
        Assert.Empty(vm.Wiring.AvailableRepos);
        Assert.Contains(vm.Wiring.Layout!.Nodes, n => n.Id == MapGraphLayout.ModuleId("alpha", "app"));

        vm.Wiring.SelectedNodeId = MapGraphLayout.ModuleId("alpha", "app");
        Assert.Equal("alpha", vm.Wiring.SelectedRepo);
        vm.Wiring.RemoveRepoCommand.Execute("alpha");
        Assert.Empty(services.Maps.Get("world")!.Repos);
        Assert.True(vm.Wiring.IsEmpty);
    }

    [Fact]
    public void Renaming_keeps_everything_about_the_map_and_removes_the_old_name()
    {
        using var store = new TempStore();
        using var a = CommitConfig("alpha", """{ "schema": 1, "name": "alpha" }""");
        var services = new AppServices(store.Root);
        services.Repos.Add(a.Path);
        services.Maps.Save(new MapDefinition
        {
            Name = "old",
            Repos = [MapRepo.Local("alpha")],
            MaxSlots = 3,
            Constants = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
            {
                ["alpha"] = new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["payments"] = new Dictionary<string, string> { ["url"] = "https://pay.sandbox.test" },
                },
            },
        });

        var vm = new MapsViewModel(services);
        vm.BeginRenameCommand.Execute(null);
        vm.RenameText = "renamed";
        vm.SaveRenameCommand.Execute(null);

        Assert.Null(services.Maps.Get("old"));
        var renamed = services.Maps.Get("renamed")!;
        Assert.Equal(3, renamed.MaxSlots);
        Assert.Equal("https://pay.sandbox.test", renamed.Constants["alpha"]["payments"]["url"]);
        Assert.Equal("renamed", vm.Selected!.Name);
    }

    [Fact]
    public void A_bad_or_taken_name_is_shown_and_nothing_is_saved()
    {
        using var store = new TempStore();
        var services = new AppServices(store.Root);
        services.Maps.Save(new MapDefinition { Name = "taken" });
        var vm = new MapsViewModel(services);

        vm.NewMapCommand.Execute(null);
        vm.NewMapName = "bad name!";
        vm.CreateMapCommand.Execute(null);
        Assert.True(vm.IsNaming);
        Assert.NotNull(vm.NameError);

        vm.NewMapName = "taken";
        vm.CreateMapCommand.Execute(null);
        Assert.Contains("already exists", vm.NameError);
        Assert.Single(services.Maps.List());
    }
}
