using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Model;

namespace Sprig.Tests.Planning;

public class PlannerTests
{
    static readonly SprigSettings Settings = new() { PortRangeStart = 9000, PortRangeEndExclusive = 9100 };

    sealed class Fixture
    {
        readonly Dictionary<string, RepoModel> _repos = [];

        /// <summary>The orchestration file's lines after its repos, which the fixture writes from the repos added.</summary>
        public string Orchestration { get; set; } = "";

        public void Add(string path, string yaml) => _repos[path] = Yaml.Repo(yaml);

        public void Add(string path, RepoModel repo) => _repos[path] = repo;

        OrchestrationModel BuildOrchestration() => Yaml.Orchestration(string.Join("\n",
        [
            "schema: 1",
            "orchestration: storefront",
            "repos:",
            .. _repos.Keys.Select(path => $"  {Path.GetFileName(path)}: {{ path: {path} }}"),
            Orchestration,
        ]));

        public Plan Plan(SprigSettings? settings = null, IReadOnlySet<int>? inUse = null)
            => Planner.Build(new PlanInput(BuildOrchestration(), _repos, "feat-x", settings ?? Settings, inUse ?? new HashSet<int>()));

        public Plan PlanWith(Func<PlanInput, PlanInput> change)
            => Planner.Build(change(new PlanInput(BuildOrchestration(), _repos, "feat-x", Settings, new HashSet<int>())));
    }

    static IReadOnlyList<string> DependsOn(Plan plan, string app) => plan.Apps.Single(a => a.Name == app).DependsOn!;

    static RepoModel ChangeApp(RepoModel repo, string app, Func<AppModel, AppModel> change)
        => repo with { Apps = repo.Apps.Select(a => a.Name == app ? change(a) : a).ToArray() };

    [Fact]
    public void Apps_start_after_what_they_wait_for_and_after_apps_their_values_point_at()
    {
        var f = new Fixture();
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            containers:
              pg: { preset: postgres }
            apps:
              migrate:
                run: dotnet
                path: tools/Migrate
                once: true
                wait-for: [pg]
              catalog-api:
                run: dotnet
                ports: { http: any }
                wait-for: [pg, migrate]
            files:
              tools/Migrate/.env:
                read-by: [migrate]
                values:
                  Db: Host=localhost;Port=${pg.port};Database=catalog;Username=sprig;Password=sprig
            outputs:
              catalog-url:
                type: url
                value: ${catalog-api.url}
            """);
        f.Add("../shop-web", """
            schema: 1
            repo: shop-web
            inputs:
              catalog-url: { type: url }
            apps:
              web:
                run: node
                ports: { http: any }
            files:
              .env:
                read-by: [web]
                values:
                  API: ${inputs.catalog-url}
            """);

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.True(plan.Apps.Single(a => a.Name == "migrate").Job);
        Assert.Empty(DependsOn(plan, "migrate"));
        Assert.Equal(["shop-services/migrate"], DependsOn(plan, "catalog-api"));
        Assert.Equal(["shop-services/catalog-api"], DependsOn(plan, "web"));
        Assert.DoesNotContain(plan.Ports, p => p.Resource == "migrate");
    }

    const string Orders = """
        schema: 1
        repo: shop-services
        containers:
          mail: { preset: mailpit }
        inputs:
          mail-smtp:
            type: url
            default: ${mail.smtp-url}
        apps:
          orders:
            run: node
            ports: { http: any }
        files:
          .env:
            read-by: [orders]
            values:
              SMTP: ${inputs.mail-smtp}
        """;

    [Fact]
    public void A_setting_nothing_else_gives_uses_its_default_and_its_infra()
    {
        var f = new Fixture();
        f.Add("../shop-services", Orders);

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.Empty(plan.ToAsk);
        var mail = Assert.Single(plan.Containers);
        Assert.Equal(("shop-services", "mail"), (mail.Owner, mail.Name));
        var smtp = plan.Apps.Single().Env.Single().Value;
        Assert.Equal($"smtp://localhost:{plan.Ports.Single(p => p.Endpoint == "smtp").Port}", smtp.Value);
        Assert.StartsWith("setting mail-smtp ← shop-services's default", smtp.Via[0]);
        Assert.Equal("mail-smtp", Assert.Single(plan.Wires()).Setting);
    }

    [Fact]
    public void A_literal_default_is_used_as_it_is()
    {
        var f = new Fixture();
        f.Add("../shop-web", """
            schema: 1
            repo: shop-web
            inputs:
              catalog-url:
                type: url
                default: http://localhost:5100
            apps:
              web:
                run: node
                ports: { http: any }
            files:
              .env:
                read-by: [web]
                values:
                  API: ${inputs.catalog-url}
            """);

        var value = f.Plan().Apps.Single().Env.Single().Value;

        Assert.Equal("http://localhost:5100", value.Value);
        Assert.Equal(("shop-web", "catalog-url"), (Assert.Single(value.Sources!).Owner, value.Sources![0].Setting));
    }

    const string OrchestrationMail = """
        containers:
          mail: { preset: mailpit }
        inputs:
          mail-smtp: ${mail.smtp-url}
        """;

    [Fact]
    public void An_orchestration_that_gives_the_setting_wins_and_the_defaults_own_infra_is_left_out()
    {
        var f = new Fixture();
        f.Add("../shop-services", Orders);
        f.Orchestration = OrchestrationMail;

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.Equal("storefront", Assert.Single(plan.Containers).Owner);
        Assert.DoesNotContain(plan.Ports, p => p.Owner == "shop-services" && p.Resource == "mail");
        Assert.StartsWith("setting mail-smtp ← the orchestration", plan.Apps.Single().Env.Single().Value.Via[0]);
    }

    [Fact]
    public void A_value_set_on_the_workspace_beats_the_orchestration_and_the_default_and_leaves_their_infra_out()
    {
        var f = new Fixture();
        f.Add("../shop-services", Orders);
        f.Orchestration = OrchestrationMail;

        var plan = f.PlanWith(i => i with { Values = new Dictionary<string, string> { ["mail-smtp"] = "smtp://staging:25" } });

        Assert.Empty(plan.Problems);
        var smtp = plan.Apps.Single().Env.Single().Value;
        Assert.Equal("smtp://staging:25", smtp.Value);
        Assert.Equal("setting mail-smtp ← this workspace", smtp.Via[0]);
        Assert.Equal(ValueSource.Workspace, Assert.Single(plan.Wires()).ProviderOwner);
        Assert.DoesNotContain(plan.Ports, p => p.Owner == "shop-services" && p.Resource == "mail");
    }

    [Fact]
    public void A_value_set_on_the_workspace_beats_another_repos_export()
    {
        var f = new Fixture();
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            apps:
              catalog-api:
                run: dotnet
                ports: { http: any }
            outputs:
              catalog-url:
                type: url
                value: ${catalog-api.url}
            """);
        f.Add("../shop-web", """
            schema: 1
            repo: shop-web
            inputs:
              catalog-url:
                type: url
                default: http://localhost:5100
            apps:
              web:
                run: node
                ports: { http: any }
            files:
              .env:
                read-by: [web]
                values:
                  API: ${inputs.catalog-url}
            """);

