using System.Text.Json;
using Sprig.Core.Config;

namespace Sprig.Tests.Config;

// M1 — the map-model repo surface (provides/needs, capability-qualified references). These live
// alongside the stack-era `inputs` during the transition; fixtures declare the current transitional
// schema (3) and are flipped to v1 at M7.

public class OutputSpecConverterTests
{
    [Fact]
    public void Port_output_round_trips_as_object()
    {
        var json = JsonSerializer.Serialize(OutputSpec.Port());
        Assert.Equal("""{"port":true}""", json);
        var back = JsonSerializer.Deserialize<OutputSpec>(json)!;
        Assert.True(back.IsPort);
        Assert.Null(back.Allowed);
        Assert.Null(back.Template);
    }

    [Fact]
    public void Port_output_keeps_allowed_set()
    {
        var json = JsonSerializer.Serialize(OutputSpec.Port("8100-8103"));
        Assert.Contains("\"allowed\":\"8100-8103\"", json);
        var back = JsonSerializer.Deserialize<OutputSpec>(json)!;
        Assert.True(back.IsPort);
        Assert.Equal("8100-8103", back.Allowed);
    }

    [Fact]
    public void Derived_output_round_trips_as_string()
    {
        var json = JsonSerializer.Serialize(OutputSpec.Derived("http://localhost:${sprig.api.port}"));
        Assert.Equal("\"http://localhost:${sprig.api.port}\"", json);
        var back = JsonSerializer.Deserialize<OutputSpec>(json)!;
        Assert.False(back.IsPort);
        Assert.Equal("http://localhost:${sprig.api.port}", back.Template);
    }

    [Fact]
    public void Non_bool_port_and_junk_shapes_are_rejected()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OutputSpec>("42"));
}

public class MapModelParseTests
{
    const string Mono = """
        {
          "schema": 1,
          "name": "acme",
          "modules": [
            { "name": "api", "path": "apps/api",
              "provides": [ { "capability": "acme-api", "type": "http",
                "outputs": { "port": { "port": true }, "url": "http://localhost:${sprig.acme-api.port}" } } ],
              "needs": [ { "capability": "acme-db", "as": "db" } ],
              "env": [ { "file": ".env", "set": { "PORT": "${sprig.acme-api.port}", "DB": "${sprig.db.connString}" } } ] },
            { "name": "web", "path": "apps/web",
              "needs": [ { "capability": "acme-api" } ],
              "env": [ { "file": ".env.local", "set": { "VITE_API": "${sprig.acme-api.url}" } } ] },
            { "name": "db", "path": "infra",
              "provides": [ { "capability": "acme-db", "type": "postgres",
                "outputs": { "port": { "port": true },
                  "connString": "Host=localhost;Port=${sprig.acme-db.port};Database=acme" } } ] }
          ]
        }
        """;

    [Fact]
    public void Parses_monorepo_provides_and_needs()
    {
        var c = SprigConfigLoader.Parse(Mono);
        Assert.Equal(3, c.Modules.Count);

        var api = c.Modules[0];
        var apiCap = Assert.Single(api.Provides);
        Assert.Equal("acme-api", apiCap.Capability);
        Assert.Equal("http", apiCap.Type);
        Assert.True(apiCap.Outputs["port"].IsPort);
        Assert.Equal("http://localhost:${sprig.acme-api.port}", apiCap.Outputs["url"].Template);
        var apiNeed = Assert.Single(api.Needs);
        Assert.Equal("acme-db", apiNeed.Capability);
        Assert.Equal("db", apiNeed.Alias);

        Assert.Equal("acme-api", Assert.Single(c.Modules[1].Needs).Capability);
        Assert.Equal("acme-api", c.Modules[1].Needs[0].Alias);   // no alias → defaults to capability
        Assert.Equal("acme-db", Assert.Single(c.Modules[2].Provides).Capability);
    }

    [Fact]
    public void Monorepo_is_valid_with_local_and_cross_module_references()
        => Assert.True(SprigConfigValidator.Validate(SprigConfigLoader.Parse(Mono)).IsValid);

