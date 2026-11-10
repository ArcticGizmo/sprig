using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Model;

namespace Sprig.Tests.Planning;

public class SecretValuesTests
{
    static Plan Shop()
    {
        var orchestration = Orchestration.Describe("storefront");
        var repo = Repo.Describe("shop-web");
        var web = repo.Node("web", ".").Http();
        web.EnvFile(".env.local", e => e
            .Set("PORT", web.Port)
            .Set("API_KEY", repo.Setting<Secret>("api-key"))
            .Set("SIGNED_URL", $"https://cdn.example.com/?sig={repo.Setting<Secret>("cdn-signature")}")
            .Set("API_URL", repo.Setting<Url>("api-url", "http://localhost:5100"))
            .Set("DB", "Host=localhost;Port=5432;Username=shop;Password=pw")
            .Set("FEATURE", "true")
            .Set("STRIPE_TOKEN", "sk_live_abc")
            .Set("DISPLAY_NAME", "Shop"));
        orchestration.Repo("shop-web");
        return Planner.Build(new PlanInput(orchestration.BuildOrchestration(), new Dictionary<string, RepoModel> { ["shop-web"] = repo.BuildRepo() },
            "1", new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9100 }, new HashSet<int>(),
            Values: new Dictionary<string, string> { ["api-key"] = "abc123", ["cdn-signature"] = "s1g" }));
    }

    static PlannedEnv Env(Plan plan, string key) => plan.Apps.Single().Env.Single(e => e.Key == key);

    [Theory]
    [InlineData("API_KEY", true)]
    [InlineData("Auth__ClientSecret", true)]
    [InlineData("ConnectionStrings:DbPassword", true)]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("aws-access-key", true)]
    [InlineData("ConnectionStrings__Db", false)]
    [InlineData("Urls", false)]
    [InlineData("KeyPrefix", false)]
    public void A_name_says_it_is_secret_whatever_its_separators_and_case(string key, bool secret)
        => Assert.Equal(secret, SecretValues.LooksSecret(key));

    [Fact]
    public void A_declared_secret_hides_every_value_built_from_it_and_a_secret_name_hides_its_own()
    {
        var plan = Shop();

        Assert.True(plan.IsSecretSetting("api-key"));
        Assert.Equal(SecretValues.Hidden, SecretValues.Show(plan, Env(plan, "API_KEY"), "abc123"));
        Assert.Equal(SecretValues.Hidden, SecretValues.Show(plan, Env(plan, "SIGNED_URL"), Env(plan, "SIGNED_URL").Value.Value!));
        Assert.Equal(SecretValues.Hidden, SecretValues.Show(plan, Env(plan, "STRIPE_TOKEN"), "sk_live_abc"));
        Assert.Equal("Host=localhost;Port=5432;Username=shop;Password=***", SecretValues.Show(plan, Env(plan, "DB"), Env(plan, "DB").Value.Value!));
        Assert.Equal("Shop", SecretValues.Show(plan, Env(plan, "DISPLAY_NAME"), "Shop"));
    }

    [Fact]
    public void Json_keeps_ports_addresses_and_flags_hides_secrets_and_leaves_the_rest_out()
    {
        var trimmed = SecretValues.Trimmed(Shop());

        string? Value(string key) => Env(trimmed, key).Value.Value;
        Assert.Matches(@"^90\d\d$", Value("PORT"));
        Assert.Equal("http://localhost:5100", Value("API_URL"));
        Assert.Equal("true", Value("FEATURE"));
        Assert.Equal("Host=localhost;Port=5432;Username=shop;Password=***", Value("DB"));
        Assert.Equal((SecretValues.Hidden, SecretValues.Hidden), (Value("API_KEY"), Env(trimmed, "API_KEY").Value.Template));
        Assert.Equal(SecretValues.Hidden, Value("SIGNED_URL"));
        Assert.Equal(SecretValues.Hidden, Value("STRIPE_TOKEN"));
        Assert.Equal((SecretValues.LeftOut, SecretValues.LeftOut), (Value("DISPLAY_NAME"), Env(trimmed, "DISPLAY_NAME").Value.Template));
        // A template that's only a reference holds nothing of its own.
        Assert.Equal("{setting.api-url}", Env(trimmed, "API_URL").Value.Template);
    }

    [Fact]
    public void Values_set_on_a_workspace_are_hidden_by_their_settings_type_or_name()
    {
        var plan = Shop();

        var shown = SecretValues.Trimmed(new Dictionary<string, string> { ["api-key"] = "abc123", ["api-url"] = "http://x:1", ["db-password"] = "pw", ["name"] = "Shop" }, plan)!;

        Assert.Equal(SecretValues.Hidden, shown["api-key"]);
        Assert.Equal("http://x:1", shown["api-url"]);
        Assert.Equal(SecretValues.Hidden, shown["db-password"]);
        Assert.Equal(SecretValues.LeftOut, shown["name"]);
    }

    [Fact]
    public void A_secret_looking_value_no_secret_setting_gives_is_warned_about_with_where_it_comes_from()
    {
        var warning = Assert.Single(Shop().Problems);

        Assert.Equal(ProblemSeverity.Warning, warning.Severity);
        Assert.StartsWith("STRIPE_TOKEN looks secret but comes from a value written in the definition", warning.Message);
    }
}
