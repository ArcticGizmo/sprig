using Sprig.Core.Config;
using Sprig.Core.Maps;
using Sprig.Core.Stacks;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Maps;

/// <summary>The map inspection behind the Maps wiring view and <c>sprig map show/check</c>.</summary>
public class MapInspectorTests
{
    static ResolvedRepo Repo(string name, string json, string? root = null) =>
        new(name, root ?? $"/repos/{name}", SprigConfigLoader.Parse(json));

    static MapDefinition Map(params string[] repos) => new() { Name = "m", Repos = repos.Select(MapRepo.Local).ToList() };

    static string Provider(string name, string cap) => $$"""
        { "schema": 1, "name": "{{name}}",
          "provides": [ { "capability": "{{cap}}", "ports": { "port": true },
                          "shapes": { "url": "http://localhost:${sprig.{{cap}}.port}" } } ],
          "env": [ { "file": ".env", "set": { "PORT": "${sprig.{{cap}}.port}" } } ] }
        """;

    static string Consumer(string name, string need) => $$"""
        { "schema": 1, "name": "{{name}}",
          "needs": [ { "capability": "{{need}}" } ],
          "env": [ { "file": ".env", "set": { "API_URL": "${sprig.{{need}}.url}" } } ] }
        """;

    [Fact]
    public void A_service_two_modules_rely_on_is_a_hub_with_both_consumers()
    {
        var result = MapInspector.Inspect(Map("auth", "billing", "catalog"),
            [Repo("auth", Provider("auth", "auth-api")), Repo("billing", Consumer("billing", "auth-api")), Repo("catalog", Consumer("catalog", "auth-api"))]);

        var hub = Assert.Single(result.Hubs);
        Assert.Equal(("auth", 2), (hub.Repo, hub.ReliedOnBy));
        var provide = hub.Provides.Single();
        Assert.Equal(ProvideUse.Shared, provide.Use);
        Assert.Equal(["billing", "catalog"], provide.Consumers.Select(c => c.Repo).Order());
        Assert.True(result.IsReady);

        var need = result.FindModule("billing", "app")!.Needs.Single();
        Assert.Equal(NeedState.Wired, need.State);
        Assert.Equal(new ProviderRef("auth", "app", "auth-api"), need.Providers.Single());
        Assert.Equal(new TemplateUse(".env", "API_URL"), need.Uses.Single());
    }

    [Fact]
    public void A_provide_only_its_own_repo_references_is_own_use_and_an_unreferenced_one_is_unused()
    {
        var result = MapInspector.Inspect(Map("api"), [Repo("api", """
            { "schema": 1, "name": "api",
              "provides": [
                { "capability": "api", "ports": { "port": true } },
                { "capability": "spare", "ports": { "port": true } } ],
              "env": [ { "file": ".env", "set": { "PORT": "${sprig.api.port}" } } ] }
            """)]);

        var provides = result.FindModule("api", "app")!.Provides;
        Assert.Equal(ProvideUse.OwnUse, provides.Single(p => p.Capability == "api").Use);
        Assert.Equal(new TemplateUse(".env", "PORT"), provides.Single(p => p.Capability == "api").OwnUses.Single());
        Assert.Equal(ProvideUse.Unused, provides.Single(p => p.Capability == "spare").Use);
    }

    [Fact]
    public void A_monorepo_need_on_a_sibling_is_not_counted_as_the_siblings_own_use()
    {
        var result = MapInspector.Inspect(Map("shop"), [Repo("shop", """
            { "schema": 1, "name": "shop", "modules": [
                { "name": "api", "path": "services/api",
                  "provides": [ { "capability": "shop-api", "ports": { "port": true } } ],
                  "env": [ { "file": ".env", "set": { "PORT": "${sprig.shop-api.port}" } } ] },
                { "name": "web", "path": "apps/web",
                  "needs": [ { "capability": "shop-api" } ],
                  "env": [ { "file": ".env.local", "set": { "API_PORT": "${sprig.shop-api.port}" } } ] } ] }
            """)]);

        var provide = result.FindModule("shop", "api")!.Provides.Single();
        Assert.Equal(new TemplateUse("services/api/.env", "PORT"), provide.OwnUses.Single());
        Assert.Equal(new ModuleRef("shop", "web"), provide.Consumers.Single());
        Assert.Equal(new TemplateUse("apps/web/.env.local", "API_PORT"), result.FindModule("shop", "web")!.Needs.Single().Uses.Single());
        Assert.Equal(1, result.FindModule("shop", "api")!.ReliedOnBy);   // a sibling module counts like any other
    }

