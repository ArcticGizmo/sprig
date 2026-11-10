using Sprig;
using Sprig.Engine.Compose;
using Sprig.Engine.Definitions;
using Sprig.Engine.Git;
using Sprig.Engine.Planning;
using Sprig.Engine.Processes;
using Sprig.Engine.Settings;
using Sprig.Engine.Workspaces;
using Sprig.Engine.Store;
using Sprig.Engine.Orchestrations;
using Sprig.Model;

namespace Sprig.Tests.Workspaces;

/// <summary>Workspaces against real git repos in a temp folder. No infra, so docker is never called.</summary>
public sealed class WorkspaceServiceTests : IDisposable
{
    readonly TempStore _store = new();
    readonly string _root = LongPath.Of(Path.Combine(Path.GetTempPath(), "sprig-workspaces-" + Guid.NewGuid().ToString("N")));
    readonly ProcessRunner _processes = new();
    readonly GitClient _git;
    readonly WorkspaceService _workspaces;

    public WorkspaceServiceTests()
    {
        Directory.CreateDirectory(_root);
        _git = new GitClient(_processes);
        var settings = new FileSettingsStore(_store.Paths);
        settings.Save(new SprigSettings { PortRangeStart = 9300, PortRangeEndExclusive = 9400 });
        _workspaces = new WorkspaceService(new WorkspaceStore(_store.Paths), new PortLeases(_store.Paths), settings, _git,
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

    static readonly WorkspaceOptions InPlace = new(InPlace: true);

    LoadedOrchestration Load(params (string path, Action<RepoBuilder> describe)[] repos)
    {
        var orchestration = Orchestration.Describe("storefront");
        var models = new Dictionary<string, RepoModel>();
        var dirs = new Dictionary<string, string>();
        foreach (var (path, describe) in repos)
        {
            orchestration.Repo(path);
            var repo = Sprig.Repo.Describe(path);
            describe(repo);
            models[path] = repo.BuildRepo();
            dirs[path] = Path.Combine(_root, path);
        }
        return new LoadedOrchestration(Path.Combine(_root, "storefront.orchestration.cs"), orchestration.BuildOrchestration(), models, [], [], [], dirs);
    }

    static void Web(RepoBuilder repo)
    {
        var web = repo.Node("web", ".").Http();
        web.EnvFile(".env.local", e => e.Set("PORT", web.Port));
    }

    static void Api(RepoBuilder repo)
    {
        var api = repo.DotNet("api", "src/Api").Http();
        api.LaunchSettings(s => s.Set("ASPNETCORE_URLS", api.Url).Set("Feature", "on"));
    }

    [Fact]
    public void Up_makes_worktrees_writes_ignored_env_files_and_keeps_its_ports()
    {
        Repo("shop-web", ("index.html", "hi"));
        Repo("shop-services", ("src/Api/Program.cs", "// api"));
        var loaded = Load(("shop-web", Web), ("shop-services", Api));

        var record = _workspaces.Up(loaded, "feat-x", null);

        Assert.Equal(WorkspaceState.Up, record.State);
        Assert.Equal("feat-x", record.Branch);
        var web = record.Repos.Single(r => r.Name == "shop-web");
        Assert.Equal(Path.Combine(_root, "storefront--feat-x", "shop-web"), web.Dir);
        Assert.True(web.BranchCreated);

        var envLocal = Path.Combine(web.Dir, ".env.local");
        var port = _workspaces.LastPlan("storefront", "feat-x")!.Ports.Single(p => p.Resource == "web").Port;
        Assert.Contains($"PORT={port}", File.ReadAllText(envLocal));

        var launch = Path.Combine(record.Repos.Single(r => r.Name == "shop-services").Dir, "src", "Api", "Properties", "launchSettings.json");
        Assert.Contains("\"Feature\": \"on\"", File.ReadAllText(launch));
        Assert.False(_git.HasUncommittedChanges(Path.GetDirectoryName(launch)!));
        // The repo's own .gitignore covers it; sprig adds no ignore rules of its own.
        var exclude = Path.Combine(_root, "shop-services", ".git", "info", "exclude");
        Assert.True(!File.Exists(exclude) || !File.ReadAllText(exclude).Contains("launchSettings"));

        var again = _workspaces.Up(loaded, "feat-x", null);
        Assert.Equal(port, _workspaces.LastPlan("storefront", "feat-x")!.Ports.Single(p => p.Resource == "web").Port);
        Assert.Equal(record.Repos.Select(r => r.Dir), again.Repos.Select(r => r.Dir));
    }

    [Fact]
    public void Two_orchestrations_that_call_themselves_the_same_keep_separate_workspaces_under_their_ids()
    {
        Repo("shop-web", ("index.html", "hi"));
        Repo("shop-web-2", ("index.html", "hi"));
        var committed = Load(("shop-web", Web));
        var local = Load(("shop-web-2", Web)) with { OrchestrationPath = Path.Combine(_root, "local", "orchestration.cs"), Id = "storefront-local" };

        var a = _workspaces.Up(committed, "feat-x", null);
        var b = _workspaces.Up(local, "feat-x", null);

        Assert.Equal("storefront/feat-x", a.Key);
        Assert.Equal("storefront-local/feat-x", b.Key);
        Assert.Equal(Path.Combine(_root, "storefront-local--feat-x", "shop-web-2"), b.Repos.Single().Dir);
        Assert.Equal(2, _workspaces.List().Count);
        Assert.NotEqual(_workspaces.LastPlan("storefront", "feat-x")!.Ports.Single().Port,
            _workspaces.LastPlan("storefront-local", "feat-x")!.Ports.Single().Port);
        Assert.Equal("storefront-local", _workspaces.LastPlan("storefront-local", "feat-x")!.Orchestration);
    }

    [Fact]
    public void A_new_workspaces_branches_start_from_each_repos_default_branch_unless_told_and_its_port_policy_is_its_own()
    {
        Repo("shop-web", ("index.html", "hi"));
        var services = Repo("shop-services", ("src/Api/Program.cs", "// api"));
        Git(services, "branch", "-m", "main", "master");
        Git(services, "branch", "develop");
        var loaded = Load(("shop-web", Web), ("shop-services", Api));

        var record = _workspaces.Up(loaded, "feat-x", null, new WorkspaceOptions(RandomPorts: true));

        Assert.Equal(("main", "master"), (record.Repos.Single(r => r.Name == "shop-web").From, record.Repos.Single(r => r.Name == "shop-services").From));
        Assert.Equal("random", record.PortPolicy);
        var plan = _workspaces.LastPlan("storefront", "feat-x")!;
        Assert.Equal(("random", "each repo's default branch"), (plan.PortPolicy, plan.WorktreesFrom));
        Assert.Contains("fresh ports", Assert.Throws<WorkspaceException>(() => _workspaces.Up(loaded, "feat-x", null, new WorkspaceOptions())).Message);

        _workspaces.Remove("storefront", "feat-x", new RemoveOptions(DeleteBranch: true));
        Git(Path.Combine(_root, "shop-web"), "branch", "develop");
        var told = _workspaces.Up(loaded, "feat-y", null, new WorkspaceOptions(From: "develop"));
        Assert.All(told.Repos, r => Assert.Equal("develop", r.From));
        Assert.Equal(("stable", "develop"), (told.PortPolicy, _workspaces.LastPlan("storefront", "feat-y")!.WorktreesFrom));
    }

    static void WebWithApi(RepoBuilder repo)
    {
        var web = repo.Node("web", ".").Http();
        web.EnvFile(".env.local", e => e.Set("PORT", web.Port).Set("API", repo.Setting<Url>("api-url", "http://localhost:5100")));
    }

    [Fact]
    public void Check_out_puts_each_branch_in_a_numbered_slot_labelled_by_its_branch()
    {
        Repo("shop-web", ("index.html", "hi"));
        Repo("shop-services", ("src/Api/Program.cs", "// api"));
        var loaded = Load(("shop-web", Web), ("shop-services", Api));

        var x = _workspaces.CheckOut(loaded, "feat-x");
        var y = _workspaces.CheckOut(loaded, "feature/y");

        Assert.Equal(("1", "feat-x", "storefront--1"), (x.Name, x.Branch, x.SlotName));
        Assert.Equal(("2", "feature/y", "storefront--2"), (y.Name, y.Branch, y.SlotName));
        Assert.Equal(Path.Combine(_root, "storefront--1", "shop-web"), x.Repos.Single(r => r.Name == "shop-web").Dir);
        Assert.Equal("feature/y", _git.CurrentBranch(y.Repos.Single(r => r.Name == "shop-services").Dir));
        // A branch already in a slot comes up there again.
        Assert.Equal("1", _workspaces.CheckOut(loaded, "feat-x").Name);
        Assert.Contains("has 'feat-x' checked out", Assert.Throws<WorkspaceException>(
            () => _workspaces.CheckOut(loaded, "feat-z", new CheckOutOptions(Slot: 1))).Message);
    }

    [Fact]
    public void A_folder_in_the_way_of_a_slot_is_found_before_anything_is_made()
    {
        Repo("shop-web", ("index.html", "hi"));
        var services = Repo("shop-services", ("src/Api/Program.cs", "// api"));
        var loaded = Load(("shop-web", Web), ("shop-services", Api));
        // Left over from something else: not a worktree of the repo.
        var stray = Path.Combine(_root, "storefront--1", "shop-services");
        Directory.CreateDirectory(stray);
        File.WriteAllText(Path.Combine(stray, "notes.txt"), "mine");

        var blocker = _workspaces.CheckOutBlocker(loaded, "feat-x");

        Assert.Equal($"{stray} already exists and isn't a worktree of {services}. Move or delete it, then try again.", blocker);
        var e = Assert.Throws<WorkspaceException>(() => _workspaces.CheckOut(loaded, "feat-x"));
        Assert.Equal($"storefront--1 is broken: {stray} is there, holding 1 file, but isn't a worktree sprig can use. Fix it (deleting what's in the way), or use storefront--2 instead", e.Message);
        // shop-web comes first, but nothing was made for it, and there's no workspace to clean up.
        Assert.False(Directory.Exists(Path.Combine(_root, "storefront--1", "shop-web")));
        Assert.False(_git.BranchExists(Path.Combine(_root, "shop-web"), "feat-x"));
        Assert.Empty(_workspaces.List());
        Assert.True(File.Exists(Path.Combine(stray, "notes.txt")));

        // A branch checked out in the repo's own folder is found the same way, and a clear slot has nothing to say.
        Assert.Contains("checked out in shop-web's own folder", _workspaces.CheckOutBlocker(loaded, "main"));
        Directory.Delete(Path.Combine(_root, "storefront--1"), recursive: true);
        Assert.Null(_workspaces.CheckOutBlocker(loaded, "feat-x"));
    }

    [Fact]
    public void A_released_slot_is_reused_with_its_folder_ports_values_and_what_was_built_in_it()
    {
        var source = Repo("shop-web", ("index.html", "hi"), (".gitignore", LocalFiles + "node_modules/\n"));
        var loaded = Load(("shop-web", WebWithApi)) with { Id = "shop-web", Alone = true };

        var first = _workspaces.CheckOut(loaded, "feat-x", values: new Dictionary<string, string> { ["api-url"] = "https://staging.example.com" });
        var dir = first.Repos.Single().Dir;
        // A repo on its own is the slot's folder itself.
        Assert.Equal(Path.Combine(_root, "shop-web--1"), dir);
        Assert.Equal(first.Folder, dir);
        var port = _workspaces.LastPlan("shop-web", "1")!.Ports.Single().Port;
        Directory.CreateDirectory(Path.Combine(dir, "node_modules"));
        File.WriteAllText(Path.Combine(dir, "node_modules", "installed.txt"), "yes");

        var released = _workspaces.Free("shop-web", "1");

        Assert.Equal(WorkspaceState.Released, released.State);
        Assert.True(released.IsFree);
        Assert.Equal("free", released.Label);
        Assert.Null(_git.CurrentBranch(dir));
        Assert.True(_git.BranchExists(source, "feat-x"));
        Assert.Contains("is free", Assert.Throws<WorkspaceException>(() => _workspaces.Up(loaded, "1", null)).Message);

        var second = _workspaces.CheckOut(loaded, "feat-y");

        Assert.Equal(("1", "feat-y"), (second.Name, second.Branch));
        Assert.Equal("feat-y", _git.CurrentBranch(dir));
        Assert.Equal(port, _workspaces.LastPlan("shop-web", "1")!.Ports.Single().Port);
        Assert.True(File.Exists(Path.Combine(dir, "node_modules", "installed.txt")));
        Assert.Contains("API=https://staging.example.com", File.ReadAllText(Path.Combine(dir, ".env.local")));

        // Checking out again can clear what the slot kept.
        _workspaces.Free("shop-web", "1");
        _workspaces.CheckOut(loaded, "feat-z", unset: ["api-url"]);
        Assert.Contains("API=http://localhost:5100", File.ReadAllText(Path.Combine(dir, ".env.local")));
    }

    [Fact]
    public void A_slot_drops_its_logs_when_released_removed_or_taken_by_a_new_branch()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", Web));
        var log = Sprig.Engine.Hosting.HostProtocol.LogFile(_store.Paths, "storefront/1", "shop-web/web");
        void Run(string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, text);
        }

        // Left by a slot 1 that was removed before logs went with it.
        Run("an old slot's run");
        _workspaces.CheckOut(loaded, "feat-x");
        Assert.False(File.Exists(log));

        // The same branch coming up again is the same workspace, logs and all.
        Run("feat-x's run");
        _workspaces.CheckOut(loaded, "feat-x");
        Assert.True(File.Exists(log));

        _workspaces.Free("storefront", "1");
        Assert.False(File.Exists(log));
        Assert.False(Directory.Exists(Path.Combine(Sprig.Engine.Hosting.HostProtocol.LogFolder(_store.Paths), "storefront")));

        _workspaces.CheckOut(loaded, "feat-y");
        Run("feat-y's run");
        _workspaces.Remove("storefront", "1", new RemoveOptions());
        Assert.False(File.Exists(log));
    }

