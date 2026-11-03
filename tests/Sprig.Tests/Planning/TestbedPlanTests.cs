using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Engine.Trellises;

namespace Sprig.Tests.Planning;

/// <summary>Plans the testbed's real definitions (testbed/*.trellis.cs and each repo's sprig.cs), compiled with
/// <c>dotnet run</c>. Only the definition files are copied; the apps themselves aren't needed to plan.</summary>
[Collection("dotnet-run")]
[Trait("Category", "Integration")]
public sealed class TestbedPlanTests : IDisposable
{
    readonly TempStore _store = new();
    readonly string _root;
    readonly FileTrustStore _trust;
    readonly TrellisLoader _loader;

    public TestbedPlanTests()
    {
        _trust = new FileTrustStore(_store.Paths);
        _loader = new TrellisLoader(new DefinitionRunner(_trust, new DotnetRunHost(), _store.Paths,
            new DefinitionRunnerOptions { Timeout = TimeSpan.FromSeconds(120), AllowLocalSdkProject = true }));
        _root = Path.Combine(_store.Root, "testbed");

        var repoRoot = RepoRoot();
        var sdk = Path.Combine(repoRoot, "src", "Sprig.Sdk", "Sprig.Sdk.csproj");
        foreach (var repo in new[] { "shop-services", "shop-web", "shop-mobile" })
            Copy(Path.Combine(repoRoot, "testbed", "repos", repo, "sprig.cs"), Path.Combine(_root, repo, "sprig.cs"), sdk);
        Copy(Path.Combine(repoRoot, "testbed", "storefront.trellis.cs"), TrellisFile, sdk);
    }

    public void Dispose() => _store.Dispose();

    string TrellisFile => Path.Combine(_root, "storefront.trellis.cs");

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
    public void The_storefront_plans_with_no_problems_and_every_import_resolved()
    {
        var loaded = _loader.Load(TrellisFile);
        Assert.True(loaded.Complete, string.Join("\n", loaded.Problems.Select(p => p.Message)));

        var plan = Planner.Build(new PlanInput(loaded.Trellis!, loaded.Repos, "feat-x",
            new SprigSettings { PortRangeStart = 20000, PortRangeEndExclusive = 20100 }, new HashSet<int>()));

        Assert.Empty(plan.Problems);
        var catalogPort = plan.Ports.Single(p => p.Resource == "catalog-api").Port;
        var web = plan.Apps.Single(a => a.Name == "web");
        Assert.Equal($"http://localhost:{catalogPort}", web.Env.Single(e => e.Key == "VITE_CATALOG_URL").Value.Value);
        Assert.InRange(plan.Ports.Single(p => p.Resource == "web").Port!.Value, 5170, 5179);

        var smtpPort = plan.Ports.Single(p => p.Resource == "mail" && p.Endpoint == "smtp").Port;
        var orders = plan.Apps.Single(a => a.Name == "orders-api");
        Assert.Equal($"smtp://localhost:{smtpPort}", orders.Env.Single(e => e.Key == "Smtp__Url").Value.Value);

        Assert.Equal(["shop-mobile/mobile", "shop-services/catalog-api"], plan.Groups.Single(g => g.Name == "mobile").Apps);
        Assert.Equal("main", plan.WorktreesFrom);
    }

    [Fact]
    public void A_compile_error_in_one_repo_is_reported_with_its_file_and_line()
    {
        var web = Path.Combine(_root, "shop-web", "sprig.cs");
        var broken = File.ReadAllText(web).Replace("repo.Import<Url>(\"orders-url\")", "repo.Imprt<Url>(\"orders-url\")");
        File.WriteAllText(web, broken);
        _trust.Approve(web, DefinitionHash.Of(broken));

        var loaded = _loader.Load(TrellisFile);

        var problem = Assert.Single(loaded.Problems);
        Assert.Contains("Imprt", problem.Message);
        Assert.Contains("sprig.cs(", problem.Message);
        Assert.Equal(2, loaded.Repos.Count);
    }
}
