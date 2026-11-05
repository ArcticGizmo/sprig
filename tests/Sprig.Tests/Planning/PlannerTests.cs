using Sprig;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Model;

namespace Sprig.Tests.Planning;

public class PlannerTests
{
    static readonly SprigSettings Settings = new() { PortRangeStart = 9000, PortRangeEndExclusive = 9100 };

    sealed class Fixture
    {
        public TrellisBuilder Trellis { get; } = Sprig.Trellis.Describe("storefront");
        readonly Dictionary<string, RepoModel> _repos = [];

        public RepoRef Add(string path, Action<RepoBuilder> describe)
        {
            var repo = Repo.Describe(Path.GetFileName(path));
            describe(repo);
            _repos[path] = repo.BuildRepo();
            return Trellis.Repo(path);
        }

        public Plan Plan(SprigSettings? settings = null, IReadOnlySet<int>? inUse = null)
            => Planner.Build(new PlanInput(Trellis.BuildTrellis(), _repos, "feat-x", settings ?? Settings, inUse ?? new HashSet<int>()));

        public Plan PlanWith(Func<PlanInput, PlanInput> change)
            => Planner.Build(change(new PlanInput(Trellis.BuildTrellis(), _repos, "feat-x", Settings, new HashSet<int>())));
    }

    static IReadOnlyList<string> DependsOn(Plan plan, string app) => plan.Apps.Single(a => a.Name == app).DependsOn!;

    [Fact]
    public void Apps_start_after_what_they_wait_for_and_after_apps_their_values_point_at()
    {
        var f = new Fixture();
        f.Add("../shop-services", repo =>
        {
            var pg = repo.Postgres("pg");
            var migrate = repo.DotNetJob("migrate", "tools/Migrate").Env("Db", pg.Database("catalog")).WaitFor(pg);
            var catalog = repo.DotNet("catalog-api", ".").Http(urlEnv: "Urls").WaitFor(pg, migrate);
            repo.Export<Url>("catalog-url", catalog.Url);
        });
        f.Add("../shop-web", repo => repo.Node("web", ".").Http().Env("API", repo.Import<Url>("catalog-url")));

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.True(plan.Apps.Single(a => a.Name == "migrate").Job);
        Assert.Empty(DependsOn(plan, "migrate"));
        Assert.Equal(["shop-services/migrate"], DependsOn(plan, "catalog-api"));
        Assert.Equal(["shop-services/catalog-api"], DependsOn(plan, "web"));
        Assert.DoesNotContain(plan.Ports, p => p.Resource == "migrate");
    }

    [Fact]
    public void Two_apps_that_call_each_other_start_in_order_instead_of_waiting_forever()
    {
        var f = new Fixture();
        f.Add("../r", repo =>
        {
            var a = repo.Node("a", ".").Http();
            var b = repo.Node("b", ".").Http();
            a.Env("B", b.Url);
            b.Env("A", a.Url);
        });

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.Equal(["r/b"], DependsOn(plan, "a"));
        Assert.Empty(DependsOn(plan, "b"));
    }

    [Fact]
    public void Readiness_checks_and_restart_policies_carry_into_the_plan()
    {
        var f = new Fixture();
        f.Add("../r", repo =>
        {
            repo.DotNet("api", ".").Http(health: "/health").Http("admin", health: "/ready").Restart(RestartPolicy.Always, max: 2);
            repo.Node("web", ".").Http().ReadyOnLog("ready in");
            repo.Job("seed", ".").Retry();
            repo.Node("plain", ".").Http().Restart(RestartPolicy.Never);
        });

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        var api = plan.Apps.Single(a => a.Name == "api");
        Assert.Equal([new PlannedHealth("http", "/health"), new PlannedHealth("admin", "/ready")], api.Health);
        Assert.Equal(("always", 2), (api.Restart!.Policy, api.Restart.Max));
        Assert.Equal("ready in", plan.Apps.Single(a => a.Name == "web").ReadyLog);
        Assert.Equal(("onFailure", 3), (plan.Apps.Single(a => a.Name == "seed").Restart!.Policy, plan.Apps.Single(a => a.Name == "seed").Restart!.Max));
        Assert.Null(plan.Apps.Single(a => a.Name == "plain").Restart);
    }

