using Sprig;
using Sprig.Engine.Compose;
using Sprig.Engine.Definitions;
using Sprig.Engine.Git;
using Sprig.Engine.Planning;
using Sprig.Engine.Processes;
using Sprig.Engine.Settings;
using Sprig.Engine.Sprouts;
using Sprig.Engine.Store;
using Sprig.Engine.Trellises;
using Sprig.Model;

namespace Sprig.Tests.Sprouts;

/// <summary>Sprouts against real git repos in a temp folder. No infra, so docker is never called.</summary>
public sealed class SproutServiceTests : IDisposable
{
    readonly TempStore _store = new();
    readonly string _root = LongPath.Of(Path.Combine(Path.GetTempPath(), "sprig-sprouts-" + Guid.NewGuid().ToString("N")));
    readonly ProcessRunner _processes = new();
    readonly GitClient _git;
    readonly SproutService _sprouts;

    public SproutServiceTests()
    {
        Directory.CreateDirectory(_root);
        _git = new GitClient(_processes);
        var settings = new FileSettingsStore(_store.Paths);
        settings.Save(new SprigSettings { PortRangeStart = 9300, PortRangeEndExclusive = 9400 });
        _sprouts = new SproutService(new SproutStore(_store.Paths), new PortLeases(_store.Paths), settings, _git,
            new DockerCompose(_processes), listening: () => new HashSet<int>());
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal); // git marks its objects read-only
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    const string LocalFiles = ".env.local\n**/Properties/launchSettings.json\n";

    string Repo(string name, params (string path, string content)[] files) => RepoWith(name, LocalFiles, files);

