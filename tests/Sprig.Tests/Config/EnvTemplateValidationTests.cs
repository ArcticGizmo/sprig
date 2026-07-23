using Sprig.Core.Config;

namespace Sprig.Tests.Config;

/// <summary>The validator accepts template paths and flags blank ones.</summary>
public class EnvTemplateValidationTests
{
    static SprigRepoConfig WithTemplates(params string[] templates) => new()
    {
        Name = "r",
        Env =
        [
            new EnvOverride
            {
                File = ".env.local",
                Templates = templates,
                Set = new Dictionary<string, string> { ["K"] = "v" },
            },
        ],
    };

    [Fact]
    public void Valid_template_paths_pass()
        => Assert.True(SprigConfigValidator.Validate(WithTemplates(".env.template", ".env.example")).IsValid);

    [Fact]
    public void A_blank_template_path_is_flagged()
    {
        var result = SprigConfigValidator.Validate(WithTemplates(".env.template", "  "));

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Path == "env[0].templates[1]");
    }

    [Fact]
    public void No_templates_is_fine()
        => Assert.True(SprigConfigValidator.Validate(new SprigRepoConfig
        {
            Name = "r",
            Env = [new EnvOverride { File = ".env", Set = new Dictionary<string, string> { ["K"] = "v" } }],
        }).IsValid);
}