    [Fact]
    public void Single_app_sugar_folds_provides_needs_into_the_implicit_module()
    {
        var c = SprigConfigLoader.Parse("""
            { "schema": 1, "name": "solo",
              "provides": [ { "capability": "solo-api", "outputs": { "port": { "port": true } } } ],
              "needs": [ { "capability": "ext-db", "as": "db" } ],
              "env": [ { "file": ".env", "set": { "PORT": "${sprig.solo-api.port}", "DB": "${sprig.db.url}" } } ] }
            """);

        var module = Assert.Single(c.EffectiveModules);
        Assert.Equal(SprigRepoConfig.DefaultModuleName, module.Name);
        Assert.Equal("solo-api", Assert.Single(module.Provides).Capability);
        Assert.Equal("ext-db", Assert.Single(module.Needs).Capability);
        Assert.True(SprigConfigValidator.Validate(c).IsValid);
    }
}

public class MapModelValidationTests
{
    static SprigRepoConfig Repo(params ModuleDeclaration[] modules)
        => new() { Name = "r", Modules = modules };

    static ProvidedCapability Cap(string name, params (string, OutputSpec)[] outputs)
        => new() { Capability = name, Outputs = outputs.ToDictionary(o => o.Item1, o => o.Item2) };

    static ModuleDeclaration Mod(string name) => new() { Name = name };

    [Fact]
    public void Duplicate_provided_capability_across_modules_is_flagged()
    {
        var r = Repo(
            Mod("a") with { Provides = [Cap("dup", ("port", OutputSpec.Port()))] },
            Mod("b") with { Provides = [Cap("dup", ("port", OutputSpec.Port()))] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Message.Contains("duplicate provided capability"));
    }

    [Fact]
    public void Dotted_capability_name_is_flagged()
    {
        var r = Repo(Mod("a") with { Provides = [Cap("bad.name", ("port", OutputSpec.Port()))] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Path.Contains("capability"));
    }

    [Fact]
    public void Bad_allowed_port_spec_is_flagged()
    {
        var r = Repo(Mod("a") with { Provides = [Cap("api", ("port", OutputSpec.Port("not-a-range")))] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Path.Contains("allowed"));
    }

    [Fact]
    public void Derived_output_with_empty_template_is_flagged()
    {
        var r = Repo(Mod("a") with { Provides = [Cap("api", ("url", new OutputSpec { Template = "" }))] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Path.Contains("outputs"));
    }

    [Fact]
    public void Reference_to_self_provided_output_is_accepted()
    {
        var r = Repo(Mod("a") with
        {
            Provides = [Cap("api", ("port", OutputSpec.Port()))],
            Env = [new() { File = ".env", Set = new Dictionary<string, string> { ["PORT"] = "${sprig.api.port}" } }],
        });
        Assert.True(SprigConfigValidator.Validate(r).IsValid);
    }

    [Fact]
    public void Reference_to_a_needed_capability_output_is_accepted_unchecked()
    {
        // The provider (and thus the output list) lives in another repo — the head must match a need; the
        // output ('connString') is validated at map-resolve time, not here.
        var r = Repo(Mod("a") with
        {
            Needs = [new() { Capability = "db", As = "database" }],
            Env = [new() { File = ".env", Set = new Dictionary<string, string> { ["DB"] = "${sprig.database.connString}" } }],
        });
        Assert.True(SprigConfigValidator.Validate(r).IsValid);
    }

    [Fact]
    public void Reference_to_an_unknown_capability_is_flagged()
    {
        var r = Repo(Mod("a") with
        {
            Env = [new() { File = ".env", Set = new Dictionary<string, string> { ["X"] = "${sprig.ghost.port}" } }],
        });
        var undeclared = ConfigReferences.UndeclaredReferences(r);
        Assert.Contains("ghost.port", undeclared);
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Path == "template");
    }

    [Fact]
    public void Reference_to_a_self_provided_capabilitys_unknown_output_is_flagged()
    {
        var r = Repo(Mod("a") with
        {
            Provides = [Cap("api", ("port", OutputSpec.Port()))],
            Env = [new() { File = ".env", Set = new Dictionary<string, string> { ["X"] = "${sprig.api.host}" } }],
        });
        Assert.Contains("api.host", ConfigReferences.UndeclaredReferences(r));
    }
}
