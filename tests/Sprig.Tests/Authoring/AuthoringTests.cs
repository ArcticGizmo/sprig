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
    public void A_trellis_repo_named_by_folder_becomes_one_used_from_the_Repos_list_leaving_the_rest_as_written()
    {
        const string trellis = """
            var trellis = Trellis.Describe("storefront");
            var web = trellis.Repo("../shop-web");          // the front end
            var services = trellis.Repo("../shop-services");
            return trellis.Emit(args);
            """;

        var used = DefinitionEditor.RepoToUseRepo(trellis, "../shop-web", "shop-web");

        Assert.True(used.Ok, used.Error);
        Assert.Contains("var web = trellis.UseRepo(\"shop-web\");          // the front end", used.Text);
        Assert.Contains("trellis.Repo(\"../shop-services\");", used.Text);
        Assert.False(DefinitionEditor.RepoToUseRepo(trellis, "../nope", "x").Ok);
    }

    StoredDefinitions Stored => new(_store.Paths);

    TrellisAuthoring Authoring(ITrustStore trust) => new(Stored, new TrellisRegistry(_store.Paths), trust);

    static bool Approved(ITrustStore trust, string file) => trust.Check(file, DefinitionHash.Of(File.ReadAllText(file))).State == TrustState.Trusted;

    [Fact]
    public void A_new_trellis_uses_each_folders_own_sprig_cs_or_drafts_one_in_sprig_and_reuses_entries()
    {
        var trust = new FileTrustStore(_store.Paths);
        var authoring = Authoring(trust);
        Write("shop-web/sprig.cs", "// shop-web's own");

        var entry = authoring.CreateTrellis("storefront", [Dir("shop-web"), Dir("shop-services")], "main", "Stable");
        var text = File.ReadAllText(entry.Path);

        Assert.Contains("var shopWeb = trellis.UseRepo(\"shop-web\");", text);
        Assert.Contains("var shopServices = trellis.UseRepo(\"shop-services\");", text);
        Assert.True(Stored.Repo("shop-web")!.InRepo);
        Assert.Equal(Path.Combine(Dir("shop-web"), "sprig.cs"), Stored.Repo("shop-web")!.DefinitionFile);
        var services = Stored.Repo("shop-services")!;
        Assert.False(services.InRepo);
        Assert.True(Approved(trust, services.DefinitionFile));
        Assert.False(File.Exists(Path.Combine(Dir("shop-services"), "sprig.cs")));

        var second = authoring.CreateTrellis("storefront", [Dir("shop-web"), Dir("shop-services")], null, "Stable");
        Assert.Equal("storefront-2", second.Id);
        Assert.Equal(2, Stored.Repos().Count);
        Assert.Equal(["storefront", "storefront-2"], authoring.UsedBy("shop-web"));

        authoring.RemoveTrellis(entry.Id);
        Assert.False(File.Exists(entry.Path));
        Assert.Equal(["storefront-2"], new TrellisRegistry(_store.Paths).List().Select(e => e.Id));
        Assert.Equal(2, Stored.Repos().Count);

        // Taking a repo off the list leaves the folder's own file where it is.
        authoring.DeleteRepo("shop-web");
        Assert.Null(Stored.Repo("shop-web"));
        Assert.True(File.Exists(Path.Combine(Dir("shop-web"), "sprig.cs")));
    }

    [Fact]
    public void Keeping_a_repo_in_sprig_copies_its_own_file_with_the_approval_it_had_and_switching_back_keeps_the_copy()
    {
        var trust = new FileTrustStore(_store.Paths);
        var authoring = Authoring(trust);
        Write("shop-web/sprig.cs", "// shop-web's own");
        var own = Path.Combine(Dir("shop-web"), "sprig.cs");
        authoring.AddRepo(Dir("shop-web"), "web");

        var inSprig = authoring.KeepInSprig("web");

        Assert.False(inSprig.InRepo);
        Assert.Equal(Stored.CopyFile("web"), inSprig.DefinitionFile);
        Assert.Equal("// shop-web's own", File.ReadAllText(inSprig.DefinitionFile));
        // The repo's own file was never approved, so neither is its copy.
        Assert.False(Approved(trust, inSprig.DefinitionFile));

        File.WriteAllText(inSprig.DefinitionFile, "// tried in sprig");
        var back = authoring.PutInRepo("web");
        Assert.True(back.InRepo);
        Assert.Equal(own, back.DefinitionFile);
        Assert.Equal("// shop-web's own", File.ReadAllText(own));

        // Switching again restores the copy rather than copying over it.
        Assert.Equal("// tried in sprig", File.ReadAllText(authoring.KeepInSprig("web").DefinitionFile));

        // With an approved own file and no copy yet, the copy is approved too.
        trust.Approve(own, DefinitionHash.Of("// shop-web's own"));
        authoring.AddRepo(Dir("shop-web"), "web-2");
        Assert.True(Approved(trust, authoring.KeepInSprig("web-2").DefinitionFile));
    }

    [Fact]
    public void Putting_a_repo_kept_in_sprig_into_a_folder_with_no_sprig_cs_writes_its_copy_there()
    {
        var trust = new FileTrustStore(_store.Paths);
        var authoring = Authoring(trust);
        var entry = authoring.AddRepo(Dir("shop-services"), "services");
        Assert.False(entry.InRepo);
        var draft = File.ReadAllText(entry.DefinitionFile);

        var inRepo = authoring.PutInRepo("services");

        var own = Path.Combine(Dir("shop-services"), "sprig.cs");
        Assert.Equal(own, inRepo.DefinitionFile);
        Assert.Equal(draft, File.ReadAllText(own));
        Assert.True(Approved(trust, own));
        Assert.True(inRepo.HasCopy);
    }

    [Fact]
    public void A_repo_named_by_folder_moves_onto_the_Repos_list_reusing_the_folders_entry()
    {
        var trust = new FileTrustStore(_store.Paths);
        var authoring = Authoring(trust);
        Write("shop-web/sprig.cs", "// shop-web's own");
        authoring.AddRepo(Dir("shop-web"), "web");
        var trellisFile = Path.Combine(_repos, "storefront.trellis.cs");
        File.WriteAllText(trellisFile, "var trellis = Trellis.Describe(\"storefront\");\nvar web = trellis.Repo(\"shop-web\");\nreturn trellis.Emit(args);\n");
        trust.Approve(trellisFile, DefinitionHash.Of(File.ReadAllText(trellisFile)));

        var entry = authoring.UseFromRepos(trellisFile, "shop-web");

        Assert.Equal("web", entry.Id);
        Assert.Contains("var web = trellis.UseRepo(\"web\");", File.ReadAllText(trellisFile));
        Assert.True(Approved(trust, trellisFile));
        Assert.Single(Stored.Repos());
    }

    /// <summary>The drafts are only useful if they run, so this one compiles them with the real SDK.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void A_trellis_made_in_the_app_over_repos_with_nothing_committed_plans()
    {
        var trust = new FileTrustStore(_store.Paths);
        var stored = new StoredDefinitions(_store.Paths);
        var registry = new TrellisRegistry(_store.Paths);
        var authoring = new TrellisAuthoring(stored, registry, trust);
        var loader = new TrellisLoader(new DefinitionRunner(trust, new DotnetRunHost(), _store.Paths,
            new DefinitionRunnerOptions { Timeout = TimeSpan.FromSeconds(120), AllowLocalSdkProject = true }), registry, stored);

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
