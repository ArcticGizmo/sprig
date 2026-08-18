using Sprig.App.ViewModels;
using Sprig.Core.Config;

namespace Sprig.Tests.App;

/// <summary>Authoring a repo's map-model surface (provides/needs) in the repo editor: load, edit, and save a
/// valid v1 .sprig.json. Provides use the "one name, many shapes" editor — a port anchor + derived shapes.</summary>
public class RepoEditMapTests
{
    static string WriteConfig(TempStore s, string json)
    {
        var dir = Path.Combine(s.Root, "repo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), json);
        return dir;
    }

    [Fact]
    public void Loads_provides_and_needs_into_editable_rows_and_rebuilds_them()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """
            { "schema": 1, "name": "acme",
              "provides": [ { "capability": "acme-api",
                "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.acme-api.port}" } } ],
              "needs": [ { "capability": "acme-db", "as": "db" } ] }
            """);

        var vm = RepoEditViewModel.Load(dir);
        var tab = Assert.Single(vm.Modules);

        var provide = Assert.Single(tab.Provides);
        Assert.Equal("acme-api", provide.Capability);
        Assert.Equal("port", provide.Port.Name);            // the single, permanent anchor
        Assert.Equal("${sprig.acme-api.port}", provide.Port.Ref);   // the name lives inside the token it heads
        var shape = Assert.Single(provide.Shapes);
        Assert.Equal("url", shape.Name);
        Assert.Contains("localhost", shape.Template);
        Assert.Equal("${sprig.acme-api.url}", shape.Ref);

        var need = Assert.Single(tab.Needs);
        Assert.Equal("acme-db", need.Capability);
        Assert.Equal("db", need.As);

        var config = vm.Build();
        var module = Assert.Single(config.EffectiveModules);
        Assert.True(module.Provides[0].Ports.ContainsKey("port"));
        Assert.Equal("http://localhost:${sprig.acme-api.port}", module.Provides[0].Shapes["url"]);
        Assert.Equal("acme-db", Assert.Single(module.Needs).Capability);
        Assert.True(SprigConfigValidator.Validate(config).IsValid);
    }

    [Fact]
    public void Authors_a_provide_and_need_from_scratch_and_saves_valid_json()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """{ "schema": 1, "name": "fresh" }""");
        var vm = RepoEditViewModel.Load(dir);

        vm.AddModuleCommand.Execute(null);
        var tab = vm.SelectedModule!;
        tab.Name = "app";

        tab.AddProvideCommand.Execute(null);
        var provide = tab.Provides[0];
        provide.Capability = "fresh-api";
        Assert.Equal("port", provide.Port.Name);            // the anchor is always present
        provide.AddShapeCommand.Execute(null);
        provide.Shapes[0].Name = "url";
        provide.Shapes[0].Template = "http://localhost:${sprig.fresh-api.port}";

        tab.AddNeedCommand.Execute(null);
        tab.Needs[0].Capability = "fresh-db";
        tab.Needs[0].As = "db";

        Assert.True(vm.Save());
        var reloaded = SprigConfigLoader.LoadFromFile(Path.Combine(dir, ".sprig.json"));
        var module = Assert.Single(reloaded.EffectiveModules);
        Assert.Equal("fresh-api", Assert.Single(module.Provides).Capability);
        Assert.True(module.Provides[0].Ports.ContainsKey("port"));
        Assert.Equal("http://localhost:${sprig.fresh-api.port}", module.Provides[0].Shapes["url"]);
        Assert.Equal("fresh-db", Assert.Single(module.Needs).Capability);
        Assert.Equal("db", module.Needs[0].As);
    }

    [Fact]
    public void Token_preview_tracks_the_capability_name_as_it_is_typed()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """{ "schema": 1, "name": "live" }""");
        var vm = RepoEditViewModel.Load(dir);

        vm.AddModuleCommand.Execute(null);
        var tab = vm.SelectedModule!;
        tab.Name = "app";
        tab.AddProvideCommand.Execute(null);
        var provide = tab.Provides[0];

        // Renaming the capability re-heads the port and every shape's rendered ${sprig.…} token, live.
        provide.Capability = "vite-server";
        Assert.Equal("${sprig.vite-server.port}", provide.Port.Ref);
        provide.AddShapeCommand.Execute(null);
        provide.Shapes[0].Name = "url";
        Assert.Equal("${sprig.vite-server.url}", provide.Shapes[0].Ref);
    }

    [Fact]
    public void Reference_lists_track_provides_and_needs_as_they_are_typed()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """{ "schema": 1, "name": "live" }""");
        var vm = RepoEditViewModel.Load(dir);

        vm.AddModuleCommand.Execute(null);
        var tab = vm.SelectedModule!;
        tab.Name = "app";

        // Typing a provide's capability makes its always-present ${sprig.<cap>.port} a known exact reference —
        // live, with no need to reload (Workstream C).
        tab.AddProvideCommand.Execute(null);
        var provide = tab.Provides[0];
        provide.Capability = "api";
        Assert.Contains("api.port", vm.SprigVariableNames);

        // Adding/naming a derived shape adds its reference too; the port anchor stays.
        provide.AddShapeCommand.Execute(null);
        provide.Shapes[0].Name = "url";
        Assert.Contains("api.url", vm.SprigVariableNames);
        Assert.Contains("api.port", vm.SprigVariableNames);

        // A need's capability + alias become open heads (any output accepted under them).
        tab.AddNeedCommand.Execute(null);
        tab.Needs[0].Capability = "db";
        tab.Needs[0].As = "primary";
        Assert.Contains("db", vm.SprigNeededCapabilities);
        Assert.Contains("primary", vm.SprigNeededCapabilities);

        // workspace is always present; churn stayed bounded (no recursion / overflow while editing).
        Assert.Contains("workspace", vm.SprigVariableNames);
    }
}
