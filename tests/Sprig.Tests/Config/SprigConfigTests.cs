using Sprig.Core.Config;

namespace Sprig.Tests.Config;

public class SprigConfigLoaderTests
{
    // A realistic, valid config modelled on the objective's examples.
    const string ValidJson = """
        {
          "schema": 1,
          "name": "dotnet-api",
          "ports": [
            { "name": "api", "description": "web host" },
            { "name": "postgres" }
          ],
          "env": [
            { "file": ".env.local", "set": { "PORT": "${sprig.ports.api}" } }
          ],
          "compose": {
            "file": "docker-compose.yml",
            "overrides": [
              { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
              { "path": ["services","postgres","ports","0"], "template": "${sprig.ports.postgres}:5432" }
            ]
          },
          "provides": { "baseUrl": "http://localhost:${sprig.ports.api}" }
        }
        """;

    [Fact]
    public void Parses_full_valid_config()
    {
        var c = SprigConfigLoader.Parse(ValidJson);

        Assert.Equal(1, c.Schema);
        Assert.Equal("dotnet-api", c.Name);
        Assert.Equal(["api", "postgres"], c.Ports.Select(p => p.Name));
        Assert.Equal("web host", c.Ports[0].Description);
        Assert.Single(c.Env);
        Assert.Equal(".env.local", c.Env[0].File);
        Assert.Equal("${sprig.ports.api}", c.Env[0].Set["PORT"]);
        Assert.NotNull(c.Compose);
        Assert.Equal(2, c.Compose!.Overrides.Count);
        Assert.Equal(["services", "postgres", "ports", "0"], c.Compose.Overrides[1].Path);
        Assert.Equal("http://localhost:${sprig.ports.api}", c.Provides["baseUrl"]);
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
        var c = SprigConfigLoader.Parse(SprigConfigLoaderTests_ValidJson);
        Assert.True(SprigConfigValidator.Validate(c).IsValid);
    }

    [Fact]
    public void Unsupported_schema_is_flagged()
    {
        var r = SprigConfigValidator.Validate(Base() with { Schema = 99 });
        Assert.Contains(r.Issues, i => i.Path == "schema");
    }

    [Fact]
    public void Empty_name_is_flagged()
    {
        var r = SprigConfigValidator.Validate(new SprigRepoConfig { Name = "" });
        Assert.Contains(r.Issues, i => i.Path == "name");
    }

    [Fact]
    public void Duplicate_port_names_are_flagged_case_insensitively()
    {
        var r = SprigConfigValidator.Validate(Base() with
        {
            Ports = [new() { Name = "api" }, new() { Name = "API" }]
        });
        Assert.Contains(r.Issues, i => i.Message.Contains("duplicate"));
    }

    [Fact]
    public void Invalid_port_name_chars_are_flagged()
    {
        var r = SprigConfigValidator.Validate(Base() with { Ports = [new() { Name = "has space" }] });
        Assert.Contains(r.Issues, i => i.Path == "ports[0].name");
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
            Compose = new ComposeConfig
            {
                File = "docker-compose.yml",
                Overrides = [new() { Path = [], Template = "" }]
            }
        });
        Assert.Contains(r.Issues, i => i.Path == "compose.overrides[0].path");
        Assert.Contains(r.Issues, i => i.Path == "compose.overrides[0].template");
    }

    [Fact]
    public void Unknown_top_level_key_is_flagged()
    {
        var c = SprigConfigLoader.Parse("""{ "schema": 1, "name": "x", "bogus": 1 }""");
        var r = SprigConfigValidator.Validate(c);
        Assert.Contains(r.Issues, i => i.Path == "bogus" && i.Message.Contains("unknown"));
    }

    // shared fixture reused from the loader tests
    const string SprigConfigLoaderTests_ValidJson = """
        {
          "schema": 1, "name": "dotnet-api",
          "ports": [ { "name": "api" }, { "name": "postgres" } ],
          "env": [ { "file": ".env.local", "set": { "PORT": "${sprig.ports.api}" } } ],
          "compose": { "file": "docker-compose.yml", "overrides": [
            { "path": ["services","postgres","container_name"], "template": "x--${sprig.workspace}" } ] },
          "provides": { "baseUrl": "http://localhost:${sprig.ports.api}" }
        }
        """;
}
