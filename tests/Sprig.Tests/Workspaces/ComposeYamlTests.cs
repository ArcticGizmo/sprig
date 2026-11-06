using Sprig.Engine.Compose;
using Sprig.Engine.Planning;
using Sprig.Model;

namespace Sprig.Tests.Workspaces;

public class ComposeYamlTests
{
    const string RepoCompose = """
        services:
          postgres:
            image: postgres:17
            container_name: shop-pg
            ports:
              - "${PG_PORT:-5432}:5432"
            volumes:
              - pgdata:/var/lib/postgresql/data
          api:
            build: ./api
            profiles: ["full"]
            depends_on:
              - postgres
              - mailpit
          mailpit:
            image: axllent/mailpit:v1.27
            ports:
              - "1025:1025"
        volumes:
          pgdata:
        """;

    [Fact]
    public void Inspect_lists_services_and_the_ones_with_a_fixed_container_name()
    {
        var info = ComposeYaml.Inspect(RepoCompose);

        Assert.Equal(["postgres", "api", "mailpit"], info.Services);
        Assert.Equal(["postgres"], info.FixedNames);
        Assert.Equal(["api"], info.Profiled);
    }

    [Fact]
    public void A_workspace_copy_keeps_named_services_on_sprig_ports_and_drops_the_rest()
    {
        var compose = new PlannedCompose("shop-services", "../shop-services", "docker-compose.yml",
            [new PlannedOverride("services.postgres.environment.POSTGRES_DB", new PlannedValue("x", "catalog_feat", []))],
            [
                new PlannedComposeService("postgres", [new PlannedMapping("tcp", 8001, 5432)], "pg_isready -h 127.0.0.1"),
                new PlannedComposeService("api", []),
            ]);

        var yaml = ComposeYaml.ForWorkspace(RepoCompose, compose);
        var again = ComposeYaml.Inspect(yaml);

        Assert.Equal(["postgres", "api"], again.Services);
        Assert.Empty(again.FixedNames);
        Assert.Contains("\"127.0.0.1:8001:5432\"", yaml);
        Assert.DoesNotContain("PG_PORT", yaml);
        Assert.DoesNotContain("profiles", yaml);
        Assert.DoesNotContain("mailpit", yaml);
        Assert.Contains("pgdata:/var/lib/postgresql/data", yaml);
        Assert.Contains("POSTGRES_DB: \"catalog_feat\"", yaml);
        Assert.Contains("\"pg_isready -h 127.0.0.1\"", yaml);
        Assert.DoesNotContain("...", yaml);
    }

    [Fact]
    public void An_override_on_a_path_that_does_not_exist_says_where_it_stopped()
    {
        var compose = new PlannedCompose("r", "../r", "c.yml",
            [new PlannedOverride("services.redis.image", new PlannedValue("x", "redis:8", []))],
            [new PlannedComposeService("postgres", [])]);

        var e = Assert.Throws<ComposeException>(() => ComposeYaml.ForWorkspace(RepoCompose, compose));
        Assert.Contains("at 'redis'", e.Message);
    }

    [Fact]
    public void Code_containers_get_owner_scoped_names_volumes_health_and_escaped_values()
    {
        var at = new SourceLocation("x", 1);
        var pg = new PlannedContainer("shop-services", "pg", "postgres:17", ["8001:5432"],
            [new PlannedEnv("POSTGRES_PASSWORD", new PlannedValue("p$ss", "p$ss", []), null, at)],
            ["data → /var/lib/postgresql/data"],
            [new PlannedMapping("tcp", 8001, 5432)],
            [new VolumeModel("data", "/var/lib/postgresql/data")],
            "pg_isready -h 127.0.0.1");

        var yaml = ComposeYaml.ForContainers([pg]);

        Assert.Contains("shop-services-pg:", yaml);
        Assert.Contains("\"shop-services-pg-data:/var/lib/postgresql/data\"", yaml);
        Assert.Contains("POSTGRES_PASSWORD: \"p$$ss\"", yaml);
        Assert.Contains("\"pg_isready -h 127.0.0.1\"", yaml);
        Assert.Equal(["shop-services-pg"], ComposeYaml.Inspect(yaml).Services);
    }

    [Theory]
    [InlineData(new[] { "sprig", "Storefront", "feat/X" }, "sprig-storefront-feat-x")]
    [InlineData(new[] { "sprig", "store front", "a.b" }, "sprig-store-front-a-b")]
    public void Project_names_are_what_compose_accepts(string[] parts, string expected)
        => Assert.Equal(expected, ComposeYaml.ProjectName(parts));
}
