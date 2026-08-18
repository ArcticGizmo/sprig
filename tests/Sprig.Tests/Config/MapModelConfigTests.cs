using System.Text.Json;
using Sprig.Core.Config;

namespace Sprig.Tests.Config;

// The map-model repo surface (provides/needs, capability-qualified references) — schema v1. A provided
// capability is ONE name that comes in many shapes: real ports it owns, plus derived strings over them.

public class PortSpecConverterTests
{
    [Fact]
    public void Any_port_round_trips_as_true()
    {
        var json = JsonSerializer.Serialize(PortSpec.Any);
        Assert.Equal("true", json);
        var back = JsonSerializer.Deserialize<PortSpec>(json)!;
        Assert.Null(back.Allowed);
    }

    [Fact]
    public void Constrained_port_round_trips_as_object()
    {
        var json = JsonSerializer.Serialize(PortSpec.Constrained("8100-8103"));
        Assert.Equal("""{"allowed":"8100-8103"}""", json);
        var back = JsonSerializer.Deserialize<PortSpec>(json)!;
        Assert.Equal("8100-8103", back.Allowed);
    }

    [Fact]
    public void Bare_object_and_string_both_read_as_a_port()
    {
        Assert.Null(JsonSerializer.Deserialize<PortSpec>("{}")!.Allowed);
        Assert.Equal("8100-8103", JsonSerializer.Deserialize<PortSpec>("\"8100-8103\"")!.Allowed);
    }

    [Fact]
    public void A_derived_shape_is_a_plain_string()
    {
        // Shapes carry no converter — they are ordinary JSON strings in the `shapes` map.
        var shapes = JsonSerializer.Deserialize<Dictionary<string, string>>(
            """{ "url": "http://localhost:${sprig.api.port}" }""")!;
        Assert.Equal("http://localhost:${sprig.api.port}", shapes["url"]);
    }

    [Fact]
    public void Junk_port_shapes_are_rejected()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PortSpec>("42"));
}

