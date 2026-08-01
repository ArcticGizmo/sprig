using Sprig.Core.Config;

namespace Sprig.Tests.Config;

public class SprigConfigLoaderTests
{
    // A realistic, valid config: repo declares the inputs it needs; env/compose reference them.
    const string ValidJson = """
        {
          "schema": 2,
          "name": "dotnet-api",
          "inputs": [
            { "name": "port", "example": "5000", "description": "web host" },
            { "name": "dbPort", "example": "5432" }
          ],
          "env": [
            { "file": ".env.local", "set": { "PORT": "${sprig.port}" } }
          ],
          "compose": [
            {
              "file": "docker-compose.yml",
              "overrides": [
                { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
                { "path": ["services","postgres","ports","0"], "template": "${sprig.dbPort}:5432" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void Parses_full_valid_config()
    {
        // Parsing migrates a schema-2 file to schema 3: the flat env/compose surface is folded into a
        // single default module; inputs stay at the repo level; the top-level lists are cleared.
        var c = SprigConfigLoader.Parse(ValidJson);

        Assert.Equal(3, c.Schema);
        Assert.Equal("dotnet-api", c.Name);
        Assert.Equal(["port", "dbPort"], c.Inputs.Select(i => i.Name));
        Assert.Equal("5000", c.Inputs[0].Example);
        Assert.Null(c.Env);       // legacy top-level cleared on migration (omitted on write)
        Assert.Null(c.Compose);

        var module = Assert.Single(c.Modules);
        Assert.Equal(SprigConfigMigration.DefaultModuleName, module.Name);
        Assert.Equal("", module.Path);
        Assert.Single(module.Env);
        Assert.Equal(".env.local", module.Env[0].File);
        Assert.Equal("${sprig.port}", module.Env[0].Set["PORT"]);
        Assert.Single(module.Compose);
        Assert.Equal(2, module.Compose[0].Overrides.Count);
        Assert.Equal(["services", "postgres", "ports", "0"], module.Compose[0].Overrides[1].Path);
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
              "schema": 2, "name": "dotnet-api",
              "inputs": [ { "name": "port" }, { "name": "dbPort" } ],
              "env": [ { "file": ".env.local", "set": { "PORT": "${sprig.port}" } } ],
              "compose": [ { "file": "docker-compose.yml", "overrides": [
                { "path": ["services","postgres","container_name"], "template": "x--${sprig.workspace}" } ] } ]
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
    public void Duplicate_input_names_are_flagged_case_insensitively()
    {
        var r = SprigConfigValidator.Validate(Base() with
        {
            Inputs = [new() { Name = "port" }, new() { Name = "PORT" }]
        });
        Assert.Contains(r.Issues, i => i.Message.Contains("duplicate"));
    }

    [Fact]
    public void Invalid_input_name_chars_are_flagged()
    {
        var r = SprigConfigValidator.Validate(Base() with { Inputs = [new() { Name = "has space" }] });
        Assert.Contains(r.Issues, i => i.Path == "inputs[0].name");
    }

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
            Inputs = [new() { Name = "port" }],
            Modules =
            [
                new ModuleDeclaration { Name = "web", Path = "apps/web",
                    Env = [new() { File = ".env.local", Set = new Dictionary<string, string> { ["PORT"] = "${sprig.port}" } }] },
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

public class SprigConfigMigrationTests
{
    const string FlatSchema2 = """
        {
          "schema": 2,
          "name": "dotnet-api",
          "inputs": [ { "name": "port", "example": "5000" } ],
          "env": [ { "file": ".env", "set": { "PORT": "${sprig.port}" } } ],
          "compose": [ { "file": "docker-compose.yml", "overrides": [
            { "path": ["services","db","container_name"], "template": "db--${sprig.workspace}" } ] } ],
          "setup": [ "dotnet restore" ]
        }
        """;

    [Fact]
    public void Schema2_flat_config_is_folded_into_a_single_default_module()
    {
        var c = SprigConfigMigration.Normalize(SprigConfigLoader.Parse(FlatSchema2));
        // Parse already migrates, so Normalize here is a no-op that also proves idempotence.

        Assert.Equal(3, c.Schema);
        Assert.Equal(["port"], c.Inputs.Select(i => i.Name));   // inputs stay at the repo level
        Assert.Null(c.Env);
        Assert.Null(c.Compose);
        Assert.Null(c.Setup);

        var m = Assert.Single(c.Modules);
        Assert.Equal("app", m.Name);
        Assert.Equal("", m.Path);
        Assert.Equal(".env", m.Env[0].File);
        Assert.Equal("docker-compose.yml", m.Compose[0].File);
        Assert.Equal(["dotnet restore"], m.Setup);
    }

    [Fact]
    public void Schema2_with_no_flat_surface_migrates_to_zero_modules()
    {
        var c = SprigConfigMigration.Normalize(new SprigRepoConfig { Schema = 2, Name = "x" });
        Assert.Equal(3, c.Schema);
        Assert.Empty(c.Modules);
    }

    [Fact]
    public void Schema3_config_is_left_untouched()
    {
        var original = new SprigRepoConfig
        {
            Schema = 3, Name = "x",
            Modules = [new ModuleDeclaration { Name = "web", Path = "apps/web", Setup = ["npm ci"] }],
        };
        var c = SprigConfigMigration.Normalize(original);
        Assert.Same(original, c);   // >= 3 short-circuits, never re-folds
    }

    [Fact]
    public void Normalize_is_idempotent()
    {
        var once = SprigConfigMigration.Normalize(SprigConfigLoader.Parse(FlatSchema2));
        var twice = SprigConfigMigration.Normalize(once);
        Assert.Equal(once.Modules.Count, twice.Modules.Count);
        Assert.Equal(3, twice.Schema);
    }

    [Fact]
    public void Unknown_top_level_keys_survive_migration()
    {
        var c = SprigConfigLoader.Parse("""{ "schema": 2, "name": "x", "bogus": 1 }""");
        Assert.Contains("bogus", c.Unknown.Keys);
    }
}
