using Sprig.Core.Config;

namespace Sprig.Tests.Config;

/// <summary>The validator accepts free-form setup commands and flags blank ones; the loader round-trips them.</summary>
public class SetupValidationTests
{
    static SprigRepoConfig WithSetup(params string[] setup) => new() { Name = "r", Setup = setup };

    [Fact]
    public void Valid_setup_commands_pass()
        => Assert.True(SprigConfigValidator.Validate(WithSetup("npm ci", "dotnet restore")).IsValid);

    [Fact]
    public void No_setup_is_fine()
        => Assert.True(SprigConfigValidator.Validate(new SprigRepoConfig { Name = "r" }).IsValid);

    [Fact]
    public void A_blank_setup_command_is_flagged()
    {
        var result = SprigConfigValidator.Validate(WithSetup("npm ci", "   "));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Path == "setup[1]");
    }

    [Fact]
    public void Setup_round_trips_through_the_loader()
    {
        var json = """
            { "schema": 2, "name": "r", "setup": ["npm ci", "dotnet restore"] }
            """;

        var config = SprigConfigLoader.Parse(json);

        Assert.Equal(new[] { "npm ci", "dotnet restore" }, config.Setup);
        Assert.True(SprigConfigValidator.Validate(config).IsValid);
    }
}
