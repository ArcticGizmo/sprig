using Sprig.Core.Compose;
using Sprig.Core.Demo;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Demo;

/// <summary>
/// End-to-end cover for the guided tour's seeder. This is the only test in the suite that drives
/// repo → stack → workspace as one continuous path, using the exact fixtures a user is shown — so it
/// doubles as the integration test for the create pipeline (docs/guided-tour-plan.md §7, §8).
///
/// Real git (the sample repos are really initialised and committed); fake Docker, because
/// <see cref="WorkspaceService.Create"/> only generates compose files and never starts containers.
/// </summary>
public class SampleSetupTests
{
    /// <summary>A demo store in a temp dir, wired exactly as the app wires the real one.</summary>
    sealed class Harness : IDisposable
    {
        public string Root { get; }
        public ISprigPaths Paths { get; }
        public SampleSetup Sample { get; }
        public WorkspaceService Workspaces { get; }
        public RepoRegistryStore Repos { get; }
        public StackStore Stacks { get; }
        public FakeDockerService Docker { get; } = new() { Available = false };

        public Harness()
        {
            Root = Path.Combine(Path.GetTempPath(), "sprig-demo-test-" + Guid.NewGuid().ToString("N"));
            Paths = new SprigPaths(Root);

            var runner = new ProcessRunner();
            var git = new GitService(runner);
            var instances = new InstanceStore(Paths);
            Repos = new RepoRegistryStore(Paths);
            Stacks = new StackStore(Paths, Repos, instances);
            Workspaces = new WorkspaceService(git, new FilePortStore(Paths), instances,
                new EnvClobberService(), new ComposeGenerator(), Docker, Paths);
            Sample = new SampleSetup(Paths, runner, Repos, Stacks,
                new StackResolver(Repos, Stacks, git), Workspaces);
        }

