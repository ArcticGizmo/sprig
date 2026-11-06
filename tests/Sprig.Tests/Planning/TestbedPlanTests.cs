using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Engine.Orchestrations;

namespace Sprig.Tests.Planning;

/// <summary>Plans the testbed's real definitions (testbed/*.orchestration.cs and each repo's sprig.cs), compiled with
/// <c>dotnet run</c>. Only the definition files are copied; the apps themselves aren't needed to plan.</summary>
[Collection("dotnet-run")]
[Trait("Category", "Integration")]
public sealed class TestbedPlanTests : IDisposable
{
    readonly TempStore _store = new();
    readonly string _root;
    readonly FileTrustStore _trust;
    readonly OrchestrationLoader _loader;

    public TestbedPlanTests()
    {
        _trust = new FileTrustStore(_store.Paths);
        _loader = new OrchestrationLoader(new DefinitionRunner(_trust, new DotnetRunHost(), _store.Paths,
            new DefinitionRunnerOptions { Timeout = TimeSpan.FromSeconds(120), AllowLocalSdkProject = true }));
        _root = Path.Combine(_store.Root, "testbed");

        var repoRoot = RepoRoot();
        var sdk = Path.Combine(repoRoot, "src", "Sprig.Sdk", "Sprig.Sdk.csproj");
        foreach (var repo in new[] { "shop-services", "shop-web", "shop-mobile" })
            Copy(Path.Combine(repoRoot, "testbed", "repos", repo, "sprig.cs"), Path.Combine(_root, repo, "sprig.cs"), sdk);
        Copy(Path.Combine(repoRoot, "testbed", "storefront.orchestration.cs"), OrchestrationFile, sdk);
    }

    public void Dispose() => _store.Dispose();

    string OrchestrationFile => Path.Combine(_root, "storefront.orchestration.cs");

    void Copy(string from, string to, string sdk)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        var text = File.ReadAllText(from).Replace("#:package Sprig.Sdk@0.1.0-exp", $"#:project {sdk}");
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
        Assert.Equal($"smtp://localhost:{smtpPort}", orders.Env.Single(e => e.Key == "Smtp__Url").Value.Value);

        Assert.Equal(["shop-mobile/mobile", "shop-services/catalog-api"], plan.Groups.Single(g => g.Name == "mobile").Apps);

        var catalog = plan.Apps.Single(a => a.Name == "catalog-api");
        Assert.Equal(new PlannedHealth("http", "/health"), Assert.Single(catalog.Health!));
        Assert.Equal(("onFailure", 5), (catalog.Restart!.Policy, catalog.Restart.Max));
        Assert.Equal(3, plan.Apps.Single(a => a.Name == "migrate").Restart!.Max);
        Assert.Equal(@"ready in \d+", plan.Apps.Single(a => a.Name == "mobile").ReadyLog);
        Assert.Empty(plan.ToAsk);
        // The orchestration's Mailpit gives mail-smtp, so shop-services' own isn't started.
        Assert.DoesNotContain(plan.Containers, c => c.Owner == "shop-services");
    }

    [Fact]
    public void Shop_web_runs_on_its_own_against_the_apis_by_their_usual_ports()
    {
        var file = Path.Combine(_root, "web-only.orchestration.cs");
        Copy(WriteOrchestration(file, "shop-web"), file, Path.Combine(RepoRoot(), "src", "Sprig.Sdk", "Sprig.Sdk.csproj"));

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

    static string WriteOrchestration(string file, string repo)
    {
        var source = file + ".src";
        File.WriteAllText(source, $"""
            #:package Sprig.Sdk@0.1.0-exp
            using Sprig;

            var orchestration = Orchestration.Describe("{Path.GetFileName(file).Split('.')[0]}");
            orchestration.Repo("{repo}");
            return orchestration.Emit(args);
            """);
        return source;
    }

    [Fact]
    public void A_compile_error_in_one_repo_is_reported_with_its_file_and_line()
    {
        var web = Path.Combine(_root, "shop-web", "sprig.cs");
        var broken = File.ReadAllText(web).Replace("repo.Setting<Url>(\"orders-url\"", "repo.Imprt<Url>(\"orders-url\"");
        File.WriteAllText(web, broken);
        _trust.Approve(web, DefinitionHash.Of(broken));

        var loaded = _loader.Load(OrchestrationFile);

        var problem = Assert.Single(loaded.Problems);
        Assert.Contains("Imprt", problem.Message);
        Assert.Contains("sprig.cs(", problem.Message);
        Assert.Equal(2, loaded.Repos.Count);
    }
}
