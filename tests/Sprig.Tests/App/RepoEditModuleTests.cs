using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>The repo editor edits modules as tabs: inputs stay shared, each module is a tab with its own
/// env/compose/setup, and modules can be added and removed down to zero.</summary>
public class RepoEditModuleTests
{
    static string Write(TempStore s, string json)
    {
        var dir = Path.Combine(s.Root, "repo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), json);
        return dir;
    }

    [Fact]
    public void Load_builds_one_tab_per_module_selecting_the_first()
    {
        using var s = new TempStore();
        var dir = Write(s, """
            { "schema":1, "name":"mono",              "modules":[
                { "name":"web", "path":"apps/web",
                  "env":[ { "file":".env.local", "set":{ "PORT":"${sprig.workspace}" } } ], "setup":["npm ci"] },
                { "name":"api", "path":"apps/api", "setup":["dotnet restore"] } ] }
            """);

        var e = RepoEditViewModel.Load(dir);

        Assert.Equal(["web", "api"], e.Modules.Select(m => m.Name));
        Assert.Same(e.Modules[0], e.SelectedModule);
        Assert.Equal("apps/web", e.Modules[0].Path);
        Assert.Equal(".env.local", e.Modules[0].Env.First().File);
        Assert.Equal(["npm ci"], e.Modules[0].Setup.Select(x => x.Command));
        Assert.Equal(["dotnet restore"], e.Modules[1].Setup.Select(x => x.Command));
    }

    [Fact]
    public void Loaded_env_template_resolves_existence_under_the_module_path()
    {
        using var s = new TempStore();
        var dir = Write(s, """
            { "schema":1, "name":"mono",              "modules":[
                { "name":"web", "path":"apps/web",
                  "env":[ { "file":".env.local", "templates":[".env.template"],
                            "set":{ "PORT":"${sprig.workspace}" } } ] } ] }
            """);
        // The template lives under the module path — the existence hint must resolve it there,
        // not against the repo root.
        Directory.CreateDirectory(Path.Combine(dir, "apps", "web"));
        File.WriteAllText(Path.Combine(dir, "apps", "web", ".env.template"), "PORT=3000\n");

        var e = RepoEditViewModel.Load(dir);
        var template = e.Modules[0].Env.Single().Templates.Single();

        Assert.Equal(".env.template", template.Path);
        Assert.True(template.ShowFound);      // found under apps/web
        Assert.False(template.ShowMissing);
        Assert.Equal("✓ found", template.StatusText);   // flattened template banner text
    }

    [Fact]
    public void Build_round_trips_modules()
    {
        using var s = new TempStore();
        var dir = Write(s, """
            { "schema":1, "name":"mono",
              "modules":[
                { "name":"web", "path":"apps/web", "setup":["npm ci"] },
                { "name":"api", "path":"apps/api", "setup":["dotnet restore"] } ] }
            """);

        var built = RepoEditViewModel.Load(dir).Build();

        Assert.Equal(1, built.Schema);
        Assert.Equal(["web", "api"], built.Modules.Select(m => m.Name));
        Assert.Equal(["apps/web", "apps/api"], built.Modules.Select(m => m.Path));
        Assert.Equal(["npm ci"], built.Modules[0].Setup);
        Assert.Null(built.Env);   // nothing at the legacy top level
    }

    [Fact]
    public void Add_module_appends_a_selectable_blank_tab_for_the_user_to_name()
    {
        using var s = new TempStore();
        var dir = Write(s, """{ "schema":1, "name":"empty" }""");
        var e = RepoEditViewModel.Load(dir);
        Assert.Empty(e.Modules);
        Assert.False(e.HasModules);

        e.AddModuleCommand.Execute(null);
        e.AddModuleCommand.Execute(null);

        // Names are left blank on purpose — the user types them; the view autofocuses the name box.
        Assert.Equal(["", ""], e.Modules.Select(m => m.Name));
        Assert.True(e.FocusNewModuleRequested);        // view is asked to focus the new module's name
        Assert.Same(e.Modules[1], e.SelectedModule);   // newest is selected
        Assert.True(e.HasModules);
    }

    [Fact]
    public void Remove_module_can_go_down_to_zero()
    {
        using var s = new TempStore();
        var dir = Write(s, """{ "schema":1, "name":"mono", "modules":[ { "name":"only", "setup":["x"] } ] }""");
        var e = RepoEditViewModel.Load(dir);

        e.SelectedModule!.RemoveCommand.Execute(null);

        Assert.Empty(e.Modules);
        Assert.Null(e.SelectedModule);
        Assert.False(e.HasModules);
    }

    [Fact]
    public void Module_path_flags_a_missing_directory_but_does_not_block_save()
    {
        using var s = new TempStore();
        var dir = Write(s, """{ "schema":1, "name":"mono", "modules":[ { "name":"web" } ] }""");
        Directory.CreateDirectory(Path.Combine(dir, "apps", "web"));   // exists; apps/api does not
        var e = RepoEditViewModel.Load(dir);
        var tab = e.Modules[0];

        Assert.False(tab.ShowPathFound);    // empty path = repo root → no hint either way
        Assert.False(tab.ShowPathMissing);

        tab.Path = "apps/web";
        Assert.True(tab.ShowPathFound);
        Assert.False(tab.ShowPathMissing);

        tab.Path = "apps/api";              // no such directory
        Assert.False(tab.ShowPathFound);
        Assert.True(tab.ShowPathMissing);

        Assert.True(e.Save());              // informational only — a missing directory still saves
    }

    [Fact]
    public void File_suggestions_start_from_the_module_path_not_the_repo_root()
    {
        using var s = new TempStore();
        var dir = Write(s, """{ "schema":1, "name":"mono", "modules":[ { "name":"api", "path":"apps/api" } ] }""");
        Directory.CreateDirectory(Path.Combine(dir, "apps", "api"));
        File.WriteAllText(Path.Combine(dir, "apps", "api", "docker-compose.yml"), "services: {}\n");
        File.WriteAllText(Path.Combine(dir, "root-only.yml"), "x\n");   // at the repo root

        var e = RepoEditViewModel.Load(dir);

        // A module's file field suggests from within the module's path, returning module-relative paths.
        var inModule = e.SuggestRepoPaths("docker", basePath: "apps/api");
        Assert.Contains("docker-compose.yml", inModule);       // not "apps/api/docker-compose.yml"
        Assert.DoesNotContain("root-only.yml", inModule);      // the repo-root file is not in scope

        // The module-path picker itself (no base path) still enumerates from the repo root.
        Assert.Contains("root-only.yml", e.SuggestRepoPaths("root"));
        Assert.Contains("apps/", e.SuggestRepoPaths("apps"));  // a directory, drillable
    }

    [Fact]
    public void Deleting_all_modules_then_saving_writes_an_empty_module_list()
    {
        using var s = new TempStore();
        var dir = Write(s, """{ "schema":1, "name":"mono", "modules":[ { "name":"only", "setup":["x"] } ] }""");
        var e = RepoEditViewModel.Load(dir);

        e.SelectedModule!.RemoveCommand.Execute(null);
        Assert.True(e.Save());

        var reloaded = RepoEditViewModel.Load(dir);
        Assert.Empty(reloaded.Modules);
    }
}
