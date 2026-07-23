using Sprig.Core.Config;

namespace Sprig.Tests.Config;

/// <summary>The validator accepts a well-formed <c>allowedPorts</c> and flags a malformed one.</summary>
public class AllowedPortsValidationTests
{
    [Fact]
    public void Valid_allowedPorts_passes()
    {
        var cfg = new SprigRepoConfig
        {
            Name = "r",
            Inputs = [new InputDeclaration { Name = "p", AllowedPorts = "8100-8103" }],
        };

        Assert.True(SprigConfigValidator.Validate(cfg).IsValid);
    }

    [Fact]
    public void Malformed_allowedPorts_is_flagged_at_the_input()
    {
        var cfg = new SprigRepoConfig
        {
            Name = "r",
            Inputs = [new InputDeclaration { Name = "p", AllowedPorts = "nope" }],
        };

        var result = SprigConfigValidator.Validate(cfg);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Path == "inputs[0].allowedPorts");
    }

    [Fact]
    public void Blank_allowedPorts_means_unrestricted()
    {
        var cfg = new SprigRepoConfig
        {
            Name = "r",
            Inputs = [new InputDeclaration { Name = "p", AllowedPorts = "" }],
        };

        Assert.True(SprigConfigValidator.Validate(cfg).IsValid);
    }
}
