using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Engine.Orchestrations;

namespace Sprig.Tests.Planning;

/// <summary>Plans the testbed's real definitions (testbed/*.orchestration.yaml and each repo's sprig.yaml). Only the
/// definition files are copied; the apps themselves aren't needed to plan.</summary>
public sealed class TestbedPlanTests : IDisposable
{
    readonly TempStore _store = new();
    readonly string _root;
    readonly FileTrustStore _trust;
    readonly OrchestrationLoader _loader;

    public TestbedPlanTests()
    {
        _trust = new FileTrustStore(_store.Paths);
        _loader = new OrchestrationLoader(new DefinitionRunner(_trust, _store.Paths));
        _root = Path.Combine(_store.Root, "testbed");

        var repoRoot = RepoRoot();
        foreach (var repo in new[] { "shop-services", "shop-web", "shop-mobile" })
            Copy(File.ReadAllText(Path.Combine(repoRoot, "testbed", "repos", repo, "sprig.yaml")), Path.Combine(_root, repo, "sprig.yaml"));
        Copy(File.ReadAllText(Path.Combine(repoRoot, "testbed", "storefront.orchestration.yaml")), OrchestrationFile);
    }

    public void Dispose() => _store.Dispose();

    string OrchestrationFile => Path.Combine(_root, "storefront.orchestration.yaml");

    void Copy(string text, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.WriteAllText(to, text);
        _trust.Approve(to, DefinitionHash.Of(text));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sprig.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void The_storefront_plans_with_no_problems_and_every_setting_resolved()
    {
        var loaded = _loader.Load(OrchestrationFile);
        Assert.True(loaded.Complete, string.Join("\n", loaded.Problems.Select(p => p.Message)));

        var plan = Planner.Build(new PlanInput(loaded.Orchestration!, loaded.Repos, "feat-x",
            new SprigSettings { PortRangeStart = 20000, PortRangeEndExclusive = 20100 }, new HashSet<int>()));

        Assert.Empty(plan.Problems);
        var catalogPort = plan.Ports.Single(p => p.Resource == "catalog-api").Port;
        var web = plan.Apps.Single(a => a.Name == "web");
        Assert.Equal($"http://localhost:{catalogPort}", web.Env.Single(e => e.Key == "VITE_CATALOG_URL").Value.Value);
        Assert.InRange(plan.Ports.Single(p => p.Resource == "web").Port!.Value, 9170, 9179);

        var smtpPort = plan.Ports.Single(p => p.Resource == "mail" && p.Endpoint == "smtp").Port;
        var orders = plan.Apps.Single(a => a.Name == "orders-api");
        Assert.Equal($"smtp://localhost:{smtpPort}", orders.Env.Single(e => e.Key == "Smtp:Url").Value.Value);

        Assert.Equal(["shop-mobile/mobile", "shop-services/catalog-api"], plan.Groups.Single(g => g.Name == "mobile").Apps);

        var catalog = plan.Apps.Single(a => a.Name == "catalog-api");
        Assert.Equal(new PlannedHealth("http", "/health"), Assert.Single(catalog.Health!));
        Assert.Equal(("onFailure", 5), (catalog.Restart!.Policy, catalog.Restart.Max));
        Assert.Equal(3, plan.Apps.Single(a => a.Name == "migrate").Restart!.Max);
        Assert.Equal(@"ready in \d+", plan.Apps.Single(a => a.Name == "mobile").ReadyLog);
        Assert.Empty(plan.ToAsk);
        // The orchestration's Mailpit gives mail-smtp, so shop-services' own isn't started.
        Assert.DoesNotContain(plan.Containers, c => c.Owner == "shop-services");

        // A group starts only the infra its apps use: the mobile app's catalog api sends no mail; the web group's orders api does.
        Assert.Equal(["shop-services/postgres", "shop-services/rabbitmq", "shop-services/redis"],
            Sprig.Engine.Workspaces.WorkspaceService.InfraFor(plan, new Sprig.Engine.Workspaces.Starting("mobile").AppsIn(plan))!.Order());
        Assert.Contains("storefront/mail", Sprig.Engine.Workspaces.WorkspaceService.InfraFor(plan, new Sprig.Engine.Workspaces.Starting("web").AppsIn(plan))!);
    }

    [Fact]
    public void Shop_web_runs_on_its_own_against_the_apis_by_their_usual_ports()
    {
        var file = Path.Combine(_root, "web-only.orchestration.yaml");
        Copy("schema: 1\norchestration: web-only\nrepos:\n  shop-web: { path: shop-web }\n", file);

        var loaded = _loader.Load(file);
        Assert.True(loaded.Complete, string.Join("\n", loaded.Problems.Select(p => p.Message)));
        var plan = Planner.Build(new PlanInput(loaded.Orchestration!, loaded.Repos, "local",
            new SprigSettings { PortRangeStart = 20000, PortRangeEndExclusive = 20100 }, new HashSet<int>()));

        Assert.Empty(plan.Problems);
        Assert.Empty(plan.ToAsk);
        var web = plan.Apps.Single();
        Assert.Equal("http://localhost:5100", web.Env.Single(e => e.Key == "VITE_CATALOG_URL").Value.Value);
        Assert.Equal("http://localhost:5200", web.Env.Single(e => e.Key == "VITE_ORDERS_URL").Value.Value);
    }

    [Fact]
    public void A_mistake_in_one_repo_is_reported_with_its_file_and_line_and_the_others_still_load()
    {
        var web = Path.Combine(_root, "shop-web", "sprig.yaml");
        var broken = File.ReadAllText(web).Replace("${inputs.orders-url}", "${inputs.order-url}");
        Copy(broken, web);

        var loaded = _loader.Load(OrchestrationFile);

        var problem = Assert.Single(loaded.Problems);
        Assert.Contains("there's no input 'order-url'", problem.Message);
        Assert.Equal(web, problem.Location!.File);
        Assert.Equal(broken.Split('\n').ToList().FindIndex(l => l.Contains("order-url}", StringComparison.Ordinal)) + 1, problem.Location.Line);
        Assert.Equal(2, loaded.Repos.Count);
    }
}
