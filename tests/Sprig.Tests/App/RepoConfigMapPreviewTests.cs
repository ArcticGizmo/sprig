using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>M8 â€” the read-only repo preview projects a module's map-model surface (provides/needs).</summary>
public class RepoConfigMapPreviewTests
{
    static string WriteConfig(TempStore s, string json)
    {
        var dir = Path.Combine(s.Root, "repo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), json);
        return dir;
    }

    [Fact]
    public void Projects_provides_and_needs_onto_the_module_tab()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """
            { "schema": 1, "name": "acme",
              "provides": [ { "capability": "acme-api",
                "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.acme-api.port}" } } ],
              "needs": [ { "value": "acme-db" }, { "value": "auth" } ] }
            """);

        var vm = RepoConfigViewModel.Load(dir);
        var module = Assert.Single(vm.Modules);

        var provide = Assert.Single(module.Provides);
        Assert.Equal("acme-api", provide.Capability);
        Assert.Contains(provide.Outputs, o => o.Key == "port" && o.Value == "port");
        Assert.Contains(provide.Outputs, o => o.Key == "url" && o.Value.Contains("localhost"));

        Assert.True(module.HasNeeds);
        Assert.Equal(["acme-db", "auth"], module.Needs.Select(n => n.Value));
    }
}