public class MapModelParseTests
{
    const string Mono = """
        {
          "schema": 1,
          "name": "acme",
          "modules": [
            { "name": "api", "path": "apps/api",
              "provides": [ { "capability": "acme-api",
                "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.acme-api.port}" } } ],
              "needs": [ { "value": "acme-db" } ],
              "env": [ { "file": ".env", "set": { "PORT": "${sprig.acme-api.port}", "DB": "${sprig.acme-db.connString}" } } ] },
            { "name": "web", "path": "apps/web",
              "needs": [ { "value": "acme-api" } ],
              "env": [ { "file": ".env.local", "set": { "VITE_API": "${sprig.acme-api.url}" } } ] },
            { "name": "db", "path": "infra",
              "provides": [ { "capability": "acme-db",
                "ports": { "port": true },
                "shapes": { "connString": "Host=localhost;Port=${sprig.acme-db.port};Database=acme" } } ] }
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
        Assert.True(apiCap.Ports.ContainsKey("port"));
        Assert.Equal("http://localhost:${sprig.acme-api.port}", apiCap.Shapes["url"]);
        var apiNeed = Assert.Single(api.Needs);
        Assert.Equal("acme-db", apiNeed.Value);

        Assert.Equal("acme-api", Assert.Single(c.Modules[1].Needs).Value);
        var dbCap = Assert.Single(c.Modules[2].Provides);
        Assert.Equal("acme-db", dbCap.Capability);
        Assert.Contains("connString", dbCap.Shapes.Keys);
    }

    [Fact]
    public void Monorepo_is_valid_with_local_and_cross_module_references()
        => Assert.True(SprigConfigValidator.Validate(SprigConfigLoader.Parse(Mono)).IsValid);

    [Fact]
    public void Single_app_sugar_folds_provides_needs_into_the_implicit_module()
    {
        var c = SprigConfigLoader.Parse("""
            { "schema": 1, "name": "solo",
              "provides": [ { "capability": "solo-api", "ports": { "port": true } } ],
              "needs": [ { "value": "ext-db" } ],
              "env": [ { "file": ".env", "set": { "PORT": "${sprig.solo-api.port}", "DB": "${sprig.ext-db.url}" } } ] }
            """);

        var module = Assert.Single(c.EffectiveModules);
        Assert.Equal(SprigRepoConfig.DefaultModuleName, module.Name);
        Assert.Equal("solo-api", Assert.Single(module.Provides).Capability);
        Assert.Equal("ext-db", Assert.Single(module.Needs).Value);
        Assert.True(SprigConfigValidator.Validate(c).IsValid);
    }
}

public class MapModelValidationTests
{
    static SprigRepoConfig Repo(params ModuleDeclaration[] modules)
        => new() { Name = "r", Modules = modules };

    // A capability's outputs, tuple-style: a PortSpec value becomes a port, a string becomes a derived shape.
    static ProvidedCapability Cap(string name, params (string Name, object Spec)[] outputs)
    {
        var ports = new Dictionary<string, PortSpec>(StringComparer.Ordinal);
        var shapes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (n, spec) in outputs)
        {
            if (spec is PortSpec p) ports[n] = p;
            else shapes[n] = (string)spec;
        }
        return new() { Capability = name, Ports = ports, Shapes = shapes };
    }

    static ModuleDeclaration Mod(string name) => new() { Name = name };

    [Fact]
    public void Duplicate_provided_capability_across_modules_is_flagged()
    {
        var r = Repo(
            Mod("a") with { Provides = [Cap("dup", ("port", PortSpec.Any))] },
            Mod("b") with { Provides = [Cap("dup", ("port", PortSpec.Any))] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Message.Contains("duplicate provided capability"));
    }

    [Fact]
    public void Dotted_capability_name_is_flagged()
    {
        var r = Repo(Mod("a") with { Provides = [Cap("bad.name", ("port", PortSpec.Any))] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Path.Contains("capability"));
    }

    [Fact]
    public void Bad_allowed_port_spec_is_flagged()
    {
        var r = Repo(Mod("a") with { Provides = [Cap("api", ("port", PortSpec.Constrained("not-a-range")))] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Path.Contains("allowed"));
    }

    [Fact]
    public void A_capability_with_no_port_or_shape_is_flagged()
    {
        var r = Repo(Mod("a") with { Provides = [new ProvidedCapability { Capability = "empty" }] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Message.Contains("at least one port or shape"));
    }

    [Fact]
    public void Derived_shape_with_empty_template_is_flagged()
    {
        var r = Repo(Mod("a") with { Provides = [Cap("api", ("url", ""))] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Path.Contains("shapes"));
    }

    [Fact]
    public void A_port_and_a_shape_sharing_a_name_is_flagged()
    {
        var r = Repo(Mod("a") with { Provides = [Cap("api", ("port", PortSpec.Any), ("port", "http://x"))] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Message.Contains("duplicate output name"));
    }

    [Fact]
    public void A_derived_shape_that_references_itself_is_flagged()
    {
        var r = Repo(Mod("a") with { Provides = [Cap("api", ("port", PortSpec.Any), ("url", "${sprig.api.url}"))] });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Message.Contains("cannot reference itself"));
    }

    [Fact]
    public void A_cycle_between_derived_shapes_is_flagged()
    {
        var r = Repo(Mod("a") with
        {
            Provides = [Cap("api", ("port", PortSpec.Any), ("a", "${sprig.api.b}"), ("b", "${sprig.api.a}"))],
        });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues, i => i.Message.Contains("circular dependency"));
    }

    [Fact]
    public void A_derived_shape_referencing_another_capability_is_flagged()
    {
        var r = Repo(Mod("a") with
        {
            Provides =
            [
                Cap("db", ("port", PortSpec.Any)),
                Cap("api", ("port", PortSpec.Any), ("bad", "${sprig.db.port}")),   // must not reach across capabilities
            ],
        });
        Assert.Contains(SprigConfigValidator.Validate(r).Issues,
            i => i.Message.Contains("may only reference this capability's own outputs"));
    }

    [Fact]
    public void A_derived_shape_may_reference_its_own_port_and_sibling_shapes()
    {
        var r = Repo(Mod("a") with
        {
            Provides =
            [
                Cap("api",
                    ("port", PortSpec.Any),
                    ("url", "http://localhost:${sprig.api.port}"),
                    ("health", "${sprig.api.url}/healthz")),   // sibling-shape reference, acyclic
            ],
        });
        Assert.True(SprigConfigValidator.Validate(r).IsValid);
    }

    [Fact]
    public void Reference_to_self_provided_output_is_accepted()
    {
        var r = Repo(Mod("a") with
        {
            Provides = [Cap("api", ("port", PortSpec.Any))],
            Env = [new() { File = ".env", Set = new Dictionary<string, string> { ["PORT"] = "${sprig.api.port}" } }],
        });
        Assert.True(SprigConfigValidator.Validate(r).IsValid);
    }

    [Fact]
    public void Reference_to_a_needed_value_output_is_accepted_unchecked()
    {
        // The provider (and thus the output list) lives in another repo - the head must match a need; the
        // output ('connString') is validated at map-resolve time, not here.
        var r = Repo(Mod("a") with
        {
            Needs = [new() { Value = "db" }],
            Env = [new() { File = ".env", Set = new Dictionary<string, string> { ["DB"] = "${sprig.db.connString}" } }],
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
            Provides = [Cap("api", ("port", PortSpec.Any))],
            Env = [new() { File = ".env", Set = new Dictionary<string, string> { ["X"] = "${sprig.api.host}" } }],
        });
        Assert.Contains("api.host", ConfigReferences.UndeclaredReferences(r));
    }
}
