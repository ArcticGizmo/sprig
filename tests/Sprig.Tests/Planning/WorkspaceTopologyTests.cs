using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Model;

namespace Sprig.Tests.Planning;

public sealed class WorkspaceTopologyTests
{
    readonly Dictionary<string, RepoModel> _repos = [];
    readonly OrchestrationBuilder _orchestration = Orchestration.Describe("storefront");

    void Add(string path, Action<RepoBuilder> describe)
    {
        var repo = Repo.Describe(path);
        describe(repo);
        _repos[path] = repo.BuildRepo();
        _orchestration.Repo(path);
    }

    WorkspaceTopology Build()
    {
        var model = _orchestration.BuildOrchestration();
        var plan = Planner.Build(new PlanInput(model, _repos, "feat-a", new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9100 }, new HashSet<int>()));
        return WorkspaceTopology.Of(plan);
    }

    void Shop()
    {
        Add("shop-services", repo =>
        {
            var pg = repo.Postgres("pg");
            var search = repo.ComposeFile("docker-compose.yml").Service("search").Endpoint("http", 7700, scheme: "http");
            repo.Container("pg-admin", "dpage/pgadmin4").Endpoint("http", 80, scheme: "http").Env("DB", pg.Database("catalog"));
            var migrate = repo.DotNetJob("migrate", "tools/Migrate").WaitFor(pg);
            var catalog = repo.DotNet("catalog-api", ".").Http().WaitFor(pg, migrate);
            catalog.AppSettings("appsettings.Local.json", s => s.Set("Db", pg.Database("catalog")).Set("Urls", catalog.Url));
            repo.DotNet("orders-api", "src/Orders").Http().AppSettings("appsettings.Local.json", s => s
                .Set("Smtp", repo.Setting<Url>("mail-smtp"))
                .Set("Search", $"http://localhost:{search.Port}")
                .Set("Payments", repo.Setting<Url>("payments-url", "https://payments.example.com"))
                .Set("Flag", repo.Setting<Text>("feature", "on"))
                .Set("Shared", repo.Setting<ConnectionString>("shared-db")));
            repo.Export<Url>("catalog-url", catalog.Url);
        });
        Add("shop-web", repo => repo.Node("web", ".").Http().EnvFile(".env.local", e => e.Set("API", repo.Setting<Url>("catalog-url"))));
        var mail = _orchestration.Mailpit("mail");
        _orchestration.Bind("mail-smtp", mail.SmtpUrl);
    }

    static TopologyLink Link(WorkspaceTopology t, string from, string to) => t.Links.Single(l => l.From == from && l.To == to);

    [Fact]
    public void An_app_is_linked_to_its_own_repo_s_database_and_to_what_it_waits_for()
    {
        Shop();
        var t = Build();

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
        Shop();
        var t = Build();

        Assert.Equal(["catalog-url"], Link(t, "shop-web/web", "shop-services/catalog-api").Values);
        Assert.Equal(["mail-smtp"], Link(t, "shop-services/orders-api", "storefront/mail").Values);
        Assert.Equal(["Search"], Link(t, "shop-services/orders-api", "shop-services/search").Values);
        Assert.Equal(["DB"], Link(t, "shop-services/pg-admin", "shop-services/pg").Values);
    }

    [Fact]
    public void Every_app_job_container_and_compose_service_is_a_node_of_its_kind()
    {
        Shop();
        var kinds = Build().Nodes.ToDictionary(n => n.Key, n => n.Kind);

        Assert.Equal(TopologyKind.App, kinds["shop-services/catalog-api"]);
        Assert.Equal(TopologyKind.Job, kinds["shop-services/migrate"]);
        Assert.Equal(TopologyKind.Container, kinds["shop-services/pg"]);
        Assert.Equal(TopologyKind.Container, kinds["storefront/mail"]);
        Assert.Equal(TopologyKind.ComposeService, kinds["shop-services/search"]);
    }

    [Fact]
    public void An_address_from_outside_gets_a_node_a_flag_doesn_t_and_a_setting_nothing_gives_is_missing()
    {
        Shop();
        var t = Build();

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