    [Fact]
    public void Quick_links_resolve_to_each_sprouts_ports_without_making_one_app_wait_for_another()
    {
        var f = new Fixture();
        var services = f.Add("../shop-services", repo =>
        {
            var api = repo.DotNet("catalog-api", ".").Http().Http("admin").Link("Swagger", "/swagger").Link("Admin", "/", endpoint: "admin");
            repo.Export<Url>("catalog-url", api.Url);
        });
        f.Add("../shop-web", repo =>
        {
            var web = repo.Node("web", ".").Http()
                .Link("Storefront")
                .Link("Docs", "https://example.com/docs");
            repo.Node("admin", ".").Http().Link("Web's cart", $"{web.Url}/cart?seed=demo");
        });
        var mail = f.Trellis.Container("mail", "mailpit").Endpoint("ui", 8025, scheme: "http");
        f.Trellis.Link("Mail inbox", mail.UrlOf("ui")).Link("Catalog", $"{services.Export("catalog-url")}/products");

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
        f.Add("../r", repo =>
        {
            var pg = repo.Postgres("pg");
            repo.Node("a", ".").Http().Link("Database", pg.Database("app")).Link("Home");
        });

        var plan = f.Plan();

        var warning = Assert.Single(plan.Problems);
        Assert.Equal(ProblemSeverity.Warning, warning.Severity);
        Assert.Contains("'Database' goes to", warning.Message);
        Assert.Equal(["Home"], plan.Apps.Single().Links!.Select(l => l.Label));
    }

    [Fact]
    public void A_bad_health_path_log_pattern_or_restart_count_is_an_error()
    {
        var f = new Fixture();
        f.Add("../r", repo =>
        {
            repo.Node("a", ".").Http(health: "health");
            repo.Node("b", ".").Http().ReadyOnLog("ready (in");
            repo.Node("c", ".").Http().Restart(max: 0);
        });

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
        f.Add("../r", repo =>
        {
            var a = repo.Node("a", ".").Http();
            var b = repo.Node("b", ".").Http().WaitFor(a);
            a.WaitFor(b);
        });

        Assert.Contains("r/a → r/b → r/a wait for each other", Assert.Single(f.Plan().Problems).Message);
    }

    [Fact]
    public void A_sprout_keeps_its_leased_ports_and_only_new_endpoints_get_new_ones()
    {
        var f = new Fixture();
        f.Add("../r", repo =>
        {
            repo.Node("web", ".").Http();
            repo.Node("admin", ".").Http();
        });
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
        f.Add("../r", repo =>
        {
            repo.Node("web", ".").Http();
            repo.Node("pinned", ".").Http(pin: "9020-9021");
        });
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
        f.Add("../shop-services", repo =>
        {
            var compose = repo.ComposeFile("docker-compose.yml");
            compose.Service("postgres").Endpoint("tcp", 5432);
            compose.Service("redis");
        });
        var info = new ComposeFileInfo(["postgres", "redis", "mailpit", "seq", "tools"], ["postgres"], ["tools"]);

        var plan = f.PlanWith(i => i with { ComposeFiles = new Dictionary<string, ComposeFileInfo> { [ComposeFileInfo.Key("../shop-services", "docker-compose.yml")] = info } });

        var warning = Assert.Single(plan.Problems);
        Assert.Equal(ProblemSeverity.Warning, warning.Severity);
        Assert.Contains("also has 'mailpit', 'seq', which its sprig.cs doesn't name, so sprouts leave them out", warning.Message);
        var compose = Assert.Single(plan.Compose);
        Assert.Equal(["postgres", "redis"], compose.Services.Select(s => s.Name));
        Assert.Contains(compose.Overrides, o => o.Path == "services.postgres.container_name" && o.Generated);
    }

