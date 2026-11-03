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

    string Repo(string name, params (string path, string content)[] files)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, ".gitignore"), ".env.local\n");
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
        Assert.True(_git.IsIgnored(Path.GetDirectoryName(launch)!, launch));
        Assert.False(_git.HasUncommittedChanges(Path.GetDirectoryName(launch)!));

        var again = _sprouts.Up(loaded, "feat-x", null);
        Assert.Equal(port, _sprouts.LastPlan("storefront", "feat-x")!.Ports.Single(p => p.Resource == "web").Port);
        Assert.Equal(record.Repos.Select(r => r.Dir), again.Repos.Select(r => r.Dir));
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
    public void A_tracked_target_is_refused_before_anything_is_written()
    {
        Repo("shop-web", ("index.html", "hi"));
        Repo("shop-services", ("src/Api/Properties/launchSettings.json", """{ "profiles": {} }"""));
        var loaded = Load(true, ("shop-web", Web), ("shop-services", Api));

        var e = Assert.Throws<SproutException>(() => _sprouts.Up(loaded, "feat-x", null));

        Assert.Contains("won't write a sprout's values into files git tracks", e.Message);
        Assert.Contains("launchSettings.json", e.Message);
        Assert.False(File.Exists(Path.Combine(_root, "storefront--feat-x", "shop-web", ".env.local")));
        Assert.Equal(SproutState.Failed, _sprouts.Get("storefront", "feat-x")!.State);
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

    [Theory]
    [InlineData("feat-x", true)]
    [InlineData("feat/x", false)]
    [InlineData("-x", false)]
    public void Sprout_names_are_checked(string name, bool ok) => Assert.Equal(ok, SproutService.CheckName(name) is null);
}