    [Fact]
    public void A_gap_whose_name_is_one_edit_from_a_provide_suggests_it()
    {
        var result = MapInspector.Inspect(Map("accounts", "admin"),
            [Repo("accounts", Provider("accounts", "accounts-api")), Repo("admin", Consumer("admin", "account-api"))]);

        var gap = Assert.Single(result.Gaps);
        Assert.Equal(new ProviderRef("accounts", "app", "accounts-api"), gap.Similar);
        Assert.False(result.IsReady);
    }

    [Theory]
    [InlineData("home-spa", "home-web")]   // a different service, not a typo
    [InlineData("auth-db", "auth-api")]
    [InlineData("db", "dbs")]              // names this short are all close to each other
    public void Names_that_merely_share_a_prefix_are_not_suggested(string need, string provided)
    {
        var result = MapInspector.Inspect(Map("p", "c"), [Repo("p", Provider("p", provided)), Repo("c", Consumer("c", need))]);
        Assert.Null(Assert.Single(result.Gaps).Similar);
    }

    [Fact]
    public void Case_and_underscores_alone_count_as_the_same_name()
    {
        var result = MapInspector.Inspect(Map("p", "c"), [Repo("p", Provider("p", "orders-api")), Repo("c", Consumer("c", "orders_api"))]);
        Assert.Equal("orders-api", Assert.Single(result.Gaps).Similar!.Capability);
    }

    [Fact]
    public void A_map_default_marks_the_need_and_carries_its_values()
    {
        var map = Map("billing") with
        {
            Defaults = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>
            {
                ["billing"] = new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["payments"] = new Dictionary<string, string> { ["url"] = "https://pay.sandbox.test" },
                },
            },
        };
        var result = MapInspector.Inspect(map, [Repo("billing", Consumer("billing", "payments"))]);

