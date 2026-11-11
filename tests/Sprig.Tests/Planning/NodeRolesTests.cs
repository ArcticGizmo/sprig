using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Model;

namespace Sprig.Tests.Planning;

public sealed class NodeRolesTests
{
    [Theory]
    [InlineData("postgres", "postgres:17", NodeRoles.Database)]
    [InlineData("pg", "postgres:17", NodeRoles.Database)]
    [InlineData("safety-db", null, NodeRoles.Database)]
    [InlineData("sql", "mcr.microsoft.com/mssql/server:2022-latest", NodeRoles.Database)]
    [InlineData("safety-redis", null, NodeRoles.Cache)]
    [InlineData("bus", "rabbitmq:4-management", NodeRoles.Queue)]
    [InlineData("kafka", null, NodeRoles.Queue)]
    [InlineData("safety-meilisearch", null, NodeRoles.Search)]
    [InlineData("mail", "axllent/mailpit:v1.27", NodeRoles.Mail)]
    [InlineData("blobs", "mcr.microsoft.com/azure-storage/azurite", NodeRoles.Storage)]
    [InlineData("auth", "quay.io/keycloak/keycloak:26", NodeRoles.Identity)]
    [InlineData("pgadmin", "dpage/pgadmin4", null)]
    public void Infra_is_known_by_its_image_or_else_its_name(string name, string? image, string? role)
        => Assert.Equal(role, NodeRoles.OfInfra(null, name, image));

    [Theory]
    [InlineData("rabbitmq-url", "amqp://u:p@mq.internal:5672", NodeRoles.Queue)]
    [InlineData("identity-url", "https://login.example.com", NodeRoles.Identity)]
    [InlineData("events", "amqp://mq.internal", NodeRoles.Queue)]
    [InlineData("shared", "Host=db.internal;Password=x", NodeRoles.Database)]
    [InlineData("cache", "rediss://cache.internal:6380", NodeRoles.Cache)]
    [InlineData("bus", "Endpoint=sb://ns.servicebus.windows.net/", NodeRoles.Queue)]
    [InlineData("home-url", "https://home.example.com", null)]
    public void An_outside_value_is_known_by_its_name_or_else_its_value(string name, string value, string? role)
        => Assert.Equal(role, NodeRoles.OfInfra(null, name, value: value));

    [Fact]
    public void What_a_definition_says_wins_and_a_role_it_doesn_t_know_is_ignored()
    {
        Assert.Equal(NodeRoles.Queue, NodeRoles.OfInfra("queue", "safety-db"));
        Assert.Equal(NodeRoles.Database, NodeRoles.OfInfra("teapot", "safety-db"));
    }

    [Fact]
    public void Presets_say_what_they_are()
    {
        var repo = Yaml.Repo("""
            schema: 1
            repo: shop

            containers:
              pg: { preset: postgres }
              cache: { preset: redis }
              bus: { preset: rabbitmq }
              mail: { preset: mailpit }
            """);

        Assert.Equal(["database", "cache", "queue", "mail"], repo.Containers.Select(c => c.Role));
    }

    [Theory]
    [InlineData("spa-api", NodeRoles.Api)]
    [InlineData("cdc-worker", NodeRoles.Worker)]
    [InlineData("client", NodeRoles.Web)]
    [InlineData("management", null)]
    public void An_app_is_known_by_its_name(string name, string? role) => Assert.Equal(role, NodeRoles.OfApp(null, name));

    [Fact]
    public void Roles_given_in_a_definition_reach_the_graph_and_one_on_a_setting_makes_an_outside_value_infra()
    {
        var repo = Yaml.Repo("""
            schema: 1
            repo: safety

            compose:
              docker-compose.yml:
                services:
                  store:
                    ports: { tcp: { container: 9000 } }
                    role: storage

            containers:
              ui:
                image: nginx:1
                role: proxy

            inputs:
              events-url:
                type: url
                default: https://events.example.com
                role: queue
              feature-flags:
                type: text
                default: on
                role: storage

            apps:
              ingest:
                run: dotnet
                role: worker

            files:
              appsettings.Local.json:
                read-by: [ingest]
                values:
                  Events: ${inputs.events-url}
                  Flags: ${inputs.feature-flags}
            """);
        var orchestration = Yaml.Orchestration("""
            schema: 1
            orchestration: safety

            repos:
              safety: { path: safety }
            """, "safety.orchestration.yaml");
        var plan = Planner.Build(new PlanInput(orchestration, new Dictionary<string, RepoModel> { ["safety"] = repo },
            "1", new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9100 }, new HashSet<int>()));

        var roles = WorkspaceTopology.Of(plan).Nodes.ToDictionary(n => n.Key, n => n.Role);

        Assert.Equal(NodeRoles.Worker, roles["safety/ingest"]);
        Assert.Equal(NodeRoles.Proxy, roles["safety/ui"]);
        Assert.Equal(NodeRoles.Storage, roles["safety/store"]);
        // Named and valued like any web page, it's a queue because the repo says so.
        Assert.Equal(NodeRoles.Queue, roles["outside/events-url"]);
        // A plain value told it's infra is drawn as infra, rather than left out as a flag would be.
        Assert.Equal(NodeRoles.Storage, roles["outside/feature-flags"]);
    }
}