    /// <summary>A second remote, <c>upstream</c>, with a branch the repo doesn't have locally.</summary>
    void Upstream(string repo, string branch)
    {
        var other = Path.Combine(_root, "upstream-" + Path.GetFileName(repo));
        Git(_root, "clone", "-q", repo, other);
        Git(other, "switch", "-q", "-c", branch);
        File.WriteAllText(Path.Combine(other, "upstream.txt"), branch);
        Git(other, "add", "-A");
        Git(other, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "on upstream");
        Git(repo, "remote", "add", "upstream", other);
        Git(repo, "fetch", "-q", "upstream");
    }

    [Fact]
    public void Branch_choices_list_local_branches_then_each_remotes_with_the_default_marked()
    {
        var source = Repo("shop-web", ("index.html", "hi"));
        Git(source, "branch", "feat-local");
        Upstream(source, "feat/up");

        var choices = _workspaces.BranchChoices(Load(("shop-web", Web)));

        Assert.Equal(["main", "feat-local"], choices.Where(c => c.Remote is null).Select(c => c.Ref));
        Assert.True(choices.First(c => c.Ref == "main").Default);
        Assert.Contains(choices, c => c.Ref == "upstream/feat/up");
        Assert.DoesNotContain(choices, c => c.Name == "HEAD");
    }

