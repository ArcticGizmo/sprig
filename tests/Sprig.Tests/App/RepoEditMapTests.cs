using Sprig.App.ViewModels;
using Sprig.Core.Config;

namespace Sprig.Tests.App;

/// <summary>M8 — authoring a repo's map-model surface (provides/needs) in the repo editor: load, edit, and
/// save a valid schema-aware .sprig.json.</summary>
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
            { "schema": 3, "name": "acme",
              "provides": [ { "capability": "acme-api", "type": "http",
                "outputs": { "port": { "port": true }, "url": "http://localhost:${sprig.acme-api.port}" } } ],
              "needs": [ { "capability": "acme-db", "as": "db" } ] }
            """);

        var vm = RepoEditViewModel.Load(dir);
        var tab = Assert.Single(vm.Modules);

        var provide = Assert.Single(tab.Provides);
        Assert.Equal("acme-api", provide.Capability);
        Assert.Equal("http", provide.Type);
        Assert.Collection(provide.Outputs,
            o => { Assert.Equal("port", o.Name); Assert.True(o.IsPort); },
            o => { Assert.Equal("url", o.Name); Assert.True(o.IsDerived); Assert.Contains("localhost", o.Template); });

        var need = Assert.Single(tab.Needs);
        Assert.Equal("acme-db", need.Capability);
        Assert.Equal("db", need.As);

        var config = vm.Build();
        var module = Assert.Single(config.EffectiveModules);
        Assert.True(module.Provides[0].Outputs["port"].IsPort);
        Assert.Equal("acme-db", Assert.Single(module.Needs).Capability);
        Assert.True(SprigConfigValidator.Validate(config).IsValid);
    }

    [Fact]
    public void Authors_a_provide_and_need_from_scratch_and_saves_valid_json()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """{ "schema": 3, "name": "fresh" }""");
        var vm = RepoEditViewModel.Load(dir);

        vm.AddModuleCommand.Execute(null);
        var tab = vm.SelectedModule!;
        tab.Name = "app";

        tab.AddProvideCommand.Execute(null);
        var provide = tab.Provides[0];
        provide.Capability = "fresh-api";
        provide.Outputs[0].Name = "port";
        provide.AddOutputCommand.Execute(null);
        provide.Outputs[1].Name = "url";
        provide.Outputs[1].IsPort = false;
        provide.Outputs[1].Template = "http://localhost:${sprig.fresh-api.port}";

        tab.AddNeedCommand.Execute(null);
        tab.Needs[0].Capability = "fresh-db";
        tab.Needs[0].As = "db";

        Assert.True(vm.Save());
        var reloaded = SprigConfigLoader.LoadFromFile(Path.Combine(dir, ".sprig.json"));
        var module = Assert.Single(reloaded.EffectiveModules);
        Assert.Equal("fresh-api", Assert.Single(module.Provides).Capability);
        Assert.True(module.Provides[0].Outputs["port"].IsPort);
        Assert.Equal("http://localhost:${sprig.fresh-api.port}", module.Provides[0].Outputs["url"].Template);
        Assert.Equal("fresh-db", Assert.Single(module.Needs).Capability);
        Assert.Equal("db", module.Needs[0].As);
    }

    [Fact]
    public void Reference_lists_track_provides_and_needs_as_they_are_typed()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """{ "schema": 3, "name": "live" }""");
        var vm = RepoEditViewModel.Load(dir);

        vm.AddModuleCommand.Execute(null);
        var tab = vm.SelectedModule!;
        tab.Name = "app";

        // Typing a provide's capability + output makes ${sprig.<cap>.<out>} a known exact reference — live,
        // with no need to reload (Workstream C).
        tab.AddProvideCommand.Execute(null);
        var provide = tab.Provides[0];
        provide.Capability = "api";
        provide.Outputs[0].Name = "port";
        Assert.Contains("api.port", vm.SprigVariableNames);

        // Renaming the output moves the known name with it (old name gone, new name present).
        provide.Outputs[0].Name = "listen";
        Assert.DoesNotContain("api.port", vm.SprigVariableNames);
        Assert.Contains("api.listen", vm.SprigVariableNames);

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
