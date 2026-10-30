using Sprig.App;
using Sprig.App.ViewModels;
using Sprig.Core.Config;

namespace Sprig.Tests.App;

/// <summary>Editing a virtual repo's config as raw JSON — pasted or loaded from a file — for quick testing.</summary>
public class VirtualJsonEditTests
{
    const string Config = """
        { "schema":1, "name":"client", "modules":[ { "name":"spa", "path":"",
          "provides":[ { "capability":"client-spa", "ports":{ "port":true },
                         "shapes":{ "url":"http://localhost:${sprig.client-spa.port}" }, "open":"url" } ] } ] }
        """;

    static (ReposViewModel vm, AppServices services) Virtual(TempStore s, TempGitRepo repo)
    {
        var services = new AppServices(s.Root);
        services.Repos.AddVirtual(repo.Path, "client-try", SprigConfigLoader.Parse(Config));
        var vm = new ReposViewModel(services);
        vm.Selected = vm.Repos.Single(r => r.Name == "client-try");
        return (vm, services);
    }

    [Fact]
    public void Edit_as_JSON_is_offered_only_for_a_virtual_repo()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("client");
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), Config);
        var (vm, services) = Virtual(s, repo);
        Assert.True(vm.CanEditJson);

        services.Repos.Add(repo.Path);
        vm = new ReposViewModel(services);
        vm.Selected = vm.Repos.Single(r => r.Name == "client");
        Assert.False(vm.CanEditJson);
    }

    [Fact]
    public void Pasted_JSON_replaces_the_config_under_the_registry_name()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("client");
        var (vm, _) = Virtual(s, repo);

        vm.BeginEditJsonCommand.Execute(null);
        Assert.Contains("client-spa", vm.JsonText);

        vm.JsonText = """{ "schema":1, "name":"someone-else", "modules":[ { "name":"api", "path":"" } ] }""";
        vm.ApplyJsonCommand.Execute(null);

        Assert.Null(vm.JsonError);
        Assert.False(vm.IsEditingJson);
        var saved = SprigConfigLoader.LoadFromFile(vm.Selected!.ConfigPath);
        Assert.Equal("client-try", saved.Name);
        Assert.Equal("api", Assert.Single(saved.Modules).Name);
        Assert.Equal("api", Assert.Single(vm.SelectedConfig!.Modules).Name);
    }

    [Fact]
    public void Invalid_JSON_is_reported_and_nothing_is_saved()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("client");
        var (vm, _) = Virtual(s, repo);
        var before = File.ReadAllText(vm.Selected!.ConfigPath);

        vm.BeginEditJsonCommand.Execute(null);
        vm.JsonText = "{ not json";
        vm.ApplyJsonCommand.Execute(null);

        Assert.NotNull(vm.JsonError);
        Assert.True(vm.IsEditingJson);
        Assert.Equal(before, File.ReadAllText(vm.Selected.ConfigPath));
    }

    [Fact]
    public void Loading_a_file_fills_the_editor_without_saving()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo("client");
        var (vm, _) = Virtual(s, repo);
        var before = File.ReadAllText(vm.Selected!.ConfigPath);
        var file = Path.Combine(s.Root, "alt.json");
        File.WriteAllText(file, """{ "schema":1, "name":"alt" }""");

        vm.BeginEditJsonCommand.Execute(null);
        vm.LoadJsonFromFile(file);

        Assert.Contains("\"alt\"", vm.JsonText);
        Assert.Equal(before, File.ReadAllText(vm.Selected.ConfigPath));
    }
}
