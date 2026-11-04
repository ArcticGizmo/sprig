using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sprig.Engine.Authoring;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Engine.Store;
using Sprig.Engine.Trellises;
using Sprig.Model;

namespace Sprig.Tests.Authoring;

public sealed class AuthoringTests : IDisposable
{
    readonly TempStore _store = new();
    readonly string _repos;

    public AuthoringTests()
    {
        _repos = LongPath.Of(Path.Combine(_store.Root, "src"));
        Write("shop-web/package.json", """{ "name": "@shop/web", "scripts": { "dev": "vite" }, "devDependencies": { "vite": "6.0.0" } }""");
        Write("shop-services/src/Catalog.Api/Catalog.Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("shop-services/src/Catalog.Api.Tests/Catalog.Api.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write("shop-services/tools/Migrate/Migrate.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        Write("shop-services/tools/Migrate/Program.cs", "// migrate");
        Write("shop-services/node_modules/x/package.json", """{ "scripts": { "dev": "x" } }""");
        Write("shop-services/docker-compose.yml", "services: {}");
    }

    public void Dispose() => _store.Dispose();

    string Dir(string repo) => Path.Combine(_repos, repo);

    void Write(string relative, string text)
    {
        var path = Path.Combine(_repos, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    static void Parses(string text)
        => Assert.DoesNotContain(CSharpSyntaxTree.ParseText(text, DefinitionEditor.ParseOptions).GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);

    [Fact]
    public void A_repo_draft_describes_the_web_apps_it_finds_and_leaves_the_rest_as_comments()
    {
        var services = Scaffold.Repo(Dir("shop-services"), "shop-services");
        var web = Scaffold.Repo(Dir("shop-web"), "shop-web");

        Assert.Contains("var catalogApi = repo.DotNet(\"catalog-api\", \"src/Catalog.Api\")", services);
        Assert.DoesNotContain("tests", services, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("// repo.DotNetJob(\"migrate\", \"tools/Migrate\");", services);
        Assert.Contains("// repo.ComposeFile(\"docker-compose.yml\")", services);
        Assert.DoesNotContain("node_modules", services);
        Assert.Contains("var web = repo.Node(\"web\", \".\").Http();", web);
        Assert.Contains("web.Run($\"npm run dev -- --port {web.Port}\");", web);
        Assert.StartsWith("#:", services);
        Parses(services);
        Parses(web);
    }

    [Fact]
    public void A_trellis_points_a_repo_at_a_definition_in_sprig_and_back_leaving_the_rest_as_written()
    {
        const string trellis = """
            var trellis = Trellis.Describe("storefront");
            var web = trellis.Repo("../shop-web");          // the front end
            var services = trellis.Repo("../shop-services");
            return trellis.Emit(args);
            """;

        var pointed = DefinitionEditor.SetRepoDefinition(trellis, "../shop-web", "web-local");
        Assert.True(pointed.Ok, pointed.Error);
        Assert.Contains("trellis.Repo(\"../shop-web\", definition: \"web-local\");          // the front end", pointed.Text);
        Assert.Contains("trellis.Repo(\"../shop-services\");", pointed.Text);

        var renamed = DefinitionEditor.SetRepoDefinition(pointed.Text, "../shop-web", "web-other");
        Assert.Contains("definition: \"web-other\"", renamed.Text);

        var back = DefinitionEditor.SetRepoDefinition(renamed.Text, "../shop-web", null);
        Assert.Equal(trellis, back.Text);

        Assert.False(DefinitionEditor.SetRepoDefinition(trellis, "../nope", "x").Ok);
    }

    [Fact]
    public void Copying_a_repos_own_definition_into_sprig_keeps_approval_only_if_it_had_it()
    {
        var trust = new FileTrustStore(_store.Paths);
        var virtuals = new VirtualDefinitions(_store.Paths);
        var authoring = new TrellisAuthoring(virtuals, new TrellisRegistry(_store.Paths), trust);
        Write("shop-web/sprig.cs", "// shop-web's own");
        var entry = authoring.CreateTrellis("storefront", [Dir("shop-web"), Dir("shop-services")], "main", "Stable");
        var trellisText = File.ReadAllText(entry.Path);

        Assert.Contains($"trellis.Repo(\"{Dir("shop-web").Replace('\\', '/')}\");", trellisText);
        Assert.Contains($"trellis.Repo(\"{Dir("shop-services").Replace('\\', '/')}\", definition: \"shop-services\");", trellisText);
        Assert.Equal(["shop-services"], virtuals.RepoIds());
        Assert.Equal(TrustState.Trusted, trust.Check(virtuals.RepoFile("shop-services"), DefinitionHash.Of(File.ReadAllText(virtuals.RepoFile("shop-services")))).State);

        var copy = authoring.DescribeInSprig(entry.Path, Dir("shop-web").Replace('\\', '/'), Dir("shop-web"));

        Assert.Equal("// shop-web's own", File.ReadAllText(copy));
        Assert.NotEqual(TrustState.Trusted, trust.Check(copy, DefinitionHash.Of("// shop-web's own")).State);
        Assert.Contains("definition: \"shop-web\"", File.ReadAllText(entry.Path));
        Assert.Equal(TrustState.Trusted, trust.Check(entry.Path, DefinitionHash.Of(File.ReadAllText(entry.Path))).State);

        authoring.UseOwnDefinition(entry.Path, Dir("shop-web").Replace('\\', '/'));
        Assert.DoesNotContain("definition: \"shop-web\"", File.ReadAllText(entry.Path));
        Assert.True(File.Exists(copy));

        authoring.RemoveTrellis(entry.Id);
        Assert.False(File.Exists(entry.Path));
        Assert.Empty(new TrellisRegistry(_store.Paths).List());
        Assert.Equal(["shop-services", "shop-web"], virtuals.RepoIds());
    }

    /// <summary>The drafts are only useful if they run, so this one compiles them with the real SDK.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void A_trellis_made_in_the_app_over_repos_with_nothing_committed_plans()
    {
        var trust = new FileTrustStore(_store.Paths);
        var virtuals = new VirtualDefinitions(_store.Paths);
        var registry = new TrellisRegistry(_store.Paths);
        var authoring = new TrellisAuthoring(virtuals, registry, trust);
        var loader = new TrellisLoader(new DefinitionRunner(trust, new DotnetRunHost(), _store.Paths,
            new DefinitionRunnerOptions { Timeout = TimeSpan.FromSeconds(120), AllowLocalSdkProject = true }), registry, virtuals);

        var entry = authoring.CreateTrellis("trial run", [Dir("shop-web"), Dir("shop-services")], null, "Stable");
        var loaded = loader.Load(entry.Path);

        Assert.True(loaded.Complete, string.Join("\n", loaded.Problems.Select(p => p.Message)));
        Assert.Equal("trial-run", loaded.Id);
        var plan = Planner.Build(new PlanInput(loaded.Trellis!, loaded.Repos, "plan", new SprigSettings(), new HashSet<int>()));
        Assert.False(plan.HasErrors, string.Join("\n", plan.Problems.Select(p => p.Message)));
        Assert.Equal(["shop-services/catalog-api", "shop-web/web"], plan.Apps.Select(a => $"{a.Owner}/{a.Name}").Order());
        Assert.False(File.Exists(Path.Combine(Dir("shop-web"), "sprig.cs")));
    }
}
