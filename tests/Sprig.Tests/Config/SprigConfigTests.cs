using Sprig.Core.Config;

namespace Sprig.Tests.Config;

public class SprigConfigLoaderTests
{
    // A realistic, valid config: repo declares the inputs it needs; env/compose reference them.
    const string ValidJson = """
        {
          "schema": 1,
          "name": "dotnet-api",
          "inputs": [
            { "name": "port", "example": "5000", "description": "web host" },
            { "name": "dbPort", "example": "5432" }
          ],
          "env": [
            { "file": ".env.local", "set": { "PORT": "${sprig.port}" } }
          ],
          "compose": {
            "file": "docker-compose.yml",
            "overrides": [
              { "path": ["services","postgres","container_name"], "template": "librarydb_postgres--${sprig.workspace}" },
              { "path": ["services","postgres","ports","0"], "template": "${sprig.dbPort}:5432" }
            ]
          }
        }
        """;

    [Fact]
    public void Parses_full_valid_config()
    {
        var c = SprigConfigLoader.Parse(ValidJson);

        Assert.Equal(1, c.Schema);
        Assert.Equal("dotnet-api", c.Name);
        Assert.Equal(["port", "dbPort"], c.Inputs.Select(i => i.Name));
        Assert.Equal("5000", c.Inputs[0].Example);
        Assert.Single(c.Env);
        Assert.Equal(".env.local", c.Env[0].File);
        Assert.Equal("${sprig.port}", c.Env[0].Set["PORT"]);
        Assert.NotNull(c.Compose);
        Assert.Equal(2, c.Compose!.Overrides.Count);
        Assert.Equal(["services", "postgres", "ports", "0"], c.Compose.Overrides[1].Path);
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
              "inputs": [ { "name": "port" }, { "name": "dbPort" } ],
              "env": [ { "file": ".env.local", "set": { "PORT": "${sprig.port}" } } ],
              "compose": { "file": "docker-compose.yml", "overrides": [
                { "path": ["services","postgres","container_name"], "template": "x--${sprig.workspace}" } ] }
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
            Compose = new ComposeConfig { File = "docker-compose.yml", Overrides = [new() { Path = [], Template = "" }] }
        });
        Assert.Contains(r.Issues, i => i.Path == "compose.overrides[0].path");
        Assert.Contains(r.Issues, i => i.Path == "compose.overrides[0].template");
    }

    [Fact]
    public void Unknown_top_level_key_is_flagged()
    {
        var c = SprigConfigLoader.Parse("""{ "schema": 1, "name": "x", "bogus": 1 }""");
        Assert.Contains(SprigConfigValidator.Validate(c).Issues, i => i.Path == "bogus" && i.Message.Contains("unknown"));
    }
}