    [Fact]
    public void Naming_a_compose_service_the_file_does_not_have_is_an_error()
    {
        var f = new Fixture();
        f.Add("../r", repo => repo.ComposeFile("compose.yml").Service("postgress").Endpoint("tcp", 5432));
        var info = new ComposeFileInfo(["postgres"], [], []);

        var plan = f.PlanWith(i => i with { ComposeFiles = new Dictionary<string, ComposeFileInfo> { [ComposeFileInfo.Key("../r", "compose.yml")] = info } });

        Assert.Contains(plan.Problems, p => p.Severity == ProblemSeverity.Error && p.Message == "compose.yml has no service named 'postgress'");
    }

    [Fact]
    public void Outputs_are_carried_into_the_plan_and_a_launch_profile_for_a_node_app_is_a_warning()
    {
        var f = new Fixture();
        f.Add("../r", repo =>
        {
            repo.DotNet("api", ".").Http(urlEnv: "ASPNETCORE_URLS").Output(EnvOutput.LaunchSettings());
            repo.Node("web", ".").Http(portEnv: "PORT").Output(EnvOutput.LaunchSettings());
        });

        var plan = f.Plan();

        Assert.Equal("Properties/launchSettings.json", Assert.Single(plan.Apps.Single(a => a.Name == "api").Outputs).File);
        Assert.Contains("'web' isn't a .NET app", Assert.Single(plan.Problems).Message);
    }

    static PlannedEnv Env(Plan plan, string app, string key)
        => plan.Apps.Single(a => a.Name == app).Env.Single(e => e.Key == key);

    static int Port(Plan plan, string resource, string endpoint = "http")
        => plan.Ports.Single(p => p.Resource == resource && p.Endpoint == endpoint).Port!.Value;

    [Fact]
    public void An_import_binds_to_the_one_export_of_that_name_and_says_how_it_got_there()
    {
        var f = new Fixture();
        f.Add("../shop-services", repo =>
        {
            var catalog = repo.DotNet("catalog-api", "src/Catalog.Api").Http(urlEnv: "ASPNETCORE_URLS");
            repo.Export<Url>("catalog-url", catalog.Url);
        });
        f.Add("../shop-web", repo =>
        {
            var catalog = repo.Import<Url>("catalog-url");
            repo.Node("web", ".").Http(pin: "5170-5173").EnvFile(".env.local", e => e.Set("VITE_CATALOG_URL", catalog));
        });

        var plan = f.Plan();

        Assert.False(plan.HasErrors, string.Join("\n", plan.Problems));
        var catalogPort = Port(plan, "catalog-api");
        var env = Env(plan, "web", "VITE_CATALOG_URL");
        Assert.Equal($"http://localhost:{catalogPort}", env.Value.Value);
        Assert.Equal(".env.local", env.File);
        Assert.Equal(["import catalog-url ← shop-services", $"catalog-api.http port {catalogPort}"], env.Value.Via);
        Assert.Equal($"http://localhost:{catalogPort}", Env(plan, "catalog-api", "ASPNETCORE_URLS").Value.Value);
    }

    [Fact]
    public void Two_exporters_of_a_name_is_an_error_until_the_trellis_picks_one()
    {
        var f = new Fixture();
        var a = f.Add("../a", repo => repo.Export<Url>("cache-url", "redis://a"));
        f.Add("../b", repo => repo.Export<Url>("cache-url", "redis://b"));
        f.Add("../web", repo => repo.Node("web", ".").Http().Env("CACHE", repo.Import<Url>("cache-url")));

        var ambiguous = f.Plan();
        var problem = Assert.Single(ambiguous.Problems);
        Assert.Contains("a and b all export 'cache-url'", problem.Message);
        Assert.Contains("trellis.Bind(\"cache-url\"", problem.Message);

        f.Trellis.Bind("cache-url", a.Export("cache-url"));
        var settled = f.Plan();
        Assert.Empty(settled.Problems);
        Assert.Equal("redis://a", Env(settled, "web", "CACHE").Value.Value);
    }

