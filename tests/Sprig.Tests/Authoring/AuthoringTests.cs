using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sprig.Engine.Authoring;
using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Engine.Store;
using Sprig.Engine.Orchestrations;
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

        Assert.Contains("var catalogApi = repo.DotNet(\"catalog-api\", \"src/Catalog.Api\").Http();", services);
        Assert.Contains("catalogApi.LaunchSettings(s => s.Set(\"Urls\", catalogApi.Url));", services);
        Assert.DoesNotContain("tests", services, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("// repo.DotNetJob(\"migrate\", \"tools/Migrate\");", services);
        Assert.Contains("// repo.ComposeFile(\"docker-compose.yml\")", services);
        Assert.DoesNotContain("node_modules", services);
        // Every value goes in a file the app reads, and the command stays one you could type.
        Assert.Contains("var web = repo.Node(\"web\", \".\").Http();", web);
        Assert.Contains("web.EnvFile(\".env.local\", e => e.Set(\"PORT\", web.Port));", web);
        Assert.Contains("Vite doesn't read PORT by itself", web);
        Assert.DoesNotContain(".Run(", web);
        Write("shop-services/src/Catalog.Api/Properties/launchSettings.json", "{}");
        Assert.Contains("catalogApi.AppSettings(\"appsettings.Local.json\", s => s.Set(\"Urls\", catalogApi.Url));",
            Scaffold.Repo(Dir("shop-services"), "shop-services"));
        Assert.StartsWith("#:", services);
        Parses(services);
        Parses(web);
    }

    [Fact]
    public void A_repo_draft_leaves_out_a_workspace_s_root_and_describes_its_apps()
    {
        Write("shop-front/package.json", """{ "name": "shop-front", "scripts": { "dev": "turbo run dev" } }""");
        Write("shop-front/pnpm-workspace.yaml", "packages: [apps/*]");
        Write("shop-front/apps/web/package.json", """{ "name": "@shop/web", "scripts": { "dev": "vite" } }""");
        Write("shop-npm/package.json", """{ "name": "shop-npm", "workspaces": ["admin"], "scripts": { "dev": "npm run dev -ws" } }""");
        Write("shop-npm/admin/package.json", """{ "name": "admin", "scripts": { "dev": "vite" } }""");

        var pnpm = Scaffold.Repo(Dir("shop-front"), "shop-front");
        var npm = Scaffold.Repo(Dir("shop-npm"), "shop-npm");

        Assert.Contains("repo.Node(\"web\", \"apps/web\")", pnpm);
        Assert.DoesNotContain("\"shop-front\", \".\"", pnpm);
        Assert.Contains("repo.Node(\"admin\", \"admin\")", npm);
        Assert.DoesNotContain("\"shop-npm\", \".\"", npm);
    }

    [Fact]
    public void An_orchestration_repo_named_by_folder_becomes_one_used_from_the_Repos_list_leaving_the_rest_as_written()
    {
        const string orchestration = """
            var orchestration = Orchestration.Describe("storefront");
            var web = orchestration.Repo("../shop-web");          // the front end
            var services = orchestration.Repo("../shop-services");
            return orchestration.Emit(args);
            """;

        var used = DefinitionEditor.RepoToUseRepo(orchestration, "../shop-web", "shop-web");

        Assert.True(used.Ok, used.Error);
        Assert.Contains("var web = orchestration.UseRepo(\"shop-web\");          // the front end", used.Text);
        Assert.Contains("orchestration.Repo(\"../shop-services\");", used.Text);
        Assert.False(DefinitionEditor.RepoToUseRepo(orchestration, "../nope", "x").Ok);
    }

    StoredDefinitions Stored => new(_store.Paths);

    OrchestrationAuthoring Authoring(ITrustStore trust) => new(Stored, new OrchestrationRegistry(_store.Paths), trust);

    static bool Approved(ITrustStore trust, string file) => trust.Check(file, DefinitionHash.Of(File.ReadAllText(file))).State == TrustState.Trusted;

    [Fact]
    public void A_new_orchestration_uses_each_folders_own_sprig_cs_or_drafts_one_in_sprig_and_reuses_entries()
    {
        var trust = new FileTrustStore(_store.Paths);
        var authoring = Authoring(trust);
        Write("shop-web/sprig.cs", "// shop-web's own");

        var entry = authoring.CreateOrchestration("storefront", [Dir("shop-web"), Dir("shop-services")]);
        var text = File.ReadAllText(entry.Path);

        Assert.Contains("var shopWeb = orchestration.UseRepo(\"shop-web\");", text);
        Assert.Contains("var shopServices = orchestration.UseRepo(\"shop-services\");", text);
        Assert.True(Stored.Repo("shop-web")!.InRepo);
        Assert.Equal(Path.Combine(Dir("shop-web"), "sprig.cs"), Stored.Repo("shop-web")!.DefinitionFile);
        var services = Stored.Repo("shop-services")!;
        Assert.False(services.InRepo);
        Assert.True(Approved(trust, services.DefinitionFile));
        Assert.False(File.Exists(Path.Combine(Dir("shop-services"), "sprig.cs")));

        var second = authoring.CreateOrchestration("storefront", [Dir("shop-web"), Dir("shop-services")]);
        Assert.Equal("storefront-2", second.Id);
        Assert.Equal(2, Stored.Repos().Count);
        Assert.Equal(["storefront", "storefront-2"], authoring.UsedBy("shop-web"));

        authoring.RemoveOrchestration(entry.Id);
        Assert.False(File.Exists(entry.Path));
        Assert.Equal(["storefront-2"], new OrchestrationRegistry(_store.Paths).List().Select(e => e.Id));
        Assert.Equal(2, Stored.Repos().Count);

        // Taking a repo off the list leaves the folder's own file where it is.
        authoring.DeleteRepo("shop-web");
        Assert.Null(Stored.Repo("shop-web"));
        Assert.True(File.Exists(Path.Combine(Dir("shop-web"), "sprig.cs")));
    }

    [Fact]
    public void Copying_a_repo_into_sprig_makes_a_new_entry_with_the_approval_it_had_and_leaves_the_first_as_it_was()
    {
        var trust = new FileTrustStore(_store.Paths);
        var authoring = Authoring(trust);
        Write("shop-web/sprig.cs", "// shop-web's own");
        var own = Path.Combine(Dir("shop-web"), "sprig.cs");
        var web = authoring.AddRepo(Dir("shop-web"), "web");
        Assert.Equal("shop-web-sprig", authoring.SuggestId(web, inRepo: false));

        var copy = authoring.CopyIntoSprig("web", "web-sprig");

        Assert.False(copy.InRepo);
        Assert.Equal(Stored.CopyFile("web-sprig"), copy.DefinitionFile);
        Assert.Equal("// shop-web's own", File.ReadAllText(copy.DefinitionFile));
        // The repo's own file was never approved, so neither is its copy.
        Assert.False(Approved(trust, copy.DefinitionFile));
        Assert.Equal(own, Stored.Repo("web")!.DefinitionFile);
        Assert.True(Stored.Repo("web")!.InRepo);
        Assert.False(File.Exists(Stored.CopyFile("web")));

        // With an approved own file, the copy is approved too.
        trust.Approve(own, DefinitionHash.Of("// shop-web's own"));
        Assert.True(Approved(trust, authoring.CopyIntoSprig("web", "web-sprig-2").DefinitionFile));

        // A new orchestration over the folder reuses the entry that uses its own file, and the folder's file is only listed once.
        Assert.Equal("web", Stored.RepoFor(Dir("shop-web"))!.Id);
        Assert.Throws<ArgumentException>(() => authoring.AddRepo(Dir("shop-web"), "web-again"));
        Assert.Throws<ArgumentException>(() => authoring.CopyIntoSprig("web-sprig", "x"));
    }

    [Fact]
    public void A_repo_added_as_a_named_kind_is_that_kind_and_one_kept_in_sprig_starts_from_the_folders_file()
    {
        var trust = new FileTrustStore(_store.Paths);
        var authoring = Authoring(trust);
        var own = Path.Combine(Dir("shop-web"), "sprig.cs");

        Assert.Throws<ArgumentException>(() => authoring.AddRepo(Dir("shop-web"), "web", inRepo: true));
        var drafted = authoring.AddRepo(Dir("shop-web"), inRepo: false);
        Assert.Equal("shop-web", drafted.Id);
        Assert.Contains("repo.Node(\"web\"", File.ReadAllText(drafted.DefinitionFile));
        Assert.True(Approved(trust, drafted.DefinitionFile));

        Write("shop-web/sprig.cs", "// shop-web's own");
        trust.Approve(own, DefinitionHash.Of("// shop-web's own"));
        Assert.Equal("shop-web-sprig", authoring.SuggestNewId(Dir("shop-web"), inRepo: false));
        var copy = authoring.AddRepo(Dir("shop-web"), inRepo: false);
        Assert.Equal("shop-web-sprig", copy.Id);
        Assert.False(copy.InRepo);
        Assert.Equal("// shop-web's own", File.ReadAllText(copy.DefinitionFile));
        Assert.True(Approved(trust, copy.DefinitionFile));

        Assert.True(authoring.AddRepo(Dir("shop-web"), "web", inRepo: true).InRepo);
        Assert.Equal(own, Stored.Repo("web")!.DefinitionFile);
    }

    [Fact]
    public void Writing_a_repo_kept_in_sprig_to_its_folder_adds_a_separate_entry_and_never_writes_over_a_sprig_cs()
    {
        var trust = new FileTrustStore(_store.Paths);
        var authoring = Authoring(trust);
        var entry = authoring.AddRepo(Dir("shop-services"), "services-sprig");
        Assert.False(entry.InRepo);
        var draft = File.ReadAllText(entry.DefinitionFile);
        Assert.Null(authoring.WriteToRepoBlocker(entry));
        Assert.Equal("shop-services", authoring.SuggestId(entry, inRepo: true));

        var inRepo = authoring.WriteToRepo("services-sprig", "services");

        var own = Path.Combine(Dir("shop-services"), "sprig.cs");
        Assert.Equal("services", inRepo.Id);
        Assert.True(inRepo.InRepo);
        Assert.Equal(own, inRepo.DefinitionFile);
        Assert.Equal(draft, File.ReadAllText(own));
        Assert.True(Approved(trust, own));
        // The one in sprig is still its own entry, reading its own file.
        var still = Stored.Repo("services-sprig")!;
        Assert.False(still.InRepo);
        Assert.Equal(Stored.CopyFile("services-sprig"), still.DefinitionFile);

        // Once the folder has a sprig.cs, writing again is refused and the file is left alone.
        File.WriteAllText(still.DefinitionFile, "// changed in sprig");
        Assert.Contains("'services'", authoring.WriteToRepoBlocker(still));
        Assert.Throws<IOException>(() => authoring.WriteToRepo("services-sprig", "services-2"));
        Assert.Equal(draft, File.ReadAllText(own));
        Assert.Throws<ArgumentException>(() => authoring.WriteToRepo("services", "x"));
    }

    [Fact]
    public void A_repo_kept_in_sprig_renames_with_its_definition_and_approval_but_not_while_an_orchestration_uses_it()
    {
        var trust = new FileTrustStore(_store.Paths);
        var authoring = Authoring(trust);
        var entry = authoring.AddRepo(Dir("shop-services"), "services-sprig");
        var draft = File.ReadAllText(entry.DefinitionFile);

        var renamed = authoring.RenameRepo("services-sprig", "catalog");

        Assert.Equal("catalog", renamed.Id);
        Assert.False(renamed.InRepo);
        Assert.Equal(entry.Folder, renamed.Folder);
        Assert.Equal(Stored.CopyFile("catalog"), renamed.DefinitionFile);
        Assert.Equal(draft, File.ReadAllText(renamed.DefinitionFile));
        Assert.True(Approved(trust, renamed.DefinitionFile));
        Assert.Null(Stored.Repo("services-sprig"));

        // Case alone can change; another entry's ID can't be taken.
        Assert.Equal("Catalog", authoring.RenameRepo("catalog", "Catalog").Id);
        Write("shop-web/sprig.cs", "// shop-web's own");
        authoring.AddRepo(Dir("shop-web"), "web");
        Assert.Throws<ArgumentException>(() => authoring.RenameRepo("Catalog", "WEB"));
        Assert.Throws<ArgumentException>(() => authoring.RenameRepo("Catalog", "not an id"));
        // Only one kept in sprig.
        Assert.NotNull(authoring.RenameBlocker(Stored.Repo("web")!));
        Assert.Throws<ArgumentException>(() => authoring.RenameRepo("web", "web-2"));

        // An orchestration that uses it holds its ID, so it stays as it is.
        authoring.CreateOrchestration("storefront", [Dir("shop-services")]);
        Assert.Contains("'storefront'", authoring.RenameBlocker(Stored.Repo("Catalog")!));
        Assert.Throws<ArgumentException>(() => authoring.RenameRepo("Catalog", "catalog-2"));
        Assert.NotNull(Stored.Repo("Catalog"));
    }

    [Fact]
    public void A_repo_named_by_folder_moves_onto_the_Repos_list_reusing_the_folders_entry()
    {
        var trust = new FileTrustStore(_store.Paths);
        var authoring = Authoring(trust);
        Write("shop-web/sprig.cs", "// shop-web's own");
        authoring.AddRepo(Dir("shop-web"), "web");
        var orchestrationFile = Path.Combine(_repos, "storefront.orchestration.cs");
        File.WriteAllText(orchestrationFile, "var orchestration = Orchestration.Describe(\"storefront\");\nvar web = orchestration.Repo(\"shop-web\");\nreturn orchestration.Emit(args);\n");
        trust.Approve(orchestrationFile, DefinitionHash.Of(File.ReadAllText(orchestrationFile)));

        var entry = authoring.UseFromRepos(orchestrationFile, "shop-web");

        Assert.Equal("web", entry.Id);
        Assert.Contains("var web = orchestration.UseRepo(\"web\");", File.ReadAllText(orchestrationFile));
        Assert.True(Approved(trust, orchestrationFile));
        Assert.Single(Stored.Repos());
    }

    /// <summary>The drafts are only useful if they run, so this one compiles them with the real SDK.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void An_orchestration_made_in_the_app_over_repos_with_nothing_committed_plans()
    {
        var trust = new FileTrustStore(_store.Paths);
        var stored = new StoredDefinitions(_store.Paths);
        var registry = new OrchestrationRegistry(_store.Paths);
        var authoring = new OrchestrationAuthoring(stored, registry, trust);
        var loader = new OrchestrationLoader(new DefinitionRunner(trust, new DotnetRunHost(), _store.Paths,
            new DefinitionRunnerOptions { Timeout = TimeSpan.FromSeconds(120), AllowLocalSdkProject = true }), registry, stored);

        var entry = authoring.CreateOrchestration("trial run", [Dir("shop-web"), Dir("shop-services")]);
        var loaded = loader.Load(entry.Path);

        Assert.True(loaded.Complete, string.Join("\n", loaded.Problems.Select(p => p.Message)));
        Assert.Equal("trial-run", loaded.Id);
        var plan = Planner.Build(new PlanInput(loaded.Orchestration!, loaded.Repos, "plan", new SprigSettings(), new HashSet<int>()));
        Assert.False(plan.HasErrors, string.Join("\n", plan.Problems.Select(p => p.Message)));
        Assert.Equal(["shop-services/catalog-api", "shop-web/web"], plan.Apps.Select(a => $"{a.Owner}/{a.Name}").Order());
        Assert.False(File.Exists(Path.Combine(Dir("shop-web"), "sprig.cs")));
    }
}
