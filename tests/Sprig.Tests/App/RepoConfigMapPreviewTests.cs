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
        // The port anchor and the derived shape both surface, each with its reference token.
        Assert.Contains(provide.Outputs, o => o.Name == "port" && o.IsPort && o.Reference == "${sprig.acme-api.port}");
        Assert.Contains(provide.Outputs, o => o.Name == "url" && !o.IsPort && o.Detail.Contains("localhost"));

        Assert.True(module.HasNeeds);
        Assert.Equal(["acme-db", "auth"], module.Needs.Select(n => n.Value));
    }

    [Fact]
    public void A_needs_used_outputs_are_read_off_the_overrides_in_the_preview()
    {
        using var s = new TempStore();
        var dir = WriteConfig(s, """
            { "schema": 1, "name": "acme",
              "needs": [ { "value": "api" } ],
              "env": [ { "file": ".env", "set": {
                  "VITE_API_URL": "${sprig.api.url}",
                  "VITE_API_PORT": "${sprig.api.port}"
              } } ] }
            """);

        var vm = RepoConfigViewModel.Load(dir);
        var need = Assert.Single(Assert.Single(vm.Modules).Needs);

        Assert.True(need.HasUsages);
        Assert.Equal(["port", "url"], need.Usages.Select(u => u.Output).OrderBy(o => o));
        var url = need.Usages.Single(u => u.Output == "url");
        Assert.Equal("${sprig.api.url}", url.Reference);
        Assert.Contains(".env · VITE_API_URL", url.Locations);
    }
}
