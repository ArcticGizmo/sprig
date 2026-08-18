using Sprig.Core.Config;

namespace Sprig.Tests.Config;

public class SprigConfigLoaderTests
{
    // A realistic, valid single-app (flat) config â€” its env/compose reference only ${sprig.workspace}.
    const string ValidJson = """
        {
          "schema": 1,
          "name": "dotnet-api",
          "env": [
            { "file": ".env.local", "set": { "NAME": "app--${sprig.workspace}" } }
          ],
          "compose": [
            {
              "file": "docker-compose.yml",
              "overrides": [
                { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
                { "path": ["services","postgres","image"], "template": "postgres--${sprig.workspace}" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Parses_full_valid_config()
    {
        // Schema v1: no migration. The flat env/compose stay on the record; EffectiveModules surfaces them
        // as the implicit "app" module every consumer iterates.
        var c = SprigConfigLoader.Parse(ValidJson);

        Assert.Equal(1, c.Schema);
        Assert.Equal("dotnet-api", c.Name);
        Assert.Empty(c.Modules);   // flat shape isn't rewritten into modules

        var module = Assert.Single(c.EffectiveModules);
        Assert.Equal(SprigRepoConfig.DefaultModuleName, module.Name);
        Assert.Equal("", module.Path);
        Assert.Single(module.Env);
        Assert.Equal(".env.local", module.Env[0].File);
        Assert.Equal("app--${sprig.workspace}", module.Env[0].Set["NAME"]);
        Assert.Single(module.Compose);
        Assert.Equal(2, module.Compose[0].Overrides.Count);
        Assert.Equal(["services", "postgres", "image"], module.Compose[0].Overrides[1].Path);
    }

    [Fact]
    public void Is_case_insensitive_on_property_names()
    {
        var c = SprigConfigLoader.Parse("""{ "Schema": 1, "NAME": "x" }""");
        Assert.Equal("x", c.Name);
    }

    [Fact]
    public void Missing_file_throws_with_path()
    {
        var ex = Assert.Throws<SprigConfigException>(
            () => SprigConfigLoader.LoadFromFile(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid() + ".json")));
        Assert.Contains("no .sprig.json", ex.Message);
    }

    [Fact]
    public void Malformed_json_throws()
    {
        var ex = Assert.Throws<SprigConfigException>(() => SprigConfigLoader.Parse("{ not json ", "x.json"));
        Assert.Contains("invalid JSON", ex.Message);
    }
}

public class SprigConfigValidatorTests
{
    static SprigRepoConfig Base() => new() { Name = "repo" };

    [Fact]
    public void Valid_config_has_no_issues()
    {
        var c = SprigConfigLoader.Parse("""
            {
              "schema": 1, "name": "dotnet-api",
              "modules": [ { "name": "app", "path": "",
                "provides": [ { "capability": "web", "ports": { "port": true } } ],
                "env": [ { "file": ".env.local", "set": { "PORT": "${sprig.web.port}" } } ],
                "compose": [ { "file": "docker-compose.yml", "overrides": [
                  { "path": ["services","postgres","container_name"], "template": "x--${sprig.workspace}" } ] } ] } ]
            }
            """);
        Assert.True(SprigConfigValidator.Validate(c).IsValid);
    }

    [Fact]
    public void Unsupported_schema_is_flagged()
        => Assert.Contains(SprigConfigValidator.Validate(Base() with { Schema = 99 }).Issues, i => i.Path == "schema");

    [Fact]
    public void Empty_name_is_flagged()
        => Assert.Contains(SprigConfigValidator.Validate(new SprigRepoConfig { Name = "" }).Issues, i => i.Path == "name");

    [Fact]
    public void Template_referencing_undeclared_input_is_flagged()
    {
        var r = SprigConfigValidator.Validate(Base() with
        {
            Env = [new() { File = ".env", Set = new Dictionary<string, string> { ["X"] = "${sprig.nope}" } }]
        });
        Assert.Contains(r.Issues, i => i.Path == "template" && i.Message.Contains("nope"));
    }

    [Fact]
    public void Env_override_needs_file_and_at_least_one_key()
    {
        var r = SprigConfigValidator.Validate(Base() with
        {
            Env = [new() { File = "", Set = new Dictionary<string, string>() }]
        });
        Assert.Contains(r.Issues, i => i.Path == "env[0].file");
        Assert.Contains(r.Issues, i => i.Path == "env[0].set");
    }

    [Fact]
    public void Compose_override_needs_path_and_template()
    {
        var r = SprigConfigValidator.Validate(Base() with
        {
            Compose = [new ComposeConfig { File = "docker-compose.yml", Overrides = [new() { Path = [], Template = "" }] }]
        });
        Assert.Contains(r.Issues, i => i.Path == "compose[0].overrides[0].path");
        Assert.Contains(r.Issues, i => i.Path == "compose[0].overrides[0].template");
    }

    [Fact]
    public void Unknown_top_level_key_is_flagged()
    {
        var c = SprigConfigLoader.Parse("""{ "schema": 1, "name": "x", "bogus": 1 }""");
        Assert.Contains(SprigConfigValidator.Validate(c).Issues, i => i.Path == "bogus" && i.Message.Contains("unknown"));
    }

    static SprigRepoConfig Module(string name, string path = "",
        IReadOnlyList<EnvOverride>? env = null, IReadOnlyList<ComposeConfig>? compose = null,
        IReadOnlyList<string>? setup = null) =>
        Base() with { Modules = [new ModuleDeclaration {
            Name = name, Path = path, Env = env ?? [], Compose = compose ?? [], Setup = setup ?? [] }] };

    [Fact]
    public void Valid_module_config_has_no_issues()
    {
        var c = Base() with
        {
            Modules =
            [
                new ModuleDeclaration { Name = "web", Path = "apps/web",
                    Provides = [new ProvidedCapability { Capability = "web", Ports = new Dictionary<string, PortSpec> { ["port"] = PortSpec.Any } }],
                    Env = [new() { File = ".env.local", Set = new Dictionary<string, string> { ["PORT"] = "${sprig.web.port}" } }] },
                new ModuleDeclaration { Name = "api", Path = "apps/api",
                    Setup = ["dotnet restore"] },
            ],
        };
        Assert.True(SprigConfigValidator.Validate(c).IsValid);
    }

    [Fact]
    public void Duplicate_module_name_is_flagged_case_insensitively()
    {
        var c = Base() with { Modules = [new() { Name = "web" }, new() { Name = "WEB" }] };
        Assert.Contains(SprigConfigValidator.Validate(c).Issues,
            i => i.Path == "modules[1].name" && i.Message.Contains("duplicate"));
    }

    [Fact]
    public void Invalid_module_name_chars_are_flagged()
        => Assert.Contains(SprigConfigValidator.Validate(Module("has space")).Issues, i => i.Path == "modules[0].name");

    [Theory]
    [InlineData("../evil")]
    [InlineData("/abs/path")]
    [InlineData("C:/somewhere")]
    [InlineData("apps/../../escape")]
    public void Unsafe_module_path_is_flagged(string path)
        => Assert.Contains(SprigConfigValidator.Validate(Module("web", path)).Issues, i => i.Path == "modules[0].path");

    [Fact]
    public void Nested_but_safe_module_path_is_allowed()
        => Assert.DoesNotContain(SprigConfigValidator.Validate(Module("web", "apps/web/client")).Issues, i => i.Path == "modules[0].path");

    [Fact]
    public void Same_compose_file_in_two_modules_at_different_paths_is_allowed()
    {
        var c = Base() with
        {
            Modules =
            [
                new ModuleDeclaration { Name = "web", Path = "apps/web",
                    Compose = [new() { File = "docker-compose.yml", Overrides = [new() { Path = ["services", "x", "image"], Template = "x" }] }] },
                new ModuleDeclaration { Name = "api", Path = "apps/api",
                    Compose = [new() { File = "docker-compose.yml", Overrides = [new() { Path = ["services", "y", "image"], Template = "y" }] }] },
            ],
        };
        Assert.DoesNotContain(SprigConfigValidator.Validate(c).Issues, i => i.Message.Contains("duplicate compose"));
    }

    [Fact]
    public void Same_effective_compose_path_across_modules_is_flagged()
    {
        var c = Base() with
        {
            Modules =
            [
                new ModuleDeclaration { Name = "a", Path = "apps",
                    Compose = [new() { File = "web/docker-compose.yml", Overrides = [new() { Path = ["services", "x", "image"], Template = "x" }] }] },
                new ModuleDeclaration { Name = "b", Path = "apps/web",
                    Compose = [new() { File = "docker-compose.yml", Overrides = [new() { Path = ["services", "y", "image"], Template = "y" }] }] },
            ],
        };
        Assert.Contains(SprigConfigValidator.Validate(c).Issues, i => i.Message.Contains("duplicate compose"));
    }

    [Fact]
    public void Template_referencing_undeclared_input_inside_a_module_is_flagged()
    {
        var c = Module("web", env: [new() { File = ".env", Set = new Dictionary<string, string> { ["X"] = "${sprig.nope}" } }]);
        Assert.Contains(SprigConfigValidator.Validate(c).Issues, i => i.Path == "template" && i.Message.Contains("nope"));
    }

    [Fact]
    public void Blank_setup_command_inside_a_module_is_flagged()
    {
        var c = Module("web", setup: ["npm ci", "   "]);
        Assert.Contains(SprigConfigValidator.Validate(c).Issues, i => i.Path == "modules[0].setup[1]");
    }
}

// Schema v1: there is no migration. A single-app config may write env/compose/setup at the top level
// instead of inside a module, and SprigRepoConfig.EffectiveModules surfaces that as one implicit "app"
// module â€” so every consumer sees a single module shape without the file being rewritten.
public class FlatConfigSugarTests
{
    const string Flat = """
        {
          "schema": 1,
          "name": "dotnet-api",
          "env": [ { "file": ".env", "set": { "NAME": "app--${sprig.workspace}" } } ],
          "compose": [ { "file": "docker-compose.yml", "overrides": [
            { "path": ["services","db","container_name"], "template": "db--${sprig.workspace}" } ] } ],
          "setup": [ "dotnet restore" ]
        }
        """;

    [Fact]
    public void A_flat_config_surfaces_its_top_level_sugar_as_the_implicit_app_module()
    {
        var c = SprigConfigLoader.Parse(Flat);

        // The file keeps its flat shape (no migration rewrites it) â€¦
        Assert.Equal(1, c.Schema);
        Assert.Empty(c.Modules);
        // â€¦ but EffectiveModules folds it into one "app" module every consumer iterates.
        var m = Assert.Single(c.EffectiveModules);
        Assert.Equal("app", m.Name);
        Assert.Equal("", m.Path);
        Assert.Equal(".env", m.Env[0].File);
        Assert.Equal("docker-compose.yml", m.Compose[0].File);
        Assert.Equal(["dotnet restore"], m.Setup);
    }

    [Fact]
    public void A_config_with_no_flat_surface_and_no_modules_has_no_effective_modules()
    {
        var c = SprigConfigLoader.Parse("""{ "schema": 1, "name": "x" }""");
        Assert.Empty(c.EffectiveModules);
    }

    [Fact]
    public void A_module_shaped_config_is_left_as_is()
    {
        var c = SprigConfigLoader.Parse("""
            { "schema": 1, "name": "x",
              "modules": [ { "name": "web", "path": "apps/web", "setup": ["npm ci"] } ] }
            """);
        var m = Assert.Single(c.EffectiveModules);
        Assert.Equal("web", m.Name);
        Assert.Equal("apps/web", m.Path);
    }

    [Fact]
    public void Unknown_top_level_keys_are_captured()
    {
        var c = SprigConfigLoader.Parse("""{ "schema": 1, "name": "x", "bogus": 1 }""");
        Assert.Contains("bogus", c.Unknown.Keys);
    }
}