        public void Dispose()
        {
            try { Sample.Destroy(); }
            catch { /* the tests assert on cleanup themselves; never fail teardown */ }
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Build_produces_two_repos_three_ports_and_a_worktree_each()
    {
        using var h = new Harness();

        var record = h.Sample.Build();

        Assert.Equal(SampleSetup.WorkspaceName, record.Workspace);
        Assert.Equal(SampleFixtures.StackName, record.Stack);
        Assert.Equal(2, record.Repos.Count);
        Assert.Equal(3, record.Ports.Count);

        foreach (var repo in record.Repos)
        {
            Assert.True(Directory.Exists(repo.WorktreePath), $"missing worktree for {repo.Name}");
            Assert.Equal($"sprig/{SampleSetup.WorkspaceName}", repo.Branch);
        }

        // Registered through the real registry, so the names stacks bind by are the configs' names.
        Assert.Equal(
            [SampleFixtures.ApiRepo, SampleFixtures.WebRepo],
            h.Repos.List().Select(r => r.Name));
    }

    [Fact]
    public void Both_repos_receive_the_same_api_port_through_different_inputs()
    {
        using var h = new Harness();

        var record = h.Sample.Build();
        var apiPort = record.Ports[SampleFixtures.ApiPort];

        var api = record.Repos.Single(r => r.Name == SampleFixtures.ApiRepo);
        var web = record.Repos.Single(r => r.Name == SampleFixtures.WebRepo);

        // The API gets the bare number; the web app gets a URL built from it. One port, two shapes —
        // the whole point of the tour's shared-port step.
        Assert.Equal(apiPort.ToString(), api.Inputs["port"]);
        Assert.Equal($"http://localhost:{apiPort}", web.Inputs["apiUrl"]);
    }

    [Fact]
    public void Env_files_are_seeded_from_the_template_then_clobbered_with_allocated_values()
    {
        using var h = new Harness();

        var record = h.Sample.Build();
        var apiPort = record.Ports[SampleFixtures.ApiPort];
        var dbPort = record.Ports[SampleFixtures.DbPort];
        var webPort = record.Ports[SampleFixtures.WebPort];

        var apiEnv = File.ReadAllText(Path.Combine(
            record.Repos.Single(r => r.Name == SampleFixtures.ApiRepo).WorktreePath, ".env"));
        var webEnv = File.ReadAllText(Path.Combine(
            record.Repos.Single(r => r.Name == SampleFixtures.WebRepo).WorktreePath, ".env"));

        Assert.Contains($"PORT={apiPort}", apiEnv);
        Assert.Contains($"localhost:{dbPort}/sample", apiEnv);
        Assert.Contains($"PORT={webPort}", webEnv);
        Assert.Contains($"VITE_API_URL=http://localhost:{apiPort}", webEnv);

        // Seeded from .env.template, so keys the stack does NOT supply survive.
        Assert.Contains("APP_NAME=sample-api", apiEnv);
        Assert.Contains("LOG_LEVEL=debug", apiEnv);

        // No unresolved placeholders anywhere — the tour would be showing a bug.
        Assert.DoesNotContain("${sprig.", apiEnv);
        Assert.DoesNotContain("${sprig.", webEnv);
    }

    [Fact]
    public void Compose_is_generated_for_the_api_with_the_db_port_rewritten()
    {
        using var h = new Harness();

        var record = h.Sample.Build();
        var dbPort = record.Ports[SampleFixtures.DbPort];

        var api = record.Repos.Single(r => r.Name == SampleFixtures.ApiRepo);
        var compose = Assert.Single(api.ComposePaths);
        Assert.True(File.Exists(compose), $"generated compose missing: {compose}");

        var yaml = File.ReadAllText(compose);
        Assert.Contains($"{dbPort}:5432", yaml);
        Assert.Contains($"sample-db--{SampleSetup.WorkspaceName}", yaml);

        // The source repo's own compose is untouched — sprig only ever writes the copy.
        var source = File.ReadAllText(Path.Combine(api.SourcePath, "docker-compose.yml"));
        Assert.Contains("\"5432:5432\"", source);
        Assert.Contains("container_name: sample-db", source);

        // The web app declares no compose, so it gets none. A repo without infra is normal.
        Assert.Empty(record.Repos.Single(r => r.Name == SampleFixtures.WebRepo).ComposePaths);
    }

    [Fact]
    public void Build_is_idempotent_and_reuses_an_existing_sample()
    {
        using var h = new Harness();

        var first = h.Sample.Build();
        var second = h.Sample.Build();

        Assert.Equal(first.CreatedAt, second.CreatedAt);
        Assert.Equal(first.Ports, second.Ports);
        Assert.Single(h.Workspaces.List());
        Assert.Equal(2, h.Repos.List().Count);
    }

    [Fact]
    public void Build_rebuilds_after_the_worktrees_are_deleted_out_from_under_it()
    {
        using var h = new Harness();

        var first = h.Sample.Build();
        foreach (var repo in first.Repos)
            Directory.Delete(repo.WorktreePath, recursive: true);

        Assert.Null(h.Sample.Existing());

        var rebuilt = h.Sample.Build();

        Assert.Single(h.Workspaces.List());
        foreach (var repo in rebuilt.Repos)
            Assert.True(Directory.Exists(repo.WorktreePath), $"missing worktree for {repo.Name}");
    }

    [Fact]
    public void Destroy_removes_the_whole_demo_store_including_the_sample_repos()
    {
        using var h = new Harness();
        var record = h.Sample.Build();
        var worktrees = record.Repos.Select(r => r.WorktreePath).ToList();

        h.Sample.Destroy();

        Assert.False(Directory.Exists(h.Root), "demo store root survived Destroy");
        foreach (var worktree in worktrees)
            Assert.False(Directory.Exists(worktree), $"worktree survived Destroy: {worktree}");
        Assert.Null(h.Sample.Existing());
    }

    [Fact]
    public void Destroy_is_safe_on_a_store_that_was_never_built()
    {
        using var h = new Harness();
        h.Sample.Destroy();
        Assert.False(Directory.Exists(h.Root));
    }

    [Fact]
    public void Destroy_refuses_a_root_that_is_not_a_demo_store()
    {
        using var h = new Harness();
        // A directory that looks like a store but carries no marker — e.g. someone pointed the demo
        // root at their real store by mistake. It must survive untouched.
        Directory.CreateDirectory(h.Root);
        var real = Path.Combine(h.Root, "repos.json");
        File.WriteAllText(real, "{}");

        Assert.Throws<SampleSetupException>(h.Sample.Destroy);
        Assert.True(File.Exists(real), "Destroy deleted a store it did not own");
    }

    [Fact]
    public void Build_marks_the_store_before_doing_anything_else()
    {
        using var h = new Harness();

        h.Sample.Build();

        // The marker must exist for Destroy to be willing to clean up, including after a failure
        // partway through — so Build writes it first, not last.
        Assert.True(File.Exists(Path.Combine(h.Root, SampleSetup.MarkerFileName)));
    }

    [Fact]
    public void Build_needs_no_docker()
    {
        using var h = new Harness();
        h.Docker.Available = false;

        h.Sample.Build();

        Assert.Empty(h.Docker.Ups);
    }
}