    string RepoWith(string name, string gitignore, params (string path, string content)[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, ".gitignore"), gitignore);
        foreach (var (path, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, path))!);
            File.WriteAllText(Path.Combine(dir, path), content);
        }
        Git(dir, "add", "-A");
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init");
        return dir;
    }

    void Git(string dir, params string[] args) => _processes.Run("git", ["-C", dir, .. args]).EnsureSuccess();

    LoadedTrellis Load(bool worktrees, params (string path, Action<RepoBuilder> describe)[] repos)
    {
        var trellis = Trellis.Describe("storefront");
        if (worktrees) trellis.Worktrees("main");
        var models = new Dictionary<string, RepoModel>();
        var dirs = new Dictionary<string, string>();
        foreach (var (path, describe) in repos)
        {
            trellis.Repo(path);
            var repo = Sprig.Repo.Describe(path);
            describe(repo);
            models[path] = repo.BuildRepo();
            dirs[path] = Path.Combine(_root, path);
        }
        return new LoadedTrellis(Path.Combine(_root, "storefront.trellis.cs"), trellis.BuildTrellis(), models, [], [], [], dirs);
    }

    static void Web(RepoBuilder repo)
    {
        var web = repo.Node("web", ".").Http();
        web.EnvFile(".env.local", e => e.Set("PORT", web.Port));
    }

    static void Api(RepoBuilder repo)
        => repo.DotNet("api", "src/Api").Http(urlEnv: "ASPNETCORE_URLS").Env("Feature", "on").Output(EnvOutput.LaunchSettings());

    [Fact]
    public void Up_makes_worktrees_writes_ignored_env_files_and_keeps_its_ports()
    {
        Repo("shop-web", ("index.html", "hi"));
        Repo("shop-services", ("src/Api/Program.cs", "// api"));
        var loaded = Load(true, ("shop-web", Web), ("shop-services", Api));

        var record = _sprouts.Up(loaded, "feat-x", null);

        Assert.Equal(SproutState.Up, record.State);
        Assert.Equal("feat-x", record.Branch);
        var web = record.Repos.Single(r => r.Name == "shop-web");
        Assert.Equal(Path.Combine(_root, "storefront--feat-x", "shop-web"), web.Dir);
        Assert.True(web.BranchCreated);

        var envLocal = Path.Combine(web.Dir, ".env.local");
        var port = _sprouts.LastPlan("storefront", "feat-x")!.Ports.Single(p => p.Resource == "web").Port;
        Assert.Contains($"PORT={port}", File.ReadAllText(envLocal));

        var launch = Path.Combine(record.Repos.Single(r => r.Name == "shop-services").Dir, "src", "Api", "Properties", "launchSettings.json");
        Assert.Contains("\"Feature\": \"on\"", File.ReadAllText(launch));
        Assert.False(_git.HasUncommittedChanges(Path.GetDirectoryName(launch)!));
        // The repo's own .gitignore covers it; sprig adds no ignore rules of its own.
        var exclude = Path.Combine(_root, "shop-services", ".git", "info", "exclude");
        Assert.True(!File.Exists(exclude) || !File.ReadAllText(exclude).Contains("launchSettings"));

        var again = _sprouts.Up(loaded, "feat-x", null);
        Assert.Equal(port, _sprouts.LastPlan("storefront", "feat-x")!.Ports.Single(p => p.Resource == "web").Port);
        Assert.Equal(record.Repos.Select(r => r.Dir), again.Repos.Select(r => r.Dir));
    }

    [Fact]
    public void Two_trellises_that_call_themselves_the_same_keep_separate_sprouts_under_their_ids()
    {
        Repo("shop-web", ("index.html", "hi"));
        Repo("shop-web-2", ("index.html", "hi"));
        var committed = Load(true, ("shop-web", Web));
        var local = Load(true, ("shop-web-2", Web)) with { TrellisPath = Path.Combine(_root, "local", "trellis.cs"), Id = "storefront-local" };

        var a = _sprouts.Up(committed, "feat-x", null);
        var b = _sprouts.Up(local, "feat-x", null);

        Assert.Equal("storefront/feat-x", a.Key);
        Assert.Equal("storefront-local/feat-x", b.Key);
        Assert.Equal(Path.Combine(_root, "storefront-local--feat-x", "shop-web-2"), b.Repos.Single().Dir);
        Assert.Equal(2, _sprouts.List().Count);
        Assert.NotEqual(_sprouts.LastPlan("storefront", "feat-x")!.Ports.Single().Port,
            _sprouts.LastPlan("storefront-local", "feat-x")!.Ports.Single().Port);
        Assert.Equal("storefront-local", _sprouts.LastPlan("storefront-local", "feat-x")!.Trellis);
    }

    [Fact]
    public void Two_sprouts_get_their_own_worktrees_and_ports()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(true, ("shop-web", Web));

        _sprouts.Up(loaded, "feat-x", null);
        _sprouts.Up(loaded, "feat-y", "feature/y");

        var x = _sprouts.LastPlan("storefront", "feat-x")!.Ports.Single().Port;
        var y = _sprouts.LastPlan("storefront", "feat-y")!.Ports.Single().Port;
        Assert.NotEqual(x, y);
        Assert.True(_git.BranchExists(Path.Combine(_root, "shop-web"), "feature/y"));
        Assert.Equal(2, _sprouts.List().Count);
    }

    [Fact]
    public void Rm_removes_worktrees_and_the_branch_and_frees_the_ports()
    {
        var source = Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(true, ("shop-web", Web));
        var record = _sprouts.Up(loaded, "feat-x", null);

        _sprouts.Remove("storefront", "feat-x", new RemoveOptions(DeleteBranch: true));

        Assert.False(Directory.Exists(record.Folder));
        Assert.False(_git.BranchExists(source, "feat-x"));
        Assert.Empty(_sprouts.List());
        Assert.Empty(new PortLeases(_store.Paths).All());
    }

    [Fact]
    public void Rm_refuses_a_worktree_with_uncommitted_changes_unless_forced()
    {
        Repo("shop-web", ("index.html", "hi"));
        var record = _sprouts.Up(Load(true, ("shop-web", Web)), "feat-x", null);
        File.WriteAllText(Path.Combine(record.Repos[0].Dir, "index.html"), "changed");

        var e = Assert.Throws<SproutException>(() => _sprouts.Remove("storefront", "feat-x", new RemoveOptions()));
        Assert.Contains("changes that aren't committed", e.Message);

        _sprouts.Remove("storefront", "feat-x", new RemoveOptions(Force: true));
        Assert.Empty(_sprouts.List());
    }

    [Fact]
    public void Worktree_and_file_status_and_the_removal_blockers_say_what_removing_would_lose()
    {
        Repo("shop-web", ("index.html", "hi"));
        Repo("shop-services", ("src/Api/Program.cs", "// api"));
        var record = _sprouts.Up(Load(true, ("shop-web", Web), ("shop-services", Api)), "feat-x", null);
        var web = record.Repos.Single(r => r.Name == "shop-web");
        var services = record.Repos.Single(r => r.Name == "shop-services");

        Assert.Empty(_sprouts.RemovalBlockers(record, deleteBranch: true));
        Assert.All(_sprouts.WorktreeStatus(record), t => Assert.Equal((true, false, 0), (t.Exists, t.Dirty, t.Unpushed)));
        var files = _sprouts.FileStatus(record);
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.True(f.Ignored && !f.Tracked));

        File.WriteAllText(Path.Combine(web.Dir, "index.html"), "changed");
        File.WriteAllText(Path.Combine(services.Dir, "notes.txt"), "new");
        Git(services.Dir, "add", "-A");
        Git(services.Dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "only here");

        var trees = _sprouts.WorktreeStatus(record).ToDictionary(t => t.Repo);
        Assert.True(trees["shop-web"].Dirty);
        Assert.Equal(1, trees["shop-services"].Unpushed);
        var blockers = _sprouts.RemovalBlockers(record, deleteBranch: true);
        Assert.Equal(2, blockers.Count);
        Assert.Contains("changes that aren't committed", blockers[0]);
        Assert.Contains("commits no other branch or remote has, in shop-services", blockers[1]);
        Assert.Single(_sprouts.RemovalBlockers(record, deleteBranch: false));

        var e = Assert.Throws<SproutException>(() => _sprouts.Remove("storefront", "feat-x", new RemoveOptions(DeleteBranch: true)));
        Assert.Equal(blockers[0], e.Message);
    }

    [Fact]
    public void A_tracked_target_is_refused_before_any_worktree_is_made()
    {
        Repo("shop-web", ("index.html", "hi"));
        RepoWith("shop-services", ".env.local\n", ("src/Api/Properties/launchSettings.json", """{ "profiles": {} }"""));
        var loaded = Load(true, ("shop-web", Web), ("shop-services", Api));

        var e = Assert.Throws<SproutException>(() => _sprouts.Up(loaded, "feat-x", null));

        var problem = Assert.Single(e.Problems);
        Assert.StartsWith("git tracks src/Api/Properties/launchSettings.json", problem.Message);
        Assert.Contains("EnvOutput.AppSettings", problem.Message);
        Assert.EndsWith("SproutServiceTests.cs", problem.Location!.File);
        Assert.False(Directory.Exists(Path.Combine(_root, "storefront--feat-x")));
        Assert.Null(_sprouts.Get("storefront", "feat-x"));
    }

    [Fact]
    public void A_target_the_repo_does_not_ignore_is_refused_with_what_to_add()
    {
        RepoWith("shop-services", "bin/\n", ("src/Api/Program.cs", "// api"));
        var loaded = Load(false, ("shop-services", repo =>
            repo.DotNet("api", "src/Api").Http(urlEnv: "Urls").Output(EnvOutput.AppSettings("appsettings.Local.json"))));

        var e = Assert.Throws<SproutException>(() => _sprouts.Up(loaded, "local", null));

        var problem = Assert.Single(e.Problems);
        Assert.Contains("src/Api/appsettings.Local.json isn't ignored by git", problem.Message);
        Assert.Contains("Add it to the repo's .gitignore", problem.Message);
        Assert.Contains("AddJsonFile(\"appsettings.Local.json\", optional: true)", problem.Message);
        Assert.False(File.Exists(Path.Combine(_root, "shop-services", "src", "Api", "appsettings.Local.json")));
    }

    [Fact]
    public void In_place_writes_into_the_repo_itself_allows_one_sprout_and_rm_puts_the_files_back()
    {
        var dir = Repo("shop-web", ("index.html", "hi"));
        File.WriteAllText(Path.Combine(dir, ".env.local"), "MINE=1\n");
        var loaded = Load(false, ("shop-web", Web));

        var record = _sprouts.Up(loaded, "local", null);

        Assert.True(record.InPlace);
        Assert.Equal(dir, record.Repos[0].Dir);
        Assert.Contains("PORT=", File.ReadAllText(Path.Combine(dir, ".env.local")));
        Assert.Throws<SproutException>(() => _sprouts.Up(loaded, "other", null));

        _sprouts.Remove("storefront", "local", new RemoveOptions());
        Assert.Equal("MINE=1\n", File.ReadAllText(Path.Combine(dir, ".env.local")));
    }

    [Fact]
    public void A_bad_plan_fails_before_any_worktree_is_made()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(true, ("shop-web", repo => repo.Node("web", ".").Http().Env("API", repo.Import<Url>("api-url"))));

        var e = Assert.Throws<SproutException>(() => _sprouts.Up(loaded, "feat-x", null));

        Assert.Contains(e.Problems, p => p.Message.Contains("imports 'api-url'"));
        Assert.False(Directory.Exists(Path.Combine(_root, "storefront--feat-x")));
        Assert.Null(_sprouts.Get("storefront", "feat-x"));
    }

    /// <summary>Stands in for <c>dotnet run</c>: each definition's text names the builder whose model it emits.</summary>
    sealed class ScriptedHost(Dictionary<string, Func<DefinitionBuilder>> models) : IDefinitionHost
    {
        public HostResult Run(string definitionPath, string outputFile, TimeSpan timeout)
        {
            models[File.ReadAllText(definitionPath).Trim()]().Emit(["emit", "--out", outputFile]);
            return new HostResult(0, "", false);
        }
    }

    static RepoBuilder WebWith(string feature)
    {
        var repo = Sprig.Repo.Describe("shop-web");
        var web = repo.Node("web", ".").Http();
        web.EnvFile(".env.local", e => e.Set("PORT", web.Port).Set("FEATURE", feature));
        return repo;
    }

    [Fact]
    public void A_branch_that_changes_sprig_cs_is_run_as_the_branch_has_it_once_approved()
    {
        var source = Repo("shop-web", ("index.html", "hi"), ("sprig.cs", "// web v1"));
        Git(source, "checkout", "-q", "-b", "feat-x");
        File.WriteAllText(Path.Combine(source, "sprig.cs"), "// web v2");
        Git(source, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-am", "v2");
        Git(source, "checkout", "-q", "main");
        var trellisFile = Path.Combine(_root, "storefront.trellis.cs");
        File.WriteAllText(trellisFile, "// trellis");

        var trust = new FileTrustStore(_store.Paths);
        var loader = new TrellisLoader(new DefinitionRunner(trust, new ScriptedHost(new()
        {
            ["// trellis"] = () => { var t = Trellis.Describe("storefront"); t.Worktrees("main"); t.Repo("shop-web"); return t; },
            ["// web v1"] = () => WebWith("v1"),
            ["// web v2"] = () => WebWith("v2"),
        }), _store.Paths));
        var settings = new FileSettingsStore(_store.Paths);
        var sprouts = new SproutService(new SproutStore(_store.Paths), new PortLeases(_store.Paths), settings, _git,
            new DockerCompose(_processes), listening: () => new HashSet<int>(), loader: loader);
        var mainDefinition = Path.Combine(source, "sprig.cs");
        trust.Approve(trellisFile, DefinitionHash.Of("// trellis"));
        trust.Approve(mainDefinition, DefinitionHash.Of("// web v1"));
        var loaded = loader.Load(trellisFile);
        Assert.True(loaded.Complete);

        var e = Assert.Throws<SproutException>(() => sprouts.Up(loaded, "feat-x", null));

        var pending = Assert.Single(e.Untrusted);
        var worktree = Path.Combine(_root, "storefront--feat-x", "shop-web");
        Assert.Equal(Path.Combine(worktree, "sprig.cs"), pending.Path);
        Assert.Equal(mainDefinition, pending.TrustPath);
        Assert.Equal(mainDefinition, sprouts.TrustPathFor(pending.Path));
        Assert.False(File.Exists(Path.Combine(worktree, ".env.local")));

        trust.Approve(pending.TrustPath, pending.Sha256);
        var record = sprouts.Up(loaded, "feat-x", null);

        Assert.Equal(SproutState.Up, record.State);
        Assert.Contains("FEATURE=v2", File.ReadAllText(Path.Combine(worktree, ".env.local")));
        // The main checkout's version stays approved alongside the branch's.
        Assert.IsType<Evaluated>(loader.Load(trellisFile).Results[1]);
    }

    [Fact]
    public void Virtual_trellises_and_definitions_sit_beside_a_repos_own_without_clashing()
    {
        var source = Repo("shop-web", ("index.html", "hi"), ("sprig.cs", "// web v1"));
        var virtuals = new VirtualDefinitions(_store.Paths);
        var registry = new TrellisRegistry(_store.Paths);
        var trust = new FileTrustStore(_store.Paths);
        var loader = new TrellisLoader(new DefinitionRunner(trust, new ScriptedHost(new()
        {
            ["// own"] = () => { var t = Trellis.Describe("storefront"); t.Worktrees("main"); t.Repo(source); return t; },
            ["// uses a"] = () => { var t = Trellis.Describe("storefront"); t.Worktrees("main"); t.Repo(source, definition: "web-a"); return t; },
            ["// uses b"] = () => { var t = Trellis.Describe("storefront"); t.Worktrees("main"); t.Repo(source, definition: "web-b"); return t; },
            ["// uses missing"] = () => { var t = Trellis.Describe("storefront"); t.Repo(source, definition: "nope"); return t; },
            ["// web v1"] = () => WebWith("v1"),
            ["// virtual a"] = () => WebWith("a"),
            ["// virtual b"] = () => WebWith("b"),
        }), _store.Paths), registry, virtuals);
        var sprouts = new SproutService(new SproutStore(_store.Paths), new PortLeases(_store.Paths), new FileSettingsStore(_store.Paths), _git,
            new DockerCompose(_processes), listening: () => new HashSet<int>(), loader: loader);

        string VirtualTrellis(string id, string text)
        {
            var file = virtuals.CreateTrellis(id, text);
            registry.Add(id, file);
            trust.Approve(file, DefinitionHash.Of(text));
            return file;
        }
        var own = VirtualTrellis("own", "// own");
        var usesA = VirtualTrellis("uses-a", "// uses a");
        var usesB = VirtualTrellis("uses-b", "// uses b");
        foreach (var (id, text) in new[] { ("web-a", "// virtual a"), ("web-b", "// virtual b") })
            trust.Approve(virtuals.CreateRepo(id, text), DefinitionHash.Of(text));
        trust.Approve(Path.Combine(source, "sprig.cs"), DefinitionHash.Of("// web v1"));

        var feature = new Dictionary<string, string>();
        foreach (var (file, sprout) in new[] { (own, "x1"), (usesA, "x2"), (usesB, "x3") })
        {
            var record = sprouts.Up(loader.Load(file), sprout, null);
            feature[record.Trellis] = string.Join(",", File.ReadAllLines(Path.Combine(record.Repos.Single().Dir, ".env.local"))
                .Where(l => l.StartsWith("FEATURE=")).Distinct());
        }

        Assert.Equal("FEATURE=v1", feature["own"]);
        Assert.Equal("FEATURE=a", feature["uses-a"]);
        Assert.Equal("FEATURE=b", feature["uses-b"]);
        Assert.Equal("// web v1", File.ReadAllText(Path.Combine(source, "sprig.cs")));
        Assert.Equal(["web-a", "web-b"], virtuals.RepoIds());

        var missing = VirtualTrellis("uses-missing", "// uses missing");
        var problem = Assert.Single(loader.Load(missing).Problems);
        Assert.Contains("names the definition 'nope', which sprig doesn't have", problem.Message);

        // A virtual definition is approved as itself, so changing it asks again.
        File.WriteAllText(virtuals.RepoFile("web-a"), "// virtual b");
        var pending = Assert.Single(loader.Load(usesA).Untrusted);
        Assert.Equal(virtuals.RepoFile("web-a"), pending.TrustPath);
    }

    [Fact]
    public void A_repo_used_by_id_brings_its_folder_and_reads_its_own_sprig_cs_from_the_sprouts_worktree()
    {
        var source = Repo("shop-web", ("index.html", "hi"), ("sprig.cs", "// web v1"));
        var virtuals = new VirtualDefinitions(_store.Paths);
        var trust = new FileTrustStore(_store.Paths);
        var loader = new TrellisLoader(new DefinitionRunner(trust, new ScriptedHost(new()
        {
            ["// trellis"] = () => { var t = Trellis.Describe("storefront"); t.Worktrees("main"); t.UseRepo("web"); return t; },
            ["// uses nothing"] = () => { var t = Trellis.Describe("storefront"); t.UseRepo("nope"); return t; },
            ["// web v1"] = () => WebWith("v1"),
        }), _store.Paths), virtuals: virtuals);
        var sprouts = new SproutService(new SproutStore(_store.Paths), new PortLeases(_store.Paths), new FileSettingsStore(_store.Paths), _git,
            new DockerCompose(_processes), listening: () => new HashSet<int>(), loader: loader);
        virtuals.ReferenceRepo("web", source);
        var trellisFile = Path.Combine(_root, "storefront.trellis.cs");
        File.WriteAllText(trellisFile, "// trellis");
        trust.Approve(trellisFile, DefinitionHash.Of("// trellis"));
        trust.Approve(Path.Combine(source, "sprig.cs"), DefinitionHash.Of("// web v1"));

        var record = sprouts.Up(loader.Load(trellisFile), "feat-x", null);

        var repo = Assert.Single(record.Repos);
        Assert.Equal("web", repo.RepoPath);
        Assert.Equal(source, repo.Source);
        Assert.Equal(Path.Combine(_root, "storefront--feat-x", "shop-web"), repo.Dir);
        Assert.Contains("FEATURE=v1", File.ReadAllText(Path.Combine(repo.Dir, ".env.local")));

        var missingFile = Path.Combine(_root, "missing.trellis.cs");
        File.WriteAllText(missingFile, "// uses nothing");
        trust.Approve(missingFile, DefinitionHash.Of("// uses nothing"));
        Assert.Contains("'nope' isn't in sprig's Repos list", Assert.Single(loader.Load(missingFile).Problems).Message);
    }

    [Fact]
    public void A_new_branch_from_main_needs_no_new_approval()
    {
        var source = Repo("shop-web", ("index.html", "hi"), ("sprig.cs", "// web v1"));
        var trellisFile = Path.Combine(_root, "storefront.trellis.cs");
        File.WriteAllText(trellisFile, "// trellis");
        var trust = new FileTrustStore(_store.Paths);
        var loader = new TrellisLoader(new DefinitionRunner(trust, new ScriptedHost(new()
        {
            ["// trellis"] = () => { var t = Trellis.Describe("storefront"); t.Worktrees("main"); t.Repo("shop-web"); return t; },
            ["// web v1"] = () => WebWith("v1"),
        }), _store.Paths));
        var sprouts = new SproutService(new SproutStore(_store.Paths), new PortLeases(_store.Paths), new FileSettingsStore(_store.Paths), _git,
            new DockerCompose(_processes), listening: () => new HashSet<int>(), loader: loader);
        trust.Approve(trellisFile, DefinitionHash.Of("// trellis"));
        trust.Approve(Path.Combine(source, "sprig.cs"), DefinitionHash.Of("// web v1"));

        var record = sprouts.Up(loader.Load(trellisFile), "feat-y", null);

        Assert.Contains("FEATURE=v1", File.ReadAllText(Path.Combine(record.Repos[0].Dir, ".env.local")));
        Assert.Equal(record.Repos[0].Dir, sprouts.Worktrees(trellisFile, "feat-y")!["shop-web"]);
    }

    [Fact]
    public void Git_status_answers_for_every_file_in_one_go()
    {
        var dir = RepoWith("shop-services", ".env.local\n", ("src/Api/appsettings.json", "{}"), ("src/Api/.env.local", ""));
        File.WriteAllText(Path.Combine(dir, "loose.txt"), "");
        string At(string relative) => Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));

        var status = _git.Status(dir, [At("src/Api/appsettings.json"), At("src/Api/.env.local"), At("web/.env.local"), At("loose.txt"), At("new.json")])!;

        Assert.Equal((true, false), status[At("src/Api/appsettings.json")]);
        Assert.Equal((false, true), status[At("src/Api/.env.local")]);   // ignored, and the commit left it out
        Assert.Equal((false, true), status[At("web/.env.local")]);       // doesn't exist yet
        Assert.Equal((false, false), status[At("loose.txt")]);
        Assert.Equal((false, false), status[At("new.json")]);
        var outside = Path.Combine(_root, "not-a-repo");
        Directory.CreateDirectory(outside);
        Assert.Null(_git.Status(outside, [Path.Combine(outside, "x")]));
    }

    [Theory]
    [InlineData("feat-x", true)]
    [InlineData("feat/x", false)]
    [InlineData("-x", false)]
    public void Sprout_names_are_checked(string name, bool ok) => Assert.Equal(ok, SproutService.CheckName(name) is null);
}