    [Fact]
    public void An_import_nobody_answers_is_reported_where_it_was_imported()
    {
        var f = new Fixture();
        f.Add("../web", repo => repo.Node("web", ".").Http().Env("API", repo.Import<Url>("api-url")));

        var problem = Assert.Single(f.Plan().Problems);

        Assert.Contains("imports 'api-url', but no repo in the trellis exports it", problem.Message);
        Assert.EndsWith("PlannerTests.cs", problem.Location!.File);
        Assert.Null(Env(f.Plan(), "web", "API").Value.Value);
    }

    [Fact]
    public void An_import_of_the_wrong_type_is_refused()
    {
        var f = new Fixture();
        f.Add("../db", repo => repo.Export<ConnectionString>("orders-db", "Host=x"));
        f.Add("../web", repo => repo.Node("web", ".").Http().Env("DB", repo.Import<Url>("orders-db")));

        Assert.Contains("imports 'orders-db' as url, but db exports it as connectionstring", Assert.Single(f.Plan().Problems).Message);
    }

    [Fact]
    public void Infra_the_trellis_owns_can_answer_an_import()
    {
        var f = new Fixture();
        f.Add("../shop-services", repo => repo.DotNet("orders-api", ".").Http().Env("Smtp__Url", repo.Import<Url>("mail-smtp")));
        var mail = f.Trellis.Mailpit("mail");
        f.Trellis.Bind("mail-smtp", mail.SmtpUrl);

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
        f.Add("../shop-services", repo =>
        {
            var pg = repo.Postgres("pg");
            var bus = repo.RabbitMq("bus");
            repo.DotNet("catalog-api", ".").Http()
                .Env("ConnectionStrings__Db", pg.Database("catalog"))
                .Env("Bus__Url", bus.Url);
        });

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
        f.Add("../r", repo =>
        {
            repo.Node("first", ".").Http();
            repo.Node("pinned", ".").Http(pin: "9000");
        });
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
        f.Add("../r", repo => repo.Node("web", ".").Http(pin: "8100"));

        var plan = f.Plan(inUse: new HashSet<int> { 8100 });

        Assert.Contains("every port in the pin \"8100\" is taken", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void Names_are_private_to_their_repo()
    {
        var f = new Fixture();
        f.Add("../a", repo => repo.DotNet("api", ".").Http().Env("Db", $"{repo.Postgres("pg").Port}"));
        f.Add("../b", repo =>
        {
            repo.Postgres("pg");
            repo.Node("web", ".").Http().Env("Api", new Ref("api.http.url"));
        });

        var plan = f.Plan();

        Assert.Equal(2, plan.Containers.Count(c => c.Name == "pg"));
        Assert.NotEqual(plan.Ports.First(p => p.Owner == "a" && p.Resource == "pg").Port, plan.Ports.First(p => p.Owner == "b" && p.Resource == "pg").Port);
        var problem = Assert.Single(plan.Problems);
        Assert.Contains("'api' isn't declared in b; to use something from another repo, import it", problem.Message);
    }

    [Fact]
    public void A_compose_service_gets_its_ports_mapped_and_other_values_set()
    {
        var f = new Fixture();
        f.Add("../shop-services", repo =>
        {
            var compose = repo.ComposeFile("docker-compose.yml");
            var pg = compose.Service("postgres").Endpoint("tcp", 5432);
            compose.Set("services.postgres.container_name", $"shop-pg-{Sprout.Name}");
            repo.DotNet("api", ".").Http().Env("Db", $"Host=localhost;Port={pg.Port}");
        });

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        var overrides = Assert.Single(plan.Compose).Overrides;
        var pgPort = Port(plan, "postgres", "tcp");
        Assert.Equal($"{pgPort}:5432", overrides.Single(o => o.Path == "services.postgres.ports").Value.Value);
        Assert.Equal("shop-pg-feat-x", overrides.Single(o => o.Path == "services.postgres.container_name").Value.Value);
        Assert.Equal($"Host=localhost;Port={pgPort}", Env(plan, "api", "Db").Value.Value);
    }

    [Fact]
    public void Groups_list_their_apps_and_reject_ones_that_do_not_exist()
    {
        var f = new Fixture();
        var services = f.Add("../shop-services", repo => { repo.DotNet("catalog-api", ".").Http(); repo.DotNet("orders-api", ".").Http(); });
        f.Trellis.Group("mobile", services.Only("catalog-api"));
        f.Trellis.Group("all", services);
        f.Trellis.Group("typo", services.Only("catalgo-api"));

        var plan = f.Plan();

        Assert.Equal(["shop-services/catalog-api"], plan.Groups.Single(g => g.Name == "mobile").Apps);
        Assert.Equal(2, plan.Groups.Single(g => g.Name == "all").Apps.Count);
        Assert.Contains("'catalgo-api', which shop-services doesn't declare", Assert.Single(plan.Problems).Message);
    }

    [Fact]
    public void A_binding_nothing_imports_is_a_warning_not_an_error()
    {
        var f = new Fixture();
        f.Add("../r", repo => repo.Node("web", ".").Http());
        f.Trellis.Bind("unused", "x");

        var plan = f.Plan();

        Assert.False(plan.HasErrors);
        Assert.Equal(ProblemSeverity.Warning, Assert.Single(plan.Problems).Severity);
    }

    [Fact]
    public void Waiting_for_something_undeclared_is_an_error()
    {
        var f = new Fixture();
        f.Add("../r", repo =>
        {
            var other = Repo.Describe("elsewhere").Postgres("ghost");
            repo.DotNet("api", ".").Http().WaitFor(other);
        });

        Assert.Contains("waits for 'ghost'", Assert.Single(f.Plan().Problems).Message);
    }

    // ---- wires: where each value comes from, as data ----

    [Fact]
    public void An_import_answered_by_an_export_is_a_wire_to_the_app_behind_it()
    {
        var f = new Fixture();
        f.Add("../shop-services", repo =>
        {
            var pg = repo.Postgres("pg");
            var catalog = repo.DotNet("catalog-api", ".").Http().Env("Db", pg.Database("catalog"));
            repo.DotNet("orders-api", ".").Http().Env("Catalog__Url", catalog.Url);
            repo.Export<Url>("catalog-url", catalog.Url);
        });
        f.Add("../shop-web", repo => repo.Node("web", ".").Http().Env("VITE_CATALOG_URL", repo.Import<Url>("catalog-url")));

        var plan = f.Plan();

        Assert.Empty(plan.Problems);
        Assert.Equal([new ValueSource("shop-services", "catalog-api", "http", "catalog-url")], Env(plan, "web", "VITE_CATALOG_URL").Value.Sources);
        // Values a repo takes from its own resources by name are inside its box, not wires.
        Assert.Equal([new PlannedWire("shop-web", "web", "VITE_CATALOG_URL", "catalog-url", "shop-services", "catalog-api", "http")], plan.Wires());
        Assert.Equal([new ValueSource("shop-services", "catalog-api", "http", null)], Env(plan, "orders-api", "Catalog__Url").Value.Sources);
    }

    [Fact]
    public void A_trellis_binding_is_a_wire_from_the_trellis_and_names_what_it_points_at()
    {
        var f = new Fixture();
        var a = f.Add("../a", repo => repo.Export<Url>("cache-url", "redis://a"));
        f.Add("../b", repo => repo.Export<Url>("cache-url", "redis://b"));
        f.Add("../shop-services", repo => repo.DotNet("orders-api", ".").Http()
            .Env("Smtp__Url", repo.Import<Url>("mail-smtp"))
            .Env("Cache", repo.Import<Url>("cache-url"))
            .Env("Flag", repo.Import<Text>("flag")));
        var mail = f.Trellis.Mailpit("mail");
        f.Trellis.Bind("mail-smtp", mail.SmtpUrl);
        f.Trellis.Bind("cache-url", a.Export("cache-url"));
        f.Trellis.Bind("flag", "on");

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
    public void An_import_nothing_fills_is_a_wire_with_no_provider()
    {
        var f = new Fixture();
        f.Add("../web", repo => repo.Node("web", ".").Http().Env("API", repo.Import<Url>("api-url")));

        var wire = Assert.Single(f.Plan().Wires());

        Assert.False(wire.Filled);
        Assert.Equal(("web", "API", "api-url"), (wire.Consumer, wire.Key, wire.Import));
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
