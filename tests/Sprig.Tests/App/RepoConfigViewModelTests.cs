using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>The read-only preview projects a repo's modules into tabs, inputs shared above them.</summary>
public class RepoConfigViewModelTests
{
    static string WriteConfig(TempStore s, string json)
    {
        var dir = Path.Combine(s.Root, "repo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), json);
        return dir;
    }

    [Fact]
    public void Projects_one_tab_per_module_with_first_selected()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """
            { "schema": 3, "name": "mono",
              "modules": [
                { "name": "web", "path": "apps/web",
                  "provides": [ { "capability": "port", "outputs": { "port": { "port": true } } } ],
                  "env": [ { "file": ".env.local", "set": { "PORT": "${sprig.port.port}" } } ],
                  "setup": [ "npm ci" ] },
                { "name": "api", "path": "apps/api",
                  "compose": [ { "file": "docker-compose.yml", "overrides": [
                      { "path": ["services","db","container_name"], "template": "db--${sprig.workspace}" } ] } ] } ] }
            """);

        var vm = RepoConfigViewModel.Load(dir);

        Assert.True(vm.Ok);
        Assert.Equal(["web", "api"], vm.Modules.Select(m => m.Name));
        Assert.Same(vm.Modules[0], vm.SelectedModule);         // first tab selected by default

        var web = vm.Modules[0];
        Assert.Equal("apps/web", web.Path);
        Assert.True(web.HasPath);
        Assert.True(web.HasEnv);
        Assert.False(web.HasCompose);
        Assert.Equal(["npm ci"], web.Setup);

        var api = vm.Modules[1];
        Assert.True(api.HasCompose);
        Assert.False(api.HasEnv);
    }

    [Fact]
    public void A_migrated_schema2_repo_shows_a_single_app_tab()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """
            { "schema": 2, "name": "solo",
              "env": [ { "file": ".env", "set": { "NAME": "x" } } ] }
            """);

        var vm = RepoConfigViewModel.Load(dir);

        var module = Assert.Single(vm.Modules);
        Assert.Equal("app", module.Name);
        Assert.Equal("", module.Path);
        Assert.False(module.HasPath);
        Assert.True(module.HasEnv);
    }

    [Fact]
    public void A_repo_with_no_modules_has_none()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """{ "schema": 3, "name": "empty" }""");

        var vm = RepoConfigViewModel.Load(dir);

        Assert.False(vm.HasModules);
        Assert.Null(vm.SelectedModule);
    }
}