        Assert.StartsWith("http://localhost:9", f.Plan().Apps.Single(a => a.Name == "web").Env.Single().Value.Value);
        var pointed = f.PlanWith(i => i with { Values = new Dictionary<string, string> { ["catalog-url"] = "https://catalog.example.com" } });
        Assert.Equal("https://catalog.example.com", pointed.Apps.Single(a => a.Name == "web").Env.Single().Value.Value);
        Assert.DoesNotContain("shop-services/catalog-api", DependsOn(pointed, "web"));
    }

    [Fact]
    public void A_setting_with_no_value_from_anywhere_is_asked_for_rather_than_a_problem()
    {
        var f = new Fixture();
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            containers:
              mail: { preset: mailpit }
            inputs:
              mail-smtp:
                type: url
                default: ${mail.smtp-url}
              payments-key: { type: secret }
            apps:
              orders:
                run: node
                path: orders
                ports: { http: any }
                wait-for: [mail]
              web:
                run: node
                path: web
                ports: { http: any }
            files:
              orders/.env:
                read-by: [orders]
                values:
                  SMTP: ${inputs.mail-smtp}
              web/.env:
                read-by: [web]
                values:
                  KEY: ${inputs.payments-key}
                  KEY_AGAIN: ${inputs.payments-key}
            """);
        f.Orchestration = """
            inputs:
              mail-smtp: smtp://elsewhere:25
            """;

        var plan = f.Plan();

        Assert.Single(plan.Containers);
        Assert.Empty(plan.Problems);
        var ask = Assert.Single(plan.ToAsk);
        Assert.Equal(("shop-services", "payments-key", "secret"), (ask.Owner, ask.Name, ask.Type));
        Assert.Null(plan.Apps.Single(a => a.Name == "web").Env.First().Value.Value);
        Assert.False(Assert.Single(plan.Wires(), w => w.Key == "KEY").Filled);

        var given = f.PlanWith(i => i with { Values = new Dictionary<string, string> { ["payments-key"] = "k" } });
        Assert.Empty(given.ToAsk);
        Assert.Equal("k", given.Apps.Single(a => a.Name == "web").Env.First().Value.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Two_apps_that_call_each_other_start_in_the_same_order_whichever_is_declared_first(bool bFirst)
    {
        const string a = "  a: { run: dotnet, path: a, ports: { http: any } }";
        const string b = "  b: { run: dotnet, path: b, ports: { http: any } }";
        string[] apps = bFirst ? [b, a] : [a, b];
        var f = new Fixture();
        f.Add("../r", $$"""
            schema: 1
            repo: r
            apps:
            {{string.Join("\n", apps)}}
            files:
              a/.env:
                read-by: [a]
                values:
                  B: ${b.url}
              b/.env:
                read-by: [b]
                values:
                  A: ${a.url}
            """);

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.Equal(["r/b"], DependsOn(plan, "a"));
        Assert.Empty(DependsOn(plan, "b"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_front_end_waits_for_the_api_it_calls_and_nothing_waits_for_a_front_end_because_it_names_its_address(bool apisFirst)
    {
        string[] frontEnds =
        [
            "  client: { run: node, path: client, ports: { http: any } }",
            "  admin-ui: { run: command, command: x, path: admin-ui, ports: { http: any } }",
        ];
        string[] backend =
        [
            "  orders-api: { run: dotnet, path: orders-api, ports: { http: any } }",
            "  billing-api: { run: dotnet, path: billing-api, ports: { http: any } }",
            "  sync-worker: { run: dotnet, path: sync-worker, ports: { http: any } }",
        ];
        string[] apps = apisFirst ? [.. backend, .. frontEnds] : [.. frontEnds, .. backend];
        var f = new Fixture();
        // The worker and an API point at each other: the worker calls the API, so it starts second.
        f.Add("../r", $$"""
            schema: 1
            repo: r
            apps:
            {{string.Join("\n", apps)}}
            files:
              client/.env:
                read-by: [client]
                values:
                  API: ${orders-api.url}
              admin-ui/.env:
                read-by: [admin-ui]
                values:
                  API: ${billing-api.url}
              orders-api/.env:
                read-by: [orders-api]
                values:
                  CLIENT_URL: ${client.url}
                  ADMIN_URL: ${admin-ui.url}
                  SYNC: ${sync-worker.url}
              billing-api/.env:
                read-by: [billing-api]
                values:
                  CLIENT_URL: ${client.url}
                  ADMIN_URL: ${admin-ui.url}
              sync-worker/.env:
                read-by: [sync-worker]
                values:
                  CLIENT_URL: ${client.url}
                  ADMIN_URL: ${admin-ui.url}
                  ORDERS: ${orders-api.url}
            """);

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.Equal(["r/orders-api"], DependsOn(plan, "client"));
        Assert.Equal(["r/billing-api"], DependsOn(plan, "admin-ui"));
        Assert.Empty(DependsOn(plan, "orders-api"));
        Assert.Empty(DependsOn(plan, "billing-api"));
        Assert.Equal(["r/orders-api"], DependsOn(plan, "sync-worker"));
    }

    [Fact]
    public void Apps_that_share_a_file_don_t_use_each_other_through_its_values_but_still_get_them_all()
    {
        var f = new Fixture();
        // One .env every backend app reads, holding each one's own port; the worker calls orders-api through a file of its own.
        f.Add("../r", """
            schema: 1
            repo: r
            apps:
              web: { run: node, path: web, ports: { http: any } }
              orders-api: { run: dotnet, path: orders-api, ports: { http: any } }
              billing-api: { run: dotnet, path: billing-api, ports: { http: any } }
              sync-worker: { run: dotnet, path: sync-worker, ports: { http: any } }
            files:
              backend.env:
                read-by: [orders-api, billing-api, sync-worker]
                values:
                  Orders_Port: ${orders-api.port}
                  Billing_Port: ${billing-api.port}
                  Sync_Port: ${sync-worker.port}
                  WEB_URL: ${web.url}
              sync-worker/.env:
                read-by: [sync-worker]
                values:
                  ORDERS: ${orders-api.url}
            """);

        var plan = f.Plan();
        var links = WorkspaceTopology.Of(plan).Links;

        Assert.Empty(plan.Problems);
        Assert.Empty(DependsOn(plan, "orders-api"));
        Assert.Empty(DependsOn(plan, "billing-api"));
        Assert.Equal(["r/orders-api"], DependsOn(plan, "sync-worker"));
        Assert.DoesNotContain(links, l => l.From == "r/orders-api" && l.To == "r/billing-api");
        Assert.DoesNotContain(links, l => l.From == "r/billing-api" && l.To == "r/orders-api");
        Assert.Equal(["ORDERS"], links.Single(l => l.From == "r/sync-worker" && l.To == "r/orders-api").Values);
        Assert.Equal(["WEB_URL"], links.Single(l => l.From == "r/billing-api" && l.To == "r/web").Values);
        var billing = plan.Apps.Single(a => a.Name == "billing-api").Env;
        Assert.Equal(["Orders_Port", "Billing_Port", "Sync_Port", "WEB_URL"], billing.Select(e => e.Key));
        Assert.All(billing, e => Assert.NotNull(e.Value.Value));
    }

    [Fact]
    public void A_front_end_someone_waits_for_by_name_is_still_waited_for()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            apps:
              client:
                run: node
                ports: { http: any }
              e2e:
                run: dotnet
                ports: { http: any }
                wait-for: [client]
            """);

        Assert.Equal(["r/client"], DependsOn(f.Plan(), "e2e"));
    }

    [Fact]
    public void Readiness_checks_and_restart_policies_carry_into_the_plan()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            apps:
              api:
                run: dotnet
                ports:
                  http: any
                  admin: { host: any, scheme: http }
                health: /health
                restart: always
              web:
                run: node
                ports: { http: any }
                ready-log: ready in
              seed:
                run: command
                command: ./seed.sh
                once: true
                retry: 3
              plain:
                run: node
                ports: { http: any }
                restart: never
            """);

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        var api = plan.Apps.Single(a => a.Name == "api");
        Assert.Equal([new PlannedHealth("http", "/health")], api.Health);
        Assert.Equal(("always", 5), (api.Restart!.Policy, api.Restart.Max));
        Assert.Equal("ready in", plan.Apps.Single(a => a.Name == "web").ReadyLog);
        Assert.Equal(("onFailure", 3), (plan.Apps.Single(a => a.Name == "seed").Restart!.Policy, plan.Apps.Single(a => a.Name == "seed").Restart!.Max));
        Assert.Null(plan.Apps.Single(a => a.Name == "plain").Restart);
    }

    [Fact]
    public void Quick_links_resolve_to_each_workspaces_ports_without_making_one_app_wait_for_another()
    {
        var f = new Fixture();
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            apps:
              catalog-api:
                run: dotnet
                ports:
                  http: any
                  admin: { host: any, scheme: http }
                links:
                  Swagger: /swagger
                  Admin: ${catalog-api.admin.url}
            outputs:
              catalog-url:
                type: url
                value: ${catalog-api.url}
            """);
        f.Add("../shop-web", """
            schema: 1
            repo: shop-web
            apps:
              web:
                run: node
                ports: { http: any }
                links:
                  Storefront: /
                  Docs: https://example.com/docs
              admin:
                run: node
                ports: { http: any }
                links:
                  Web's cart: ${web.url}/cart?seed=demo
            """);
        f.Orchestration = """
            containers:
              mail:
                image: mailpit
                ports:
                  ui: { container: 8025, scheme: http }
            links:
              Mail inbox: ${mail.ui.url}
              Catalog: ${shop-services.outputs.catalog-url}/products
            """;

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        string Port(string resource, string endpoint = "http") => plan.Ports.Single(p => p.Resource == resource && p.Endpoint == endpoint).Port!.Value.ToString();
        var api = plan.Apps.Single(a => a.Name == "catalog-api");
        Assert.Equal([new PlannedLink("Swagger", $"http://localhost:{Port("catalog-api")}/swagger"), new PlannedLink("Admin", $"http://localhost:{Port("catalog-api", "admin")}")],
            api.Links);
        Assert.Equal([new PlannedLink("Storefront", $"http://localhost:{Port("web")}"), new PlannedLink("Docs", "https://example.com/docs")],
            plan.Apps.Single(a => a.Name == "web").Links);
        var admin = plan.Apps.Single(a => a.Name == "admin");
        Assert.Equal($"http://localhost:{Port("web")}/cart?seed=demo", Assert.Single(admin.Links!).Url);
        Assert.Empty(admin.DependsOn!);
        Assert.Equal([new PlannedLink("Mail inbox", $"http://localhost:{Port("mail", "ui")}"), new PlannedLink("Catalog", $"http://localhost:{Port("catalog-api")}/products")],
            plan.Links);
    }

    [Fact]
    public void A_quick_link_that_isnt_a_web_address_is_left_out_with_a_warning()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            containers:
              pg: { preset: postgres }
            apps:
              a:
                run: node
                ports: { http: any }
                links:
                  Database: Host=localhost;Port=${pg.port};Database=app;Username=sprig;Password=sprig
                  Home: /
            """);

        var plan = f.Plan();

        var warning = Assert.Single(plan.Problems);
        Assert.Equal(ProblemSeverity.Warning, warning.Severity);
        Assert.Contains("'Database' goes to", warning.Message);
        Assert.Equal(["Home"], plan.Apps.Single().Links!.Select(l => l.Label));
    }

    [Fact]
    public void A_bad_health_path_log_pattern_or_restart_count_is_an_error()
    {
        var repo = Yaml.Repo("""
            schema: 1
            repo: r
            apps:
              a:
                run: node
                ports: { http: any }
                health: /health
              b:
                run: node
                ports: { http: any }
                ready-log: ready in
              c:
                run: node
                ports: { http: any }
                restart: on-failure
            """);
        // The format refuses each of these as it's read, so they're set on the model to test the planner refuses them too.
        repo = ChangeApp(repo, "a", a => a with { Endpoints = [a.Endpoints[0] with { Health = "health" }] });
        repo = ChangeApp(repo, "b", b => b with { ReadyLog = "ready (in" });
        repo = ChangeApp(repo, "c", c => c with { Restart = c.Restart! with { Max = 0 } });
        var f = new Fixture();
        f.Add("../r", repo);

        var problems = f.Plan().Problems.Select(p => p.Message).ToArray();

        Assert.Equal(3, problems.Length);
        Assert.Contains(problems, m => m.Contains("'a' has a health path 'health'") && m.Contains("starting with /"));
        Assert.Contains(problems, m => m.Contains("'b' waits for a log line matching 'ready (in'") && m.Contains("isn't a valid regular expression"));
        Assert.Contains(problems, m => m.Contains("'c' restarts at most 0 times"));
    }

    [Fact]
    public void Apps_that_wait_for_each_other_are_an_error()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            apps:
              a:
                run: node
                ports: { http: any }
                wait-for: [b]
              b:
                run: node
                ports: { http: any }
                wait-for: [a]
            """);

        Assert.Contains("r/a → r/b → r/a wait for each other", Assert.Single(f.Plan().Problems).Message);
    }

    const string WebAndAdmin = """
        schema: 1
        repo: r
        apps:
          web:
            run: node
            ports: { http: any }
          admin:
            run: node
            ports: { http: any }
        """;

    [Fact]
    public void A_workspace_keeps_its_leased_ports_and_only_new_endpoints_get_new_ones()
    {
        var f = new Fixture();
        f.Add("../r", WebAndAdmin);
        var leased = new Dictionary<string, int> { ["r/web.http"] = 9050 };

        var plan = f.PlanWith(i => i with { Leased = leased });

        Assert.Empty(plan.Problems);
        Assert.Equal(9050, Port(plan, "web"));
        Assert.Equal(9000, Port(plan, "admin"));
        Assert.Equal("r/web.http", plan.Ports.Single(p => p.Resource == "web").Key);
    }

    [Fact]
    public void A_leased_port_is_given_up_when_something_else_has_it_or_its_pin_no_longer_allows_it()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            apps:
              web:
                run: node
                ports: { http: any }
              pinned:
                run: node
                ports: { http: 9020-9021 }
            """);
        var leased = new Dictionary<string, int> { ["r/web.http"] = 9050, ["r/pinned.http"] = 9000 };

        var plan = f.PlanWith(i => i with { Leased = leased, PortsInUse = new HashSet<int> { 9050 } });

        Assert.Empty(plan.Problems);
        Assert.NotEqual(9050, Port(plan, "web"));
        Assert.Equal(9020, Port(plan, "pinned"));
    }

    [Fact]
    public void Compose_services_the_repo_does_not_name_are_left_out_with_a_warning()
    {
        var f = new Fixture();
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            compose:
              docker-compose.yml:
                services:
                  postgres:
                    ports: { tcp: { container: 5432 } }
                  redis: {}
            """);
        var info = new ComposeFileInfo(["postgres", "redis", "mailpit", "seq", "tools"], ["postgres"], ["tools"]);

        var plan = f.PlanWith(i => i with { ComposeFiles = new Dictionary<string, ComposeFileInfo> { [ComposeFileInfo.Key("../shop-services", "docker-compose.yml")] = info } });

        var warning = Assert.Single(plan.Problems);
        Assert.Equal(ProblemSeverity.Warning, warning.Severity);
        Assert.Contains("also has 'mailpit', 'seq', which its sprig.yaml doesn't name, so workspaces leave them out", warning.Message);
        var compose = Assert.Single(plan.Compose);
        Assert.Equal(["postgres", "redis"], compose.Services.Select(s => s.Name));
        Assert.Contains(compose.Overrides, o => o.Path == "services.postgres.container_name" && o.Generated);
    }

    [Fact]
    public void Naming_a_compose_service_the_file_does_not_have_is_an_error()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            compose:
              compose.yml:
                services:
                  postgress:
                    ports: { tcp: { container: 5432 } }
            """);
        var info = new ComposeFileInfo(["postgres"], [], []);

        var plan = f.PlanWith(i => i with { ComposeFiles = new Dictionary<string, ComposeFileInfo> { [ComposeFileInfo.Key("../r", "compose.yml")] = info } });

        Assert.Contains(plan.Problems, p => p.Severity == ProblemSeverity.Error && p.Message == "compose.yml has no service named 'postgress'");
    }

    [Fact]
    public void Each_value_keeps_its_file_and_a_launch_profile_for_a_node_app_is_a_warning()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            apps:
              api:
                run: dotnet
                ports: { http: any }
              web:
                run: node
                path: web
                ports: { http: any }
            files:
              Properties/launchSettings.json:
                read-by: [api]
                values:
                  ASPNETCORE_URLS: ${api.url}
              web/Properties/launchSettings.json:
                read-by: [web]
                values:
                  PORT: ${web.port}
            """);

        var plan = f.Plan();

        var urls = Env(plan, "api", "ASPNETCORE_URLS");
        Assert.Equal(("Properties/launchSettings.json", "launchSettings", "sprig"), (urls.File, urls.Kind, urls.Profile));
        Assert.Contains("'web' isn't a .NET app", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void A_value_meant_for_the_process_is_refused_since_running_the_app_yourself_would_not_get_it()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            apps:
              api:
                run: dotnet
                ports: { http: any }
            """);
        var at = new SourceLocation("sprig.yaml", 3);

        // The format has nowhere to write a value but a file, so one with no file is put on the model directly.
        var plan = f.PlanWith(i => i with
        {
            Repos = i.Repos.ToDictionary(r => r.Key, r => r.Value with
            {
                Apps = [r.Value.Apps[0] with { Env = [new EnvModel("Feature", "on", null, at)] }],
            }),
        });

        var problem = Assert.Single(plan.Problems);
        Assert.Equal(ProblemSeverity.Error, problem.Severity);
        Assert.Contains("'api' would get Feature on its process, which running it yourself wouldn't", problem.Message);
        Assert.Contains("Write it into a file the app reads, under files:", problem.Message);
        Assert.Equal(at, problem.Location);
    }

    [Fact]
    public void A_value_in_a_run_command_is_refused_since_typing_the_command_would_not_fill_it_in()
    {
        var repo = Yaml.Repo("""
            schema: 1
            repo: r
            apps:
              web:
                run: node
                command: npm run dev
                ports: { http: any }
              docs:
                run: node
                command: npm run docs
                ports: { http: any }
            """);
        // The format refuses a reference in a command as it's read, so it's put on the model to test the planner refuses it too.
        repo = ChangeApp(repo, "web", web => web with { Run = "npm run dev -- --port {web.http.port}" });
        var f = new Fixture();
        f.Add("../r", repo);

        var problem = Assert.Single(f.Plan().Problems);

        Assert.Equal(ProblemSeverity.Error, problem.Severity);
        Assert.Contains("'web' has {web.http.port} in its Run command", problem.Message);
        Assert.Contains("the value in a file the app reads, under files:", problem.Message);
    }

    static PlannedEnv Env(Plan plan, string app, string key)
        => plan.Apps.Single(a => a.Name == app).Env.Single(e => e.Key == key);

    static int Port(Plan plan, string resource, string endpoint = "http")
        => plan.Ports.Single(p => p.Resource == resource && p.Endpoint == endpoint).Port!.Value;

    [Fact]
    public void A_setting_binds_to_the_one_export_of_that_name_and_says_how_it_got_there()
    {
        var f = new Fixture();
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            apps:
              catalog-api:
                run: dotnet
                path: src/Catalog.Api
                ports: { http: any }
            files:
              src/Catalog.Api/Properties/launchSettings.json:
                read-by: [catalog-api]
                values:
                  ASPNETCORE_URLS: ${catalog-api.url}
            outputs:
              catalog-url:
                type: url
                value: ${catalog-api.url}
            """);
        f.Add("../shop-web", """
            schema: 1
            repo: shop-web
            inputs:
              catalog-url:
                type: url
                default: http://localhost:5100
            apps:
              web:
                run: node
                ports: { http: 5170-5173 }
            files:
              .env.local:
                read-by: [web]
                values:
                  VITE_CATALOG_URL: ${inputs.catalog-url}
            """);

        var plan = f.Plan();

        Assert.False(plan.HasErrors, string.Join("\n", plan.Problems));
        var catalogPort = Port(plan, "catalog-api");
        var env = Env(plan, "web", "VITE_CATALOG_URL");
        Assert.Equal($"http://localhost:{catalogPort}", env.Value.Value);
        Assert.Equal(".env.local", env.File);
        Assert.Equal(["setting catalog-url ← shop-services", $"catalog-api.http port {catalogPort}"], env.Value.Via);
        Assert.Equal($"http://localhost:{catalogPort}", Env(plan, "catalog-api", "ASPNETCORE_URLS").Value.Value);
    }

    static string CacheUrlOutput(string repo) => $"""
        schema: 1
        repo: {repo}
        outputs:
          cache-url:
            type: url
            value: redis://{repo}
        """;

    [Fact]
    public void Two_exporters_of_a_name_is_an_error_until_the_orchestration_picks_one()
    {
        var f = new Fixture();
        f.Add("../a", CacheUrlOutput("a"));
        f.Add("../b", CacheUrlOutput("b"));
        f.Add("../web", """
            schema: 1
            repo: web
            inputs:
              cache-url: { type: url }
            apps:
              web:
                run: node
                ports: { http: any }
            files:
              .env:
                read-by: [web]
                values:
                  CACHE: ${inputs.cache-url}
            """);

        var ambiguous = f.Plan();
        var problem = Assert.Single(ambiguous.Problems);
        Assert.Contains("a and b all output 'cache-url'", problem.Message);
        Assert.Contains("inputs: { cache-url: ${<repo>.outputs.cache-url} }", problem.Message);

        f.Orchestration = """
            inputs:
              cache-url: ${a.outputs.cache-url}
            """;
        var settled = f.Plan();
        Assert.Empty(settled.Problems);
        Assert.Equal("redis://a", Env(settled, "web", "CACHE").Value.Value);
    }

    const string WebAskingForApiUrl = """
        schema: 1
        repo: web
        inputs:
          api-url: { type: url }
        apps:
          web:
            run: node
            ports: { http: any }
        files:
          .env:
            read-by: [web]
            values:
              API: ${inputs.api-url}
        """;

    [Fact]
    public void A_setting_to_ask_for_says_where_it_was_declared()
    {
        var f = new Fixture();
        f.Add("../web", WebAskingForApiUrl);

        var ask = Assert.Single(f.Plan().ToAsk);

        Assert.Equal(("web", "api-url", "url"), (ask.Owner, ask.Name, ask.Type));
        Assert.Equal(new SourceLocation("sprig.yaml", 4), ask.Location);
        Assert.Null(Env(f.Plan(), "web", "API").Value.Value);
    }

    [Fact]
    public void A_setting_of_the_wrong_type_is_refused()
    {
        var f = new Fixture();
        f.Add("../db", """
            schema: 1
            repo: db
            outputs:
              orders-db:
                type: connection-string
                value: Host=x
            """);
        f.Add("../web", """
            schema: 1
            repo: web
            inputs:
              orders-db: { type: url }
            apps:
              web:
                run: node
                ports: { http: any }
            files:
              .env:
                read-by: [web]
                values:
                  DB: ${inputs.orders-db}
            """);

        Assert.Contains("web's input 'orders-db' is a url, but db outputs it as connectionstring", Assert.Single(f.Plan().Problems).Message);
    }

    [Fact]
    public void Infra_the_orchestration_owns_can_give_a_setting()
    {
        var f = new Fixture();
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            inputs:
              mail-smtp: { type: url }
            apps:
              orders-api:
                run: dotnet
                ports: { http: any }
            files:
              .env:
                read-by: [orders-api]
                values:
                  Smtp__Url: ${inputs.mail-smtp}
            """);
        f.Orchestration = OrchestrationMail;

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        var smtp = Port(plan, "mail", "smtp");
        Assert.Equal($"smtp://localhost:{smtp}", Env(plan, "orders-api", "Smtp__Url").Value.Value);
        var container = Assert.Single(plan.Containers);
        Assert.Equal("storefront", container.Owner);
        Assert.Equal([$"{smtp}:1025", $"{Port(plan, "mail", "ui")}:8025"], container.Ports);
        Assert.Equal("/mailpit readyz", container.Health);
    }

    [Fact]
    public void Presets_build_connection_strings_from_their_allocated_port()
    {
        var f = new Fixture();
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            containers:
              pg: { preset: postgres }
              bus: { preset: rabbitmq }
            apps:
              catalog-api:
                run: dotnet
                ports: { http: any }
            files:
              .env:
                read-by: [catalog-api]
                values:
                  ConnectionStrings__Db: Host=localhost;Port=${pg.port};Database=catalog;Username=sprig;Password=sprig
                  Bus__Url: ${bus.amqp-url}
            """);

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.Equal($"Host=localhost;Port={Port(plan, "pg", "tcp")};Database=catalog;Username=sprig;Password=sprig",
            Env(plan, "catalog-api", "ConnectionStrings__Db").Value.Value);
        Assert.Equal($"amqp://sprig:sprig@localhost:{Port(plan, "bus", "amqp")}", Env(plan, "catalog-api", "Bus__Url").Value.Value);
        Assert.Contains("data → /var/lib/postgresql/data", plan.Containers.Single(c => c.Name == "pg").Volumes);
    }

    [Fact]
    public void Ports_skip_restricted_and_busy_ones_and_pins_are_served_first()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            apps:
              first:
                run: node
                ports: { http: any }
              pinned:
                run: node
                ports: { http: 9000 }
            """);
        var settings = new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9010, RestrictedPorts = [9001] };

        var plan = f.Plan(settings, inUse: new HashSet<int> { 9002 });

        Assert.Empty(plan.Problems);
        Assert.Equal(9000, Port(plan, "pinned"));
        Assert.Equal(9003, Port(plan, "first"));
        Assert.Equal(["first", "pinned"], plan.Ports.Select(p => p.Resource));
    }

    [Fact]
    public void A_pin_with_no_free_port_left_is_an_error_on_that_endpoint()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            apps:
              web:
                run: node
                ports: { http: 8100 }
            """);

        var plan = f.Plan(inUse: new HashSet<int> { 8100 });

        Assert.Contains("every port in the pin \"8100\" is taken", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void Names_are_private_to_their_repo()
    {
        var f = new Fixture();
        f.Add("../a", """
            schema: 1
            repo: a
            containers:
              pg: { preset: postgres }
            apps:
              api:
                run: dotnet
                ports: { http: any }
            files:
              .env:
                read-by: [api]
                values:
                  Db: ${pg.port}
            """);
        var b = Yaml.Repo("""
            schema: 1
            repo: b
            containers:
              pg: { preset: postgres }
            apps:
              web:
                run: node
                ports: { http: any }
            files:
              .env:
                read-by: [web]
                values:
                  Api: http://localhost
            """);
        // The format refuses a name from another repo as it's read, so it's put on the model to test the planner refuses it too.
        f.Add("../b", ChangeApp(b, "web", web => web with { Env = [web.Env[0] with { Value = "{api.http.url}" }] }));

        var plan = f.Plan();

        Assert.Equal(2, plan.Containers.Count(c => c.Name == "pg"));
        Assert.NotEqual(plan.Ports.First(p => p.Owner == "a" && p.Resource == "pg").Port, plan.Ports.First(p => p.Owner == "b" && p.Resource == "pg").Port);
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("'api' isn't declared in b; to use something from another repo, make it a setting", problem.Message);
    }

    [Fact]
    public void A_compose_service_gets_its_ports_mapped_and_other_values_set()
    {
        var f = new Fixture();
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            compose:
              docker-compose.yml:
                services:
                  postgres:
                    ports: { tcp: { container: 5432 } }
                set:
                  services.postgres.container_name: shop-pg-${workspace.name}
            apps:
              api:
                run: dotnet
                ports: { http: any }
            files:
              .env:
                read-by: [api]
                values:
                  Db: Host=localhost;Port=${postgres.port}
            """);

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        var overrides = Assert.Single(plan.Compose).Overrides;
        var pgPort = Port(plan, "postgres", "tcp");
        Assert.Equal($"{pgPort}:5432", overrides.Single(o => o.Path == "services.postgres.ports").Value.Value);
        Assert.Equal("shop-pg-feat-x", overrides.Single(o => o.Path == "services.postgres.container_name").Value.Value);
        Assert.Equal($"Host=localhost;Port={pgPort}", Env(plan, "api", "Db").Value.Value);
    }

    const string CatalogAndOrders = """
        schema: 1
        repo: shop-services
        apps:
          catalog-api:
            run: dotnet
            ports: { http: any }
          orders-api:
            run: dotnet
            ports: { http: any }
        """;

    [Fact]
    public void Groups_list_their_apps_and_reject_ones_that_do_not_exist()
    {
        var f = new Fixture();
        f.Add("../shop-services", CatalogAndOrders);
        f.Orchestration = """
            groups:
              mobile: [shop-services/catalog-api]
              all: [shop-services]
              typo: [shop-services/catalgo-api]
            """;

        var plan = f.Plan();

        Assert.Equal(["shop-services/catalog-api"], plan.Groups.Single(g => g.Name == "mobile").Apps);
        Assert.Equal(2, plan.Groups.Single(g => g.Name == "all").Apps.Count);
        Assert.Contains("'catalgo-api', which shop-services doesn't declare", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void A_repos_groups_join_others_of_the_same_name_and_an_orchestrations_group_replaces_them()
    {
        var f = new Fixture();
        f.Add("../shop-services", $"""
            {CatalogAndOrders}
            groups:
              apis: [catalog-api, orders-api]
              client: [catalog-api]
            """);
        f.Add("../shop-web", """
            schema: 1
            repo: shop-web
            apps:
              web:
                run: node
                ports: { http: any }
            groups:
              client: [web]
            """);
        f.Orchestration = """
            groups:
              apis: [shop-services/orders-api]
            """;

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.Equal(["apis", "client"], plan.Groups.Select(g => g.Name));
        Assert.Equal(["shop-services/catalog-api", "shop-web/web"], plan.Groups.Single(g => g.Name == "client").Apps);
        Assert.Equal(["shop-services/orders-api"], plan.Groups.Single(g => g.Name == "apis").Apps);
    }

    [Fact]
    public void A_repos_group_holds_only_its_apps_and_jobs()
    {
        var repo = Yaml.Repo("""
            schema: 1
            repo: shop-services
            containers:
              redis: { preset: redis }
            apps:
              api:
                run: dotnet
                ports: { http: any }
            groups:
              cache: [api]
            """);
        // The format refuses a container in a group as it's read, so it's put on the model to test the planner refuses it too.
        repo = repo with { Groups = [repo.Groups![0] with { Apps = ["redis", "api"] }] };
        var f = new Fixture();
        f.Add("../shop-services", repo);

        var plan = f.Plan();

        Assert.Equal(["shop-services/api"], plan.Groups.Single().Apps);
        Assert.Contains("'redis', which shop-services doesn't declare as an app or job", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void A_binding_no_repo_has_a_setting_for_is_a_warning_not_an_error()
    {
        var f = new Fixture();
        f.Add("../r", """
            schema: 1
            repo: r
            apps:
              web:
                run: node
                ports: { http: any }
            """);
        f.Orchestration = """
            inputs:
              unused: x
            """;

        var plan = f.Plan();

        Assert.False(plan.HasErrors);
        Assert.Equal(ProblemSeverity.Warning, Assert.Single(plan.Problems).Severity);
    }

    [Fact]
    public void Waiting_for_something_undeclared_is_an_error()
    {
        var repo = Yaml.Repo("""
            schema: 1
            repo: r
            apps:
              api:
                run: dotnet
                ports: { http: any }
            """);
        // The format refuses waiting for a name the file doesn't declare as it's read, so it's put on the model to test the planner refuses it too.
        var f = new Fixture();
        f.Add("../r", ChangeApp(repo, "api", api => api with { WaitFor = ["ghost"] }));

        Assert.Contains("waits for 'ghost'", Assert.Single(f.Plan().Problems).Message);
    }

    // ---- wires: where each value comes from, as data ----

    [Fact]
    public void A_setting_given_by_an_export_is_a_wire_to_the_app_behind_it()
    {
        var f = new Fixture();
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            containers:
              pg: { preset: postgres }
            apps:
              catalog-api:
                run: dotnet
                path: catalog
                ports: { http: any }
              orders-api:
                run: dotnet
                path: orders
                ports: { http: any }
            files:
              catalog/.env:
                read-by: [catalog-api]
                values:
                  Db: Host=localhost;Port=${pg.port};Database=catalog;Username=sprig;Password=sprig
              orders/.env:
                read-by: [orders-api]
                values:
                  Catalog__Url: ${catalog-api.url}
            outputs:
              catalog-url:
                type: url
                value: ${catalog-api.url}
            """);
        f.Add("../shop-web", """
            schema: 1
            repo: shop-web
            inputs:
              catalog-url: { type: url }
            apps:
              web:
                run: node
                ports: { http: any }
            files:
              .env:
                read-by: [web]
                values:
                  VITE_CATALOG_URL: ${inputs.catalog-url}
            """);

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.Equal([new ValueSource("shop-services", "catalog-api", "http", "catalog-url")], Env(plan, "web", "VITE_CATALOG_URL").Value.Sources);
        // Values a repo takes from its own resources by name are inside its box, not wires.
        Assert.Equal([new PlannedWire("shop-web", "web", "VITE_CATALOG_URL", "catalog-url", "shop-services", "catalog-api", "http")], plan.Wires());
        Assert.Equal([new ValueSource("shop-services", "catalog-api", "http", null)], Env(plan, "orders-api", "Catalog__Url").Value.Sources);
    }

    [Fact]
    public void An_orchestration_binding_is_a_wire_from_the_orchestration_and_names_what_it_points_at()
    {
        var f = new Fixture();
        f.Add("../a", CacheUrlOutput("a"));
        f.Add("../b", CacheUrlOutput("b"));
        f.Add("../shop-services", """
            schema: 1
            repo: shop-services
            inputs:
              mail-smtp: { type: url }
              cache-url: { type: url }
              flag: { type: text }
            apps:
              orders-api:
                run: dotnet
                ports: { http: any }
            files:
              .env:
                read-by: [orders-api]
                values:
                  Smtp__Url: ${inputs.mail-smtp}
                  Cache: ${inputs.cache-url}
                  Flag: ${inputs.flag}
            """);
        f.Orchestration = """
            containers:
              mail: { preset: mailpit }
            inputs:
              mail-smtp: ${mail.smtp-url}
              cache-url: ${a.outputs.cache-url}
              flag: on
            """;

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.Equal(
            [
                new PlannedWire("shop-services", "orders-api", "Smtp__Url", "mail-smtp", "storefront", "mail", "smtp"),
                new PlannedWire("shop-services", "orders-api", "Cache", "cache-url", "a", null, null),
                new PlannedWire("shop-services", "orders-api", "Flag", "flag", "storefront", null, null),
            ],
            plan.Wires());
    }

    [Fact]
    public void A_setting_to_ask_for_is_a_wire_with_no_provider()
    {
        var f = new Fixture();
        f.Add("../web", WebAskingForApiUrl);

        var wire = Assert.Single(f.Plan().Wires());

        Assert.False(wire.Filled);
        Assert.Equal(("web", "API", "api-url"), (wire.Consumer, wire.Key, wire.Setting));
    }
}

public class TemplateTextTests
{
    [Fact]
    public void Parses_text_placeholders_and_escaped_braces()
    {
        var parts = TemplateText.Parse("a{{b}}{x.y.port}c");

        Assert.Equal([new TextPart("a{b}"), new RefPart("x.y.port"), new TextPart("c")], parts);
    }

    [Theory]
    [InlineData("a{b")]
    [InlineData("a}b")]
    public void Unbalanced_braces_are_rejected(string template)
        => Assert.Throws<FormatException>(() => TemplateText.Parse(template));

    [Theory]
    [InlineData("8100-8103", new[] { 8100, 8101, 8102, 8103 })]
    [InlineData("8100, 8200", new[] { 8100, 8200 })]
    [InlineData("8100-8101,8101", new[] { 8100, 8101 })]
    public void Pins_parse_to_ports(string pin, int[] expected) => Assert.Equal(expected, PortSet.Parse(pin));

    [Theory]
    [InlineData("8103-8100")]
    [InlineData("abc")]
    [InlineData("70000")]
    public void Bad_pins_are_rejected(string pin) => Assert.Throws<FormatException>(() => PortSet.Parse(pin));
}
