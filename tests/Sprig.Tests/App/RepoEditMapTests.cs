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
              "needs": [ { "value": "acme-db" } ] }
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
        Assert.Equal("acme-db", need.Value);

        var config = vm.Build();
        var module = Assert.Single(config.EffectiveModules);
        Assert.True(module.Provides[0].Ports.ContainsKey("port"));
        Assert.Equal("http://localhost:${sprig.acme-api.port}", module.Provides[0].Shapes["url"]);
        Assert.Equal("acme-db", Assert.Single(module.Needs).Value);
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
        tab.Needs[0].Value = "fresh-db";

        Assert.True(vm.Save());
        var reloaded = SprigConfigLoader.LoadFromFile(Path.Combine(dir, ".sprig.json"));
        var module = Assert.Single(reloaded.EffectiveModules);
        Assert.Equal("fresh-api", Assert.Single(module.Provides).Capability);
        Assert.True(module.Provides[0].Ports.ContainsKey("port"));
        Assert.Equal("http://localhost:${sprig.fresh-api.port}", module.Provides[0].Shapes["url"]);
        Assert.Equal("fresh-db", Assert.Single(module.Needs).Value);
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
    public void A_derived_shapes_autocomplete_offers_siblings_but_never_itself()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """{ "schema": 1, "name": "live" }""");
        var vm = RepoEditViewModel.Load(dir);
        vm.AddModuleCommand.Execute(null);
        var tab = vm.SelectedModule!;
        tab.Name = "app";
        tab.AddProvideCommand.Execute(null);
        var provide = tab.Provides[0];
        provide.Capability = "vite-server";

        provide.AddShapeCommand.Execute(null);
        provide.Shapes[0].Name = "url";
        provide.AddShapeCommand.Execute(null);
        provide.Shapes[1].Name = "health";

        var url = provide.Shapes[0];
        Assert.Contains("vite-server.port", url.Variables);      // the port anchor
        Assert.Contains("vite-server.health", url.Variables);    // a sibling shape
        Assert.Contains("workspace", url.Variables);
        Assert.DoesNotContain("vite-server.url", url.Variables); // never itself

        var health = provide.Shapes[1];
        Assert.Contains("vite-server.url", health.Variables);
        Assert.DoesNotContain("vite-server.health", health.Variables);
    }

    [Fact]
    public void A_self_referencing_shape_shows_a_live_error_that_clears_when_fixed()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """{ "schema": 1, "name": "live" }""");
        var vm = RepoEditViewModel.Load(dir);
        vm.AddModuleCommand.Execute(null);
        var tab = vm.SelectedModule!;
        tab.Name = "app";
        tab.AddProvideCommand.Execute(null);
        var provide = tab.Provides[0];
        provide.Capability = "api";
        provide.AddShapeCommand.Execute(null);
        var url = provide.Shapes[0];
        url.Name = "url";

        url.Template = "${sprig.api.url}";                        // references itself
        Assert.NotNull(url.ReferenceError);
        Assert.Contains("itself", url.ReferenceError);

        url.Template = "http://localhost:${sprig.api.port}";      // now valid
        Assert.Null(url.ReferenceError);
    }

    [Fact]
    public void A_circular_reference_between_shapes_shows_a_live_error_on_both()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """{ "schema": 1, "name": "live" }""");
        var vm = RepoEditViewModel.Load(dir);
        vm.AddModuleCommand.Execute(null);
        var tab = vm.SelectedModule!;
        tab.Name = "app";
        tab.AddProvideCommand.Execute(null);
        var provide = tab.Provides[0];
        provide.Capability = "api";

        provide.AddShapeCommand.Execute(null);
        provide.Shapes[0].Name = "a";
        provide.AddShapeCommand.Execute(null);
        provide.Shapes[1].Name = "b";
        provide.Shapes[0].Template = "${sprig.api.b}";
        provide.Shapes[1].Template = "${sprig.api.a}";

        Assert.NotNull(provide.Shapes[0].ReferenceError);
        Assert.NotNull(provide.Shapes[1].ReferenceError);
        Assert.Contains("circular", provide.Shapes[0].ReferenceError);
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

        // A need's value name becomes an open head (any output accepted under it).
        tab.AddNeedCommand.Execute(null);
        tab.Needs[0].Value = "db";
        Assert.Contains("db", vm.SprigNeededCapabilities);

        // workspace is always present; churn stayed bounded (no recursion / overflow while editing).
        Assert.Contains("workspace", vm.SprigVariableNames);
    }

    const string RefsButUndeclared = """
        { "schema": 1, "name": "acme",
          "modules": [ { "name": "app",
            "env": [ { "file": ".env", "set": {
                "CACHE_PORT": "${sprig.cache.port}",
                "CACHE_URL": "${sprig.cache.url}"
            } } ] } ] }
        """;

    [Fact]
    public void A_reference_matching_no_provide_or_need_surfaces_as_a_quick_add()
    {
        using var s = new TempStore();
        var vm = RepoEditViewModel.Load(WriteConfig(s, RefsButUndeclared));
        var tab = Assert.Single(vm.Modules);

        // 'cache' is referenced by the env overrides but neither provided nor needed — one chip per head.
        Assert.True(tab.HasUnresolvedReferences);
        Assert.Equal(["cache"], tab.UnresolvedReferences);
    }

    [Fact]
    public void Quick_add_need_declares_the_value_and_clears_the_chip()
    {
        using var s = new TempStore();
        var vm = RepoEditViewModel.Load(WriteConfig(s, RefsButUndeclared));
        var tab = Assert.Single(vm.Modules);

        tab.AddUnresolvedNeedCommand.Execute("cache");

        // A need is an open head, so every ${sprig.cache.*} reference resolves at once — chip gone.
        Assert.Equal("cache", Assert.Single(tab.Needs).Value);
        Assert.Empty(tab.UnresolvedReferences);
        Assert.False(tab.HasUnresolvedReferences);
    }

    [Fact]
    public void Quick_add_provide_scaffolds_a_shape_for_each_referenced_output()
    {
        using var s = new TempStore();
        var vm = RepoEditViewModel.Load(WriteConfig(s, RefsButUndeclared));
        var tab = Assert.Single(vm.Modules);

        tab.AddUnresolvedProvideCommand.Execute("cache");

        // The capability owns its port (covers cache.port); the referenced 'url' output is stubbed as a shape
        // (covers cache.url), so both references become declared and the chip clears.
        var provide = Assert.Single(tab.Provides);
        Assert.Equal("cache", provide.Capability);
        Assert.Equal("url", Assert.Single(provide.Shapes).Name);
        Assert.Empty(tab.UnresolvedReferences);
    }

    [Fact]
    public void Quick_add_is_idempotent_and_does_not_duplicate_a_row()
    {
        using var s = new TempStore();
        var vm = RepoEditViewModel.Load(WriteConfig(s, RefsButUndeclared));
        var tab = Assert.Single(vm.Modules);

        tab.AddUnresolvedNeedCommand.Execute("cache");
        tab.AddUnresolvedNeedCommand.Execute("cache");   // no-op: already declared

        Assert.Single(tab.Needs);
    }

    const string NeedWithReferences = """
        { "schema": 1, "name": "acme",
          "modules": [ { "name": "app",
            "needs": [ { "value": "api" }, { "value": "unused" } ],
            "env": [ { "file": ".env", "set": {
                "VITE_API_URL": "${sprig.api.url}",
                "VITE_API_PORT": "${sprig.api.port}"
            } } ] } ] }
        """;

    [Fact]
    public void A_needs_used_outputs_are_read_off_where_it_is_referenced()
    {
        using var s = new TempStore();
        var vm = RepoEditViewModel.Load(WriteConfig(s, NeedWithReferences));
        var tab = Assert.Single(vm.Modules);

        var api = tab.Needs.Single(n => n.Value == "api");
        Assert.True(api.HasUsages);
        Assert.Equal(["port", "url"], api.Usages.Select(u => u.Output).OrderBy(o => o));

        var url = api.Usages.Single(u => u.Output == "url");
        Assert.Equal("${sprig.api.url}", url.Reference);
        Assert.Contains(".env · VITE_API_URL", url.Locations);
    }

    [Fact]
    public void A_need_referenced_nowhere_shows_no_used_outputs()
    {
        using var s = new TempStore();
        var vm = RepoEditViewModel.Load(WriteConfig(s, NeedWithReferences));
        var tab = Assert.Single(vm.Modules);

        var unused = tab.Needs.Single(n => n.Value == "unused");
        Assert.False(unused.HasUsages);
        Assert.Empty(unused.Usages);
    }

    [Fact]
    public void Used_outputs_recompute_live_as_the_value_name_changes()
    {
        using var s = new TempStore();
        var vm = RepoEditViewModel.Load(WriteConfig(s, NeedWithReferences));
        var tab = Assert.Single(vm.Modules);
        var need = tab.Needs.Single(n => n.Value == "api");

        // Rename the value away from what the overrides reference — its used outputs fall away.
        need.Value = "api2";
        Assert.False(need.HasUsages);

        // Rename it back — the ${sprig.api.*} references bind to it again, live, with no reload.
        need.Value = "api";
        Assert.Equal(["port", "url"], need.Usages.Select(u => u.Output).OrderBy(o => o));
    }
}
