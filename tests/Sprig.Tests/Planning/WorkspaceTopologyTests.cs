using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Model;

namespace Sprig.Tests.Planning;

public sealed class WorkspaceTopologyTests
{
    static WorkspaceTopology Shop()
    {
        var services = Yaml.Repo("""
            schema: 1
            repo: shop-services

            compose:
              docker-compose.yml:
                services:
                  search:
                    ports: { http: { container: 7700 } }

            containers:
              pg: { preset: postgres }
              pg-admin:
                image: dpage/pgadmin4
                ports: { http: { container: 80 } }
                env:
                  DB: Host=localhost;Port=${pg.port};Database=catalog;Username=sprig;Password=sprig

            inputs:
              mail-smtp: { type: url }
              payments-url:
                type: url
                default: https://payments.example.com
              feature:
                type: text
                default: on
              shared-db: { type: connection-string }

            apps:
              catalog-api:
                run: dotnet
                ports: { http: any }
                wait-for: [pg, migrate]
              orders-api:
                run: dotnet
                path: src/Orders
                ports: { http: any }
              migrate:
                run: dotnet
                path: tools/Migrate
                once: true
                wait-for: [pg]

            files:
              appsettings.Local.json:
                read-by: [catalog-api]
                values:
                  Db: Host=localhost;Port=${pg.port};Database=catalog;Username=sprig;Password=sprig
                  Urls: ${catalog-api.url}
              src/Orders/appsettings.Local.json:
                read-by: [orders-api]
                values:
                  Smtp: ${inputs.mail-smtp}
                  Search: http://localhost:${search.port}
                  Payments: ${inputs.payments-url}
                  Flag: ${inputs.feature}
                  Shared: ${inputs.shared-db}

            outputs:
              catalog-url:
                type: url
                value: ${catalog-api.url}
            """);
        var web = Yaml.Repo("""
            schema: 1
            repo: shop-web

            inputs:
              catalog-url: { type: url }

            apps:
              web:
                run: node
                ports: { http: any }

            files:
              .env.local:
                read-by: [web]
                values:
                  API: ${inputs.catalog-url}
            """);
        var orchestration = Yaml.Orchestration("""
            schema: 1
            orchestration: storefront

            repos:
              shop-services: { path: shop-services }
              shop-web: { path: shop-web }

            containers:
              mail: { preset: mailpit }

            inputs:
              mail-smtp: ${mail.smtp-url}
            """);
        var repos = new Dictionary<string, RepoModel> { ["shop-services"] = services, ["shop-web"] = web };
        var plan = Planner.Build(new PlanInput(orchestration, repos, "feat-a", new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9100 }, new HashSet<int>()));
        return WorkspaceTopology.Of(plan);
    }

    static TopologyLink Link(WorkspaceTopology t, string from, string to) => t.Links.Single(l => l.From == from && l.To == to);

    [Fact]
    public void An_app_is_linked_to_its_own_repo_s_database_and_to_what_it_waits_for()
    {
        var t = Shop();

        var db = Link(t, "shop-services/catalog-api", "shop-services/pg");
        Assert.Equal(["Db"], db.Values);
        Assert.True(db.Waits);
        var job = Link(t, "shop-services/catalog-api", "shop-services/migrate");
        Assert.True(job.OrderOnly);
        Assert.DoesNotContain(t.Links, l => l.From == l.To);
    }

    [Fact]
    public void Values_across_repos_and_from_the_orchestration_are_linked_by_their_setting()
    {
        var t = Shop();

        Assert.Equal(["catalog-url"], Link(t, "shop-web/web", "shop-services/catalog-api").Values);
        Assert.Equal(["mail-smtp"], Link(t, "shop-services/orders-api", "storefront/mail").Values);
        Assert.Equal(["Search"], Link(t, "shop-services/orders-api", "shop-services/search").Values);
        Assert.Equal(["DB"], Link(t, "shop-services/pg-admin", "shop-services/pg").Values);
    }

    [Fact]
    public void Every_app_job_container_and_compose_service_is_a_node_of_its_kind()
    {
        var kinds = Shop().Nodes.ToDictionary(n => n.Key, n => n.Kind);

        Assert.Equal(TopologyKind.App, kinds["shop-services/catalog-api"]);
        Assert.Equal(TopologyKind.Job, kinds["shop-services/migrate"]);
        Assert.Equal(TopologyKind.Container, kinds["shop-services/pg"]);
        Assert.Equal(TopologyKind.Container, kinds["storefront/mail"]);
        Assert.Equal(TopologyKind.ComposeService, kinds["shop-services/search"]);
    }

    [Fact]
    public void An_address_from_outside_gets_a_node_a_flag_doesn_t_and_a_setting_nothing_gives_is_missing()
    {
        var t = Shop();

        var payments = t.Nodes.Single(n => n.Key == "outside/payments-url");
        Assert.Equal(TopologyKind.Outside, payments.Kind);
        Assert.Equal("https://payments.example.com", payments.Detail);
        Assert.DoesNotContain(t.Nodes, n => n.Name == "feature");
        Assert.Equal(TopologyKind.Missing, t.Nodes.Single(n => n.Key == "outside/shared-db").Kind);
        Assert.Equal(["shared-db"], Link(t, "shop-services/orders-api", "outside/shared-db").Values);
    }

    [Theory]
    [InlineData("postgres://u:p@db.internal:5432/app", true)]
    [InlineData("Host=db.internal;Port=5432;Password=x", true)]
    [InlineData("Server=.;Database=app", true)]
    [InlineData("cache.internal:6379", true)]
    [InlineData("on", false)]
    [InlineData("Development", false)]
    public void Only_values_that_name_somewhere_to_connect_to_look_like_addresses(string value, bool address)
        => Assert.Equal(address, WorkspaceTopology.LooksLikeAddress(value));
}