    [Fact]
    public void A_new_branch_is_refused_when_its_name_is_taken_and_an_existing_one_is_taken_from_the_remote_named()
    {
        var source = Repo("shop-web", ("index.html", "hi"));
        Upstream(source, "feat/up");
        var loaded = Load(("shop-web", Web));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.CheckOut(loaded, "feat/up", new CheckOutOptions(NewBranch: true)));
        Assert.Contains("already exists in upstream (shop-web)", e.Message);
        Assert.Contains("Existing branch", e.Message);

        var record = _workspaces.CheckOut(loaded, "feat/up", new CheckOutOptions(Track: "upstream"));

        Assert.True(File.Exists(Path.Combine(record.Repos[0].Dir, "upstream.txt")));
        Assert.Equal("upstream/feat/up", RunGit(record.Repos[0].Dir, "rev-parse", "--abbrev-ref", "@{upstream}"));
    }

    [Fact]
    public void A_free_slot_can_be_made_with_no_branch_and_claimed_later()
    {
        var source = Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", Web));

        var free = _workspaces.MakeFreeSlot(loaded);

        Assert.True(free.IsFree);
        Assert.Equal("1", free.Name);
        Assert.Null(_git.CurrentBranch(free.Repos[0].Dir));
        Assert.Contains(_git.Worktrees(source), w => string.Equals(w, Path.GetFullPath(free.Repos[0].Dir), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("2", _workspaces.FreeSlotName("storefront"));

        // The next branch takes the free slot, reusing its worktree.
        var claimed = _workspaces.CheckOut(loaded, "feat-x", new CheckOutOptions(NewBranch: true));
        Assert.Equal("1", claimed.Name);
        Assert.Equal("feat-x", _git.CurrentBranch(claimed.Repos[0].Dir));
        Assert.Single(_workspaces.List());
    }

    string RunGit(string dir, params string[] args)
    {
        var result = _processes.Run("git", ["-C", dir, .. args], dir);
        Assert.True(result.Success, result.StdErr);
        return result.StdOut.Trim();
    }

    [Fact]
    public void A_broken_slot_is_fixed_when_asked_or_passed_over_for_the_next_one()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", Web)) with { Id = "shop-web", Alone = true };
        // Left from a slot whose record went: not a worktree any more, with read-only files (as git leaves) and a link
        // (as pnpm makes) to something outside it.
        var stray = Path.Combine(_root, "shop-web--1");
        Directory.CreateDirectory(Path.Combine(stray, "objects"));
        var readOnly = Path.Combine(stray, "objects", "pack");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        var outside = Path.Combine(_root, "store");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "kept.txt"), "not the slot's");
        Link(Path.Combine(stray, "node_modules"), outside);

        var broken = _workspaces.BrokenSlotFor(loaded, "feat-x");

        Assert.NotNull(broken);
        Assert.Equal(("1", "shop-web--1", "2", "shop-web--2"), (broken.Slot, broken.SlotName, broken.Next, broken.NextName));
        Assert.Equal([stray], broken.InTheWay);
        Assert.Contains("use shop-web--2 instead", Assert.Throws<WorkspaceException>(() => _workspaces.CheckOut(loaded, "feat-x")).Message);

        // Passed over: the next slot, and the broken one left as it was.
        Assert.Equal("2", _workspaces.CheckOut(loaded, "feat-x", new CheckOutOptions(Slot: int.Parse(broken.Next))).Name);
        Assert.True(File.Exists(readOnly));

        // Fixed: made again where it was, without touching what the link pointed at.
        var fixedSlot = _workspaces.CheckOut(loaded, "feat-y", new CheckOutOptions(FixSlot: true));
        Assert.Equal("1", fixedSlot.Name);
        Assert.Equal("feat-y", _git.CurrentBranch(stray));
        Assert.False(File.Exists(readOnly));
        Assert.True(File.Exists(Path.Combine(outside, "kept.txt")));
        Assert.Null(_workspaces.BrokenSlotFor(loaded, "feat-z"));
    }

    void Link(string link, string target)
    {
        if (OperatingSystem.IsWindows()) _processes.Run("cmd.exe", ["/d", "/c", "mklink", "/J", link, target]).EnsureSuccess();
        else Directory.CreateSymbolicLink(link, target);
    }

    [Fact]
    public void A_free_slot_whose_folder_was_deleted_by_hand_is_made_again()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", Web)) with { Id = "shop-web", Alone = true };
        var dir = _workspaces.CheckOut(loaded, "feat-x").Repos.Single().Dir;
        _workspaces.Free("shop-web", "1");
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);

        var again = _workspaces.CheckOut(loaded, "feat-y");

        Assert.Equal("1", again.Name);
        Assert.Equal("feat-y", _git.CurrentBranch(dir));
    }

    [Fact]
    public void Release_refuses_uncommitted_changes_and_a_branch_checked_out_elsewhere_is_refused()
    {
        var source = Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", Web));
        var record = _workspaces.CheckOut(loaded, "feat-x");
        File.WriteAllText(Path.Combine(record.Repos.Single().Dir, "index.html"), "changed");

        Assert.Contains("aren't committed", Assert.Throws<WorkspaceException>(() => _workspaces.Free("storefront", "1")).Message);
        Assert.Equal("feat-x", _git.CurrentBranch(record.Repos.Single().Dir));

        // main is the repo's own checkout.
        Assert.Contains("checked out in shop-web's own folder", Assert.Throws<WorkspaceException>(() => _workspaces.CheckOut(loaded, "main")).Message);
        Assert.Equal("main", _git.CurrentBranch(source));
    }

    [Fact]
    public void A_switch_made_with_git_in_a_slot_is_what_it_is_labelled()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", Web));
        var record = _workspaces.CheckOut(loaded, "feat-x");
        Git(record.Repos.Single().Dir, "switch", "-q", "-c", "feat-x2");

        Assert.Equal("feat-x2", _workspaces.Up(loaded, "1", null).Branch);
    }

    [Fact]
    public void Two_workspaces_get_their_own_worktrees_and_ports()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", Web));

        _workspaces.Up(loaded, "feat-x", null);
        _workspaces.Up(loaded, "feat-y", "feature/y");

        var x = _workspaces.LastPlan("storefront", "feat-x")!.Ports.Single().Port;
        var y = _workspaces.LastPlan("storefront", "feat-y")!.Ports.Single().Port;
        Assert.NotEqual(x, y);
        Assert.True(_git.BranchExists(Path.Combine(_root, "shop-web"), "feature/y"));
        Assert.Equal(2, _workspaces.List().Count);
    }

    [Fact]
    public void Rm_removes_worktrees_and_the_branch_and_frees_the_ports()
    {
        var source = Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", Web));
        var record = _workspaces.Up(loaded, "feat-x", null);

        _workspaces.Remove("storefront", "feat-x", new RemoveOptions(DeleteBranch: true));

        Assert.False(Directory.Exists(record.Folder));
        Assert.False(_git.BranchExists(source, "feat-x"));
        Assert.Empty(_workspaces.List());
        Assert.Empty(new PortLeases(_store.Paths).All());
    }

    [Fact]
    public void Rm_with_a_file_held_open_says_so_keeps_the_slot_and_finishes_once_it_is_let_go()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Build output is ignored, so it doesn't count as uncommitted changes.
        var source = RepoWith("shop-web", LocalFiles + "\nobj/\n", ("index.html", "hi"));
        var record = _workspaces.Up(Load(("shop-web", Web)), "feat-x", null);
        var held =Path.Combine(record.Repos[0].Dir, "obj", "app.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(held)!);
        File.WriteAllText(held, "x");

        using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var e = Assert.Throws<WorkspaceException>(() => _workspaces.Remove("storefront", "feat-x", new RemoveOptions()));
            Assert.Contains("couldn't be deleted", e.Message);
            Assert.Contains("remove it again", e.Message);
            Assert.Single(_workspaces.List());
        }

        // git has forgotten the worktree by now, so this is sprig deleting what it left.
        _workspaces.Remove("storefront", "feat-x", new RemoveOptions());

        Assert.False(Directory.Exists(record.Repos[0].Dir));
        Assert.False(Directory.Exists(record.Folder));
        Assert.DoesNotContain(_git.Worktrees(source), w => w.Contains("storefront--", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(_workspaces.List());
    }

    [Fact]
    public void Rm_deletes_the_dangling_links_git_leaves_and_says_so_in_its_own_step()
    {
        if (!OperatingSystem.IsWindows()) return;
        var source = RepoWith("shop-web", LocalFiles + "\nnode_modules/\n", ("index.html", "hi"));
        var record = _workspaces.Up(Load(("shop-web", Web)), "feat-x", null);
        var dir = record.Repos[0].Dir;
        // As pnpm lays node_modules out: a package in its store, and a junction to it from an app's node_modules. git
        // deletes the store first, can't remove the junction once it dangles, and still says it succeeded.
        var package = Path.Combine(dir, "node_modules", ".pnpm", "dotenv@17", "node_modules", "dotenv");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "index.js"), "x");
        var link = Path.Combine(dir, "app", "node_modules", "dotenv");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        _processes.Run("cmd", ["/c", "mklink", "/J", link, package]).EnsureSuccess();
        var events = new List<WorkspaceEvent>();

        _workspaces.Remove("storefront", "feat-x", new RemoveOptions(), new SyncProgressList(events));

        Assert.False(Directory.Exists(dir));
        Assert.Empty(_workspaces.List());
        Assert.DoesNotContain(_git.Worktrees(source), w => w.Contains("storefront--", StringComparison.OrdinalIgnoreCase));
        var cleanup = Assert.Single(events, e => e.Step.StartsWith("Clean up ", StringComparison.Ordinal) && e.State == StepState.Done);
        Assert.Contains("1 link", cleanup.Detail);
        Assert.EndsWith("deleted", cleanup.Detail);
    }

    sealed class SyncProgressList(List<WorkspaceEvent> events) : IProgress<WorkspaceEvent>
    {
        public void Report(WorkspaceEvent value) => events.Add(value);
    }

    [Fact]
    public void A_broken_slot_says_what_the_folder_in_its_way_holds()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", Web)) with { Id = "shop-web", Alone = true };
        Directory.CreateDirectory(Path.Combine(_root, "shop-web--1", "frontend", "node_modules", "a"));
        Directory.CreateDirectory(Path.Combine(_root, "shop-web--1", "frontend", "node_modules", "b"));

        var broken = _workspaces.BrokenSlotFor(loaded, "feat-x");

        Assert.NotNull(broken);
        Assert.Contains("holding 4 empty folders", broken.Why);
    }

    [Fact]
    public void Rm_refuses_a_worktree_with_uncommitted_changes_unless_forced()
    {
        Repo("shop-web", ("index.html", "hi"));
        var record = _workspaces.Up(Load(("shop-web", Web)), "feat-x", null);
        File.WriteAllText(Path.Combine(record.Repos[0].Dir, "index.html"), "changed");

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Remove("storefront", "feat-x", new RemoveOptions()));
        Assert.Contains("changes that aren't committed", e.Message);

        _workspaces.Remove("storefront", "feat-x", new RemoveOptions(Force: true));
        Assert.Empty(_workspaces.List());
    }

    [Fact]
    public void Worktree_and_file_status_and_the_removal_blockers_say_what_removing_would_lose()
    {
        Repo("shop-web", ("index.html", "hi"));
        Repo("shop-services", ("src/Api/Program.cs", "// api"));
        var record = _workspaces.Up(Load(("shop-web", Web), ("shop-services", Api)), "feat-x", null);
        var web = record.Repos.Single(r => r.Name == "shop-web");
        var services = record.Repos.Single(r => r.Name == "shop-services");

        Assert.Empty(_workspaces.RemovalBlockers(record, deleteBranch: true));
        Assert.All(_workspaces.WorktreeStatus(record), t => Assert.Equal((true, false, 0), (t.Exists, t.Dirty, t.Unpushed)));
        var files = _workspaces.FileStatus(record);
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.True(f.Ignored && !f.Tracked));

        File.WriteAllText(Path.Combine(web.Dir, "index.html"), "changed");
        File.WriteAllText(Path.Combine(services.Dir, "notes.txt"), "new");
        Git(services.Dir, "add", "-A");
        Git(services.Dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "only here");

        var trees = _workspaces.WorktreeStatus(record).ToDictionary(t => t.Repo);
        Assert.True(trees["shop-web"].Dirty);
        Assert.Equal(1, trees["shop-services"].Unpushed);
        var blockers = _workspaces.RemovalBlockers(record, deleteBranch: true);
        Assert.Equal(2, blockers.Count);
        Assert.Contains("changes that aren't committed", blockers[0]);
        Assert.Contains("commits no other branch or remote has, in shop-services", blockers[1]);
        Assert.Single(_workspaces.RemovalBlockers(record, deleteBranch: false));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Remove("storefront", "feat-x", new RemoveOptions(DeleteBranch: true)));
        Assert.Equal(blockers[0], e.Message);
    }

    [Fact]
    public void A_tracked_target_is_refused_before_any_worktree_is_made()
    {
        Repo("shop-web", ("index.html", "hi"));
        RepoWith("shop-services", ".env.local\n", ("src/Api/Properties/launchSettings.json", """{ "profiles": {} }"""));
        var loaded = Load(("shop-web", Web), ("shop-services", Api));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Up(loaded, "feat-x", null));

        var problem = Assert.Single(e.Problems);
        Assert.StartsWith("git tracks src/Api/Properties/launchSettings.json", problem.Message);
        Assert.Contains(".AppSettings(\"appsettings.Local.json\", …)", problem.Message);
        Assert.EndsWith("WorkspaceServiceTests.cs", problem.Location!.File);
        Assert.False(Directory.Exists(Path.Combine(_root, "storefront--feat-x")));
        Assert.Null(_workspaces.Get("storefront", "feat-x"));
    }

    [Fact]
    public void A_target_the_repo_does_not_ignore_is_refused_with_what_to_add()
    {
        RepoWith("shop-services", "bin/\n", ("src/Api/Program.cs", "// api"));
        var loaded = Load(("shop-services", repo =>
        {
            var api = repo.DotNet("api", "src/Api").Http();
            api.AppSettings("appsettings.Local.json", s => s.Set("Urls", api.Url));
        }));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Up(loaded, "local", null, InPlace));

        var problem = Assert.Single(e.Problems);
        Assert.Contains("src/Api/appsettings.Local.json isn't ignored by git", problem.Message);
        Assert.Contains("Add it to the repo's .gitignore", problem.Message);
        Assert.Contains("AddJsonFile(\"appsettings.Local.json\", optional: true)", problem.Message);
        Assert.False(File.Exists(Path.Combine(_root, "shop-services", "src", "Api", "appsettings.Local.json")));
    }

    [Fact]
    public void In_place_writes_into_the_repo_itself_allows_one_workspace_and_rm_puts_the_files_back()
    {
        var dir = Repo("shop-web", ("index.html", "hi"));
        File.WriteAllText(Path.Combine(dir, ".env.local"), "MINE=1\n");
        var loaded = Load(("shop-web", Web));

        var record = _workspaces.Up(loaded, "local", null, InPlace);

        Assert.True(record.InPlace);
        Assert.Equal(dir, record.Repos[0].Dir);
        Assert.Contains("PORT=", File.ReadAllText(Path.Combine(dir, ".env.local")));
        Assert.Throws<WorkspaceException>(() => _workspaces.Up(loaded, "other", null, InPlace));
        // Brought up again, it stays in place without being told; told otherwise, it says so rather than switching.
        Assert.True(_workspaces.Up(loaded, "local", null).InPlace);
        Assert.Contains("runs its repos in place", Assert.Throws<WorkspaceException>(() => _workspaces.Up(loaded, "local", null, new WorkspaceOptions())).Message);

        _workspaces.Remove("storefront", "local", new RemoveOptions());
        Assert.Equal("MINE=1\n", File.ReadAllText(Path.Combine(dir, ".env.local")));
    }

    [Fact]
    public void Main_runs_in_place_on_the_checkouts_branch_and_refuses_values_a_branch_and_removal()
    {
        var dir = Repo("shop-web", ("index.html", "hi"));
        Git(dir, "switch", "-q", "-c", "develop");
        var loaded = Load(("shop-web", Web));
        Assert.Equal(WorkspaceState.Down, _workspaces.MainOf("storefront", loaded.OrchestrationPath).State);
        Assert.Null(_workspaces.Get("storefront", "main"));

        var record = _workspaces.Up(loaded, "main", null, new WorkspaceOptions());

        Assert.True(record.IsMain);
        Assert.Equal(dir, record.Repos.Single().Dir);
        Assert.Equal("develop", record.CheckoutBranch);
        Assert.Equal("storefront", record.SlotName);
        Assert.NotNull(_workspaces.Get("storefront", "main"));
        Assert.Contains("own values", Assert.Throws<WorkspaceException>(() =>
            _workspaces.Up(loaded, "main", null, values: new Dictionary<string, string> { ["x"] = "1" })).Message);
        Assert.Contains("own values", Assert.Throws<WorkspaceException>(() =>
            _workspaces.SetValues("storefront", "main", new Dictionary<string, string> { ["x"] = "1" })).Message);
        Assert.Contains("slot", Assert.Throws<WorkspaceException>(() => _workspaces.Up(loaded, "main", "feat-x")).Message);
        Assert.Contains("can't be removed", Assert.Throws<WorkspaceException>(() =>
            _workspaces.Remove("storefront", "main", new RemoveOptions())).Message);
    }

    [Fact]
    public void Main_goes_with_its_id_putting_back_what_it_wrote_and_is_nothing_to_remove_before_it_has_come_up()
    {
        var dir = Repo("shop-web", ("index.html", "hi"));
        File.WriteAllText(Path.Combine(dir, ".env.local"), "MINE=1\n");
        var loaded = Load(("shop-web", Web));
        _workspaces.RemoveMain("storefront");

        _workspaces.Up(loaded, "main", null);
        _workspaces.RemoveMain("storefront");

        Assert.Null(_workspaces.Get("storefront", "main"));
        Assert.Equal("MINE=1\n", File.ReadAllText(Path.Combine(dir, ".env.local")));
    }

    [Fact]
    public void Main_with_a_setting_that_has_no_default_says_to_give_it_one_rather_than_asking()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", WebWithKey));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Up(loaded, "main", null));

        Assert.Empty(e.Asks);
        Assert.Contains("api-key", e.Message);
        Assert.Contains("give it a default in the definition", e.Message);
    }

    [Fact]
    public void One_definition_runs_in_a_checkout_at_a_time_and_taking_over_takes_the_other_down()
    {
        var dir = Repo("shop-web", ("index.html", "hi"));
        var own = Load(("shop-web", Web));
        var copy = Load(("shop-web", Web)) with { OrchestrationPath = Path.Combine(_root, "copy", "orchestration.cs"), Id = "shop-web-copy" };
        _workspaces.Up(own, "main", null);

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Up(copy, "main", null));

        Assert.Equal("storefront", e.HeldBy?.Orchestration);
        Assert.Contains($"storefront is up in {dir.Replace('\\', '/')}", e.Message);
        Assert.Equal("storefront", _workspaces.HolderOf(copy)?.Orchestration);
        Assert.Null(_workspaces.Get("shop-web-copy", "main"));

        var taken = _workspaces.Up(copy, "main", null, takeOver: true);

        Assert.Equal(WorkspaceState.Up, taken.State);
        Assert.Equal(WorkspaceState.Down, _workspaces.Get("storefront", "main")!.State);
        // The one taken over says who has the checkout now; one that's down holds nothing.
        Assert.Equal("shop-web-copy", _workspaces.HolderOf(_workspaces.Get("storefront", "main")!)?.Orchestration);
        _workspaces.Down("shop-web-copy", "main");
        Assert.Null(_workspaces.HolderOf(own));
        Assert.Equal(WorkspaceState.Up, _workspaces.Up(own, "main", null).State);
    }

    const string Secrets = ".env.local\n**/Properties/launchSettings.json\nbackend/src/.env\ncerts/\nnot-there.json\n";

    [Fact]
    public void What_git_does_not_have_is_copied_into_a_slots_worktree_and_the_worktrees_own_copy_is_kept()
    {
        var dir = RepoWith("shop-web", Secrets, ("index.html", "hi"));
        Directory.CreateDirectory(Path.Combine(dir, "backend", "src"));
        File.WriteAllText(Path.Combine(dir, "backend", "src", ".env"), "API_KEY=secret\n");
        Directory.CreateDirectory(Path.Combine(dir, "certs", "dev"));
        File.WriteAllText(Path.Combine(dir, "certs", "dev", "cert.pem"), "pem");
        var loaded = Load(("shop-web", repo =>
        {
            Web(repo);
            repo.CopyIntoWorktrees("backend/src/.env").CopyIntoWorktrees("certs").CopyIntoWorktrees("not-there.json");
        }));
        var events = new List<WorkspaceEvent>();

        var record = _workspaces.CheckOut(loaded, "feat-x", progress: new SyncProgressList(events));

        var worktree = record.Repos.Single().Dir;
        Assert.Equal("API_KEY=secret\n", File.ReadAllText(Path.Combine(worktree, "backend", "src", ".env")));
        Assert.Equal("pem", File.ReadAllText(Path.Combine(worktree, "certs", "dev", "cert.pem")));
        Assert.False(_git.HasUncommittedChanges(worktree));
        Assert.Equal("backend/src/.env: copied; certs: copied 1 file; not-there.json: not in the checkout, so there was nothing to copy",
            events.Single(e => e.Step == "Copy into worktrees" && e.State == StepState.Done).Detail);

        // A change made in the worktree stays when it comes up again.
        File.WriteAllText(Path.Combine(worktree, "backend", "src", ".env"), "API_KEY=mine\n");
        events.Clear();
        _workspaces.Up(loaded, record.Name, null, progress: new SyncProgressList(events));
        Assert.Equal("API_KEY=mine\n", File.ReadAllText(Path.Combine(worktree, "backend", "src", ".env")));
        Assert.StartsWith("backend/src/.env: kept the worktree's own, which differs from the checkout's; certs: copied 0, kept 1",
            events.Single(e => e.Step == "Copy into worktrees" && e.State == StepState.Done).Detail);

        // Main runs in the checkout itself, which has them already.
        _workspaces.Free("storefront", record.Name);
        Assert.Equal(WorkspaceState.Up, _workspaces.Up(loaded, "main", null).State);
    }

    [Fact]
    public void A_copy_that_git_tracks_or_does_not_ignore_or_that_leaves_the_repo_is_refused_before_any_worktree_is_made()
    {
        RepoWith("shop-web", Secrets, ("index.html", "hi"), ("settings.json", "{}"));
        File.WriteAllText(Path.Combine(_root, "shop-web", "loose.txt"), "x");
        var loaded = Load(("shop-web", repo =>
        {
            Web(repo);
            repo.CopyIntoWorktrees("settings.json").CopyIntoWorktrees("loose.txt").CopyIntoWorktrees("../elsewhere/.env");
        }));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.CheckOut(loaded, "feat-x"));

        Assert.Collection(e.Problems.Select(p => p.Message),
            m => Assert.StartsWith("git tracks settings.json, so every worktree has it already", m),
            m => Assert.StartsWith("loose.txt isn't ignored by git", m),
            m => Assert.Contains("names something outside the repo", m));
        Assert.False(Directory.Exists(Path.Combine(_root, "storefront--1")));
    }

    [Fact]
    public void A_bad_plan_fails_before_any_worktree_is_made()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", repo => repo.Node("web", ".").Http().WaitFor(Sprig.Repo.Describe("elsewhere").Postgres("ghost"))));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Up(loaded, "feat-x", null));

        Assert.Contains(e.Problems, p => p.Message.Contains("waits for 'ghost'"));
        Assert.False(Directory.Exists(Path.Combine(_root, "storefront--feat-x")));
        Assert.Null(_workspaces.Get("storefront", "feat-x"));
    }

    static void WebWithKey(RepoBuilder repo)
    {
        var web = repo.Node("web", ".").Http();
        web.EnvFile(".env.local", e => e.Set("PORT", web.Port).Set("API_KEY", repo.Setting<Secret>("api-key")).Set("API", repo.Setting<Url>("api-url", "http://localhost:5100")));
    }

    [Fact]
    public void A_setting_with_no_value_is_asked_for_before_anything_is_made_and_a_value_given_is_kept()
    {
        Repo("shop-web", ("index.html", "hi"));
        var loaded = Load(("shop-web", WebWithKey));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Up(loaded, "feat-x", null));
        Assert.Equal(("api-key", "secret", "shop-web"), (Assert.Single(e.Asks).Name, e.Asks[0].Type, e.Asks[0].Owner));
        Assert.Empty(e.Problems);
        Assert.False(Directory.Exists(Path.Combine(_root, "storefront--feat-x")));

        var record = _workspaces.Up(loaded, "feat-x", null, values: new Dictionary<string, string> { ["api-key"] = "k1" });
        var envLocal = Path.Combine(record.Repos.Single().Dir, ".env.local");
        Assert.Contains("API_KEY=k1", File.ReadAllText(envLocal));
        Assert.Contains("API=http://localhost:5100", File.ReadAllText(envLocal));

        // Kept: the next up needs nothing more, and a value set later replaces the default.
        _workspaces.SetValues("storefront", "feat-x", new Dictionary<string, string> { ["api-url"] = "https://api.example.com" });
        var again = _workspaces.Up(loaded, "feat-x", null);
        Assert.Equal(new Dictionary<string, string> { ["api-key"] = "k1", ["api-url"] = "https://api.example.com" }, again.Values);
        Assert.Contains("API=https://api.example.com", File.ReadAllText(envLocal));

        _workspaces.SetValues("storefront", "feat-x", new Dictionary<string, string>(), unset: ["api-url"]);
        _workspaces.Up(loaded, "feat-x", null);
        Assert.Contains("API=http://localhost:5100", File.ReadAllText(envLocal));
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
        var orchestrationFile = Path.Combine(_root, "storefront.orchestration.cs");
        File.WriteAllText(orchestrationFile, "// orchestration");

        var trust = new FileTrustStore(_store.Paths);
        var loader = new OrchestrationLoader(new DefinitionRunner(trust, new ScriptedHost(new()
        {
            ["// orchestration"] = () => { var t = Orchestration.Describe("storefront"); t.Repo("shop-web"); return t; },
            ["// web v1"] = () => WebWith("v1"),
            ["// web v2"] = () => WebWith("v2"),
        }), _store.Paths));
        var settings = new FileSettingsStore(_store.Paths);
        var workspaces = new WorkspaceService(new WorkspaceStore(_store.Paths), new PortLeases(_store.Paths), settings, _git,
            new DockerCompose(_processes), listening: () => new HashSet<int>(), loader: loader);
        var mainDefinition = Path.Combine(source, "sprig.cs");
        trust.Approve(orchestrationFile, DefinitionHash.Of("// orchestration"));
        trust.Approve(mainDefinition, DefinitionHash.Of("// web v1"));
        var loaded = loader.Load(orchestrationFile);
        Assert.True(loaded.Complete);

        var e = Assert.Throws<WorkspaceException>(() => workspaces.Up(loaded, "feat-x", null));

        var pending = Assert.Single(e.Untrusted);
        var worktree = Path.Combine(_root, "storefront--feat-x", "shop-web");
        Assert.Equal(Path.Combine(worktree, "sprig.cs"), pending.Path);
        Assert.Equal(mainDefinition, pending.TrustPath);
        Assert.Equal(mainDefinition, workspaces.TrustPathFor(pending.Path));
        Assert.False(File.Exists(Path.Combine(worktree, ".env.local")));

        trust.Approve(pending.TrustPath, pending.Sha256);
        var record = workspaces.Up(loaded, "feat-x", null);

        Assert.Equal(WorkspaceState.Up, record.State);
        Assert.Contains("FEATURE=v2", File.ReadAllText(Path.Combine(worktree, ".env.local")));
        // The main checkout's version stays approved alongside the branch's.
        Assert.IsType<Evaluated>(loader.Load(orchestrationFile).Results[1]);
    }

    [Fact]
    public void Orchestrations_and_repo_copies_kept_in_sprig_sit_beside_a_repos_own_without_clashing()
    {
        var source = Repo("shop-web", ("index.html", "hi"), ("sprig.cs", "// web v1"));
        var stored = new StoredDefinitions(_store.Paths);
        var registry = new OrchestrationRegistry(_store.Paths);
        var trust = new FileTrustStore(_store.Paths);
        var loader = new OrchestrationLoader(new DefinitionRunner(trust, new ScriptedHost(new()
        {
            ["// own"] = () => { var t = Orchestration.Describe("storefront"); t.Repo(source); return t; },
            ["// uses a"] = () => { var t = Orchestration.Describe("storefront"); t.UseRepo("web-a"); return t; },
            ["// uses b"] = () => { var t = Orchestration.Describe("storefront"); t.UseRepo("web-b"); return t; },
            ["// uses missing"] = () => { var t = Orchestration.Describe("storefront"); t.UseRepo("nope"); return t; },
            ["// web v1"] = () => WebWith("v1"),
            ["// copy a"] = () => WebWith("a"),
            ["// copy b"] = () => WebWith("b"),
        }), _store.Paths), registry, stored);
        var workspaces = new WorkspaceService(new WorkspaceStore(_store.Paths), new PortLeases(_store.Paths), new FileSettingsStore(_store.Paths), _git,
            new DockerCompose(_processes), listening: () => new HashSet<int>(), loader: loader);

        string KeptOrchestration(string id, string text)
        {
            var file = stored.CreateOrchestration(id, text);
            registry.Add(id, file);
            trust.Approve(file, DefinitionHash.Of(text));
            return file;
        }
        var own = KeptOrchestration("own", "// own");
        var usesA = KeptOrchestration("uses-a", "// uses a");
        var usesB = KeptOrchestration("uses-b", "// uses b");
        foreach (var (id, text) in new[] { ("web-a", "// copy a"), ("web-b", "// copy b") })
            trust.Approve(stored.AddRepo(id, source, inRepo: false, text).DefinitionFile, DefinitionHash.Of(text));
        trust.Approve(Path.Combine(source, "sprig.cs"), DefinitionHash.Of("// web v1"));

        var feature = new Dictionary<string, string>();
        foreach (var (file, workspace) in new[] { (own, "x1"), (usesA, "x2"), (usesB, "x3") })
        {
            var record = workspaces.Up(loader.Load(file), workspace, null);
            feature[record.Orchestration] = string.Join(",", File.ReadAllLines(Path.Combine(record.Repos.Single().Dir, ".env.local"))
                .Where(l => l.StartsWith("FEATURE=")).Distinct());
        }

        Assert.Equal("FEATURE=v1", feature["own"]);
        Assert.Equal("FEATURE=a", feature["uses-a"]);
        Assert.Equal("FEATURE=b", feature["uses-b"]);
        Assert.Equal("// web v1", File.ReadAllText(Path.Combine(source, "sprig.cs")));
        Assert.Equal(["web-a", "web-b"], stored.Repos().Select(r => r.Id));

        var missing = KeptOrchestration("uses-missing", "// uses missing");
        var problem = Assert.Single(loader.Load(missing).Problems);
        Assert.Contains("'nope' isn't in sprig's Repos list", problem.Message);

        // A copy kept in sprig is approved as itself, and once approved it's the user's own: changing it, in the app
        // or anywhere else, doesn't ask again.
        File.WriteAllText(stored.CopyFile("web-a"), "// copy b");
        Assert.Empty(loader.Load(usesA).Untrusted);
    }

    [Fact]
    public void A_repo_used_by_id_brings_its_folder_and_reads_its_own_sprig_cs_from_the_workspaces_worktree()
    {
        var source = Repo("shop-web", ("index.html", "hi"), ("sprig.cs", "// web v1"));
        var stored = new StoredDefinitions(_store.Paths);
        var trust = new FileTrustStore(_store.Paths);
        var loader = new OrchestrationLoader(new DefinitionRunner(trust, new ScriptedHost(new()
        {
            ["// orchestration"] = () => { var t = Orchestration.Describe("storefront"); t.UseRepo("web"); return t; },
            ["// uses nothing"] = () => { var t = Orchestration.Describe("storefront"); t.UseRepo("nope"); return t; },
            ["// web v1"] = () => WebWith("v1"),
        }), _store.Paths), stored: stored);
        var workspaces = new WorkspaceService(new WorkspaceStore(_store.Paths), new PortLeases(_store.Paths), new FileSettingsStore(_store.Paths), _git,
            new DockerCompose(_processes), listening: () => new HashSet<int>(), loader: loader);
        stored.AddRepo("web", source, inRepo: true, definition: null);
        var orchestrationFile = Path.Combine(_root, "storefront.orchestration.cs");
        File.WriteAllText(orchestrationFile, "// orchestration");
        trust.Approve(orchestrationFile, DefinitionHash.Of("// orchestration"));
        trust.Approve(Path.Combine(source, "sprig.cs"), DefinitionHash.Of("// web v1"));

        var record = workspaces.Up(loader.Load(orchestrationFile), "feat-x", null);

        var repo = Assert.Single(record.Repos);
        Assert.Equal("web", repo.RepoPath);
        Assert.Equal(source, repo.Source);
        Assert.Equal(Path.Combine(_root, "storefront--feat-x", "shop-web"), repo.Dir);
        Assert.Contains("FEATURE=v1", File.ReadAllText(Path.Combine(repo.Dir, ".env.local")));

        var missingFile = Path.Combine(_root, "missing.orchestration.cs");
        File.WriteAllText(missingFile, "// uses nothing");
        trust.Approve(missingFile, DefinitionHash.Of("// uses nothing"));
        Assert.Contains("'nope' isn't in sprig's Repos list", Assert.Single(loader.Load(missingFile).Problems).Message);
    }

    [Fact]
    public void A_repo_on_the_list_runs_on_its_own_under_its_id_and_ids_are_shared_with_orchestrations()
    {
        var source = Repo("shop-web", ("index.html", "hi"), ("sprig.cs", "// web v1"));
        var stored = new StoredDefinitions(_store.Paths);
        var registry = new OrchestrationRegistry(_store.Paths);
        var trust = new FileTrustStore(_store.Paths);
        var loader = new OrchestrationLoader(new DefinitionRunner(trust, new ScriptedHost(new()
        {
            ["// web v1"] = () => WebWith("v1"),
        }), _store.Paths), registry, stored);
        var workspaces = new WorkspaceService(new WorkspaceStore(_store.Paths), new PortLeases(_store.Paths), new FileSettingsStore(_store.Paths), _git,
            new DockerCompose(_processes), listening: () => new HashSet<int>(), loader: loader);
        stored.AddRepo("web", source, inRepo: true, definition: null);
        trust.Approve(Path.Combine(source, "sprig.cs"), DefinitionHash.Of("// web v1"));

        var entry = registry.Resolve("web");
        Assert.Equal(stored.EntryFile("web"), entry);
        var loaded = loader.Load(entry!);
        Assert.True(loaded.Complete, string.Join("\n", loaded.Problems.Select(p => p.Message)));
        Assert.True(loaded.Alone);
        Assert.Equal("web", loaded.Id);

        var record = workspaces.Up(loaded, "feat-x", null);
        Assert.Equal("web/feat-x", record.Key);
        // On its own, the repo's worktree is the workspace's folder, with nothing inside to dig into.
        Assert.Equal(Path.Combine(_root, "web--feat-x"), Assert.Single(record.Repos).Dir);
        Assert.Contains("FEATURE=v1", File.ReadAllText(Path.Combine(record.Repos[0].Dir, ".env.local")));
        // Brought up again by its orchestration path, as the app does, it reads its definition from its worktree.
        Assert.Equal(record.Repos[0].Dir, workspaces.Worktrees(entry!, "feat-x")!["web"]);
        Assert.Equal(WorkspaceState.Up, workspaces.Up(loader.Load(entry!, workspaces.Worktrees(entry!, "feat-x")), "feat-x", null).State);

        // An orchestration can't take a repo's ID, nor a repo an orchestration's.
        Assert.Throws<ArgumentException>(() => registry.Add("web", Path.Combine(_root, "x.orchestration.cs")));
        Assert.Equal("web-2", registry.UnusedId("web"));
        registry.Add("storefront", Path.Combine(_root, "storefront.orchestration.cs"));
        Assert.Equal("storefront-2", stored.UnusedRepoId("storefront"));
        Assert.Throws<ArgumentException>(() => stored.AddRepo("storefront", source, inRepo: true, definition: null));

        // Once the repo is off the list, its workspace says so rather than failing to read.
        stored.DeleteRepo("web");
        Assert.Contains("'web' isn't in sprig's Repos list", Assert.Single(loader.Load(entry!).Problems).Message);
    }

    [Fact]
    public void A_new_branch_from_main_needs_no_new_approval()
    {
        var source = Repo("shop-web", ("index.html", "hi"), ("sprig.cs", "// web v1"));
        var orchestrationFile = Path.Combine(_root, "storefront.orchestration.cs");
        File.WriteAllText(orchestrationFile, "// orchestration");
        var trust = new FileTrustStore(_store.Paths);
        var loader = new OrchestrationLoader(new DefinitionRunner(trust, new ScriptedHost(new()
        {
            ["// orchestration"] = () => { var t = Orchestration.Describe("storefront"); t.Repo("shop-web"); return t; },
            ["// web v1"] = () => WebWith("v1"),
        }), _store.Paths));
        var workspaces = new WorkspaceService(new WorkspaceStore(_store.Paths), new PortLeases(_store.Paths), new FileSettingsStore(_store.Paths), _git,
            new DockerCompose(_processes), listening: () => new HashSet<int>(), loader: loader);
        trust.Approve(orchestrationFile, DefinitionHash.Of("// orchestration"));
        trust.Approve(Path.Combine(source, "sprig.cs"), DefinitionHash.Of("// web v1"));

        var record = workspaces.Up(loader.Load(orchestrationFile), "feat-y", null);

        Assert.Contains("FEATURE=v1", File.ReadAllText(Path.Combine(record.Repos[0].Dir, ".env.local")));
        Assert.Equal(record.Repos[0].Dir, workspaces.Worktrees(orchestrationFile, "feat-y")!["shop-web"]);
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
    public void Workspace_names_are_checked(string name, bool ok) => Assert.Equal(ok, WorkspaceService.CheckName(name) is null);
}