        var need = Assert.Single(result.Defaulted);
        Assert.Equal("https://pay.sandbox.test", need.DefaultValues!["url"]);
        Assert.True(result.IsReady);
    }

    [Fact]
    public void A_provide_of_fixed_shapes_and_no_port_is_a_constant()
    {
        var result = MapInspector.Inspect(Map("auth"), [Repo("auth", """
            { "schema": 1, "name": "auth", "provides": [
                { "capability": "auth-realm", "shapes": { "realm": "storefront-dev" } },
                { "capability": "auth-api", "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.auth-api.port}" } },
                { "capability": "queue", "shapes": { "name": "jobs-${sprig.workspace}" } } ] }
            """)]);

        var provides = result.FindModule("auth", "app")!.Provides;
        Assert.Equal("storefront-dev", provides.Single(p => p.Capability == "auth-realm").Constants!["realm"]);
        Assert.Null(provides.Single(p => p.Capability == "auth-api").Constants);
        Assert.Null(provides.Single(p => p.Capability == "queue").Constants);   // differs per workspace
    }

    [Fact]
    public void Two_providers_are_ambiguous_and_both_are_listed_with_their_modules()
    {
        var result = MapInspector.Inspect(Map("a", "b", "web"),
            [Repo("a", Provider("a", "cache")), Repo("b", Provider("b", "cache")), Repo("web", Consumer("web", "cache"))]);

        var need = Assert.Single(result.Ambiguities);
        Assert.Equal([new ProviderRef("a", "app", "cache"), new ProviderRef("b", "app", "cache")], need.Providers);
        Assert.False(result.IsReady);
    }

    [Fact]
    public void A_gap_gets_a_value_hint_from_the_env_template_around_its_reference()
    {
        var root = Path.Combine(Path.GetTempPath(), "sprig-hint-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "apps", "web"));
            File.WriteAllText(Path.Combine(root, "apps", "web", ".env.template"), "VITE_HELP_URL=http://localhost:4000/\nOTHER=1\n");
            var result = MapInspector.Inspect(Map("shop"), [Repo("shop", """
                { "schema": 1, "name": "shop", "modules": [
                    { "name": "web", "path": "apps/web", "needs": [ { "capability": "help-site" } ],
                      "env": [ { "file": ".env.local", "templates": [ ".env.template" ],
                                 "set": { "VITE_HELP_URL": "${sprig.help-site.url}/" } } ] } ] }
                """, root)]);

            var hint = Assert.Single(Assert.Single(result.Gaps).Hints);
            Assert.Equal(("url", "http://localhost:4000"), (hint.Output, hint.Value));
            Assert.Contains("VITE_HELP_URL", hint.Source);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Literal_local_addresses_and_ports_are_reported_as_fixed_values()
    {
        var result = MapInspector.Inspect(Map("shop"), [Repo("shop", """
            { "schema": 1, "name": "shop",
              "provides": [ { "capability": "shop-api", "ports": { "port": true } } ],
              "env": [ { "file": ".env", "set": {
                  "MAIL_HOST": "localhost:1025",
                  "RABBITMQ_PORT": "8512",
                  "DB_HOST": "localhost",
                  "API_PORT": "${sprig.shop-api.port}",
                  "TIMEOUT": "8512" } } ],
              "compose": [ { "file": "docker-compose.yml", "overrides": [
                  { "path": [ "services", "mq", "ports", "0" ], "template": "8512:5672" },
                  { "path": [ "services", "api", "ports", "0" ], "template": "${sprig.shop-api.port}:80" } ] } ] }
            """)]);

        Assert.Equal(
            [("MAIL_HOST", FixedKind.Address), ("RABBITMQ_PORT", FixedKind.Port), ("services.mq.ports.0", FixedKind.Port)],
            result.FixedValues.Select(f => (f.Key, f.Kind)));
    }

    [Theory]
    [InlineData("amqp://admin:secret@localhost:5672", "amqp://admin:•••@localhost:5672")]
    [InlineData("Host=localhost:5432;Password=secret;Database=app", "Host=localhost:5432;Password=•••;Database=app")]
    [InlineData("localhost:1025", "localhost:1025")]
    public void A_fixed_values_display_form_masks_passwords(string value, string shown)
        => Assert.Equal(shown, new FixedValue("r", "m", ".env", "K", value, FixedKind.Address).DisplayValue);

    [Fact]
    public void Unregistered_and_unreadable_repos_are_problems_not_exceptions()
    {
        using var store = new TempStore();
        var registry = new RepoRegistryStore(store.Paths);
        var maps = new MapStore(store.Paths, registry);

        var good = Path.Combine(store.Root, "repos", "good");
        Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(good, ".sprig.json"), Provider("good", "api"));
        registry.Add(good);

        var broken = Path.Combine(store.Root, "repos", "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, ".sprig.json"), """{ "schema": 1, "name": "broken" }""");
        registry.Add(broken);
        File.WriteAllText(Path.Combine(broken, ".sprig.json"), "{ not json");

        var map = new MapDefinition
        {
            Name = "m",
            Repos = [MapRepo.Local("good"), MapRepo.Local("broken"), MapRepo.Local("ghost"),
                     new MapRepo { Name = "remote", Repo = "https://example.test/remote.git" }],
        };
        var result = new MapInspector(registry, maps).Inspect(map);

        Assert.Equal(["good"], result.Repos.Select(r => r.Name));
        Assert.Equal(["broken", "ghost", "remote"], result.Problems.Select(p => p.Repo));
        Assert.Contains("invalid JSON", result.Problems[0].Message);
        Assert.Contains("no git URL", result.Problems[1].Message);
        Assert.Contains("will clone it", result.Problems[2].Message);
        Assert.False(result.IsReady);
    }
}
