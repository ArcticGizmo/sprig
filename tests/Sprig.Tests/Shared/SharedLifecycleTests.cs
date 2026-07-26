using System.Collections.Generic;
using System.Linq;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Shared;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Shared;

/// <summary>
/// The full lifecycle against a managed resource: attach at create, refcount between up and down, detach
/// at rm. The two counters are deliberately different — capacity counts <b>attached</b> workspaces, while
/// the container follows <b>running</b> ones, and "running" is answered by asking docker rather than by
/// trusting a record.
/// </summary>
public class SharedLifecycleTests
{
    const string ApiConfig = """
        {
          "schema": 2, "name": "api",
          "inputs": [ { "name": "port", "example": "5000" }, { "name": "dbPort", "example": "5432" } ],
          "env": [ { "file": ".env", "set": { "DB": "postgres://localhost:${sprig.dbPort}/librarydb" } } ],
          "compose": [ { "file": "docker-compose.yml", "overrides": [] } ]
        }
        """;

    const string ComposeYaml = """
        services:
          api:
            image: api:latest
        """;

    sealed class Harness : IDisposable
    {
        public TempStore Store { get; } = new();
        public FakeDockerService Docker { get; } = new();
        public SharedInfrastructure Shared { get; }
        public WorkspaceService Svc { get; }

        public Harness()
        {
            var resources = new SharedResourceStore(Store.Paths);
            var leases = new SharedLeaseStore(Store.Paths);
            var runner = new SharedResourceRunner(Docker, resources, leases, Store.Paths)
            {
                ReadyTimeoutSeconds = 1,
                Delay = _ => { },   // no real waiting in tests
            };
            Shared = new SharedInfrastructure(resources, leases, runner);
            Svc = new WorkspaceService(
                new GitService(new ProcessRunner()), new FilePortStore(Store.Paths),
                new InstanceStore(Store.Paths), new EnvClobberService(), new ComposeGenerator(),
                Docker, Store.Paths, null, Shared);

            Directory.CreateDirectory(Store.Paths.SharedDir);
            File.WriteAllText(Path.Combine(Store.Paths.SharedDir, "postgres-16.compose.yml"), """
                services:
                  db:
                    image: postgres:16
                """);
        }

        public void Save(SharedResourceDefinition resource) => Shared.Resources.Save(resource);
        public void Dispose() => Store.Dispose();
    }

    static SharedResourceDefinition Postgres(int capacity = 5, string whenIdle = "stop") => new()
    {
        Name = "postgres-16",
        Capacity = capacity,
        WhenIdle = whenIdle,
        Compose = "postgres-16.compose.yml",
        ExecService = "db",
        Values = new Dictionary<string, string>
        {
            ["port"] = "5432",
            ["database"] = "sprig_${sprig.workspace}",
        },
        Attach = ["""psql -U sprig -c 'CREATE DATABASE "${sprig.shared.database}"'"""],
        Detach = ["""psql -U sprig -c 'DROP DATABASE IF EXISTS "${sprig.shared.database}"'"""],
        Injects =
        [
            new ResourceInjection
            {
                Repo = "api",
                Inputs = new Dictionary<string, string> { ["dbPort"] = "${sprig.shared.port}" },
            },
        ],
    };

    static ResolvedStack Stack(TempGitRepo repo)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), ApiConfig);
        File.WriteAllText(Path.Combine(repo.Path, "docker-compose.yml"), ComposeYaml);
        var config = SprigConfigLoader.LoadFromFile(Path.Combine(repo.Path, ".sprig.json"));
        return new ResolvedStack("api", [new ResolvedRepo(config.Name, repo.Path, config)],
            ["api_port", "postgres_port"],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["api"] = new Dictionary<string, string>
                {
                    ["port"] = "${sprig.ports.api_port}",
                    ["dbPort"] = "${sprig.ports.postgres_port}",
                },
            });
    }

    [Fact]
    public void Create_attaches_a_slot_and_carves_out_the_workspaces_database()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres());

        var record = h.Svc.Create(Stack(repo), "feature-x");

        Assert.Equal(["postgres-16"], record.AppliedOverlays);
        var slot = Assert.Single(record.Slots);
        Assert.Equal(1, slot.Slot);
        Assert.Equal("sprig_feature-x", Assert.Single(slot.Namespaces).Values["database"]);

        // The container came up and the attach command ran against the right service.
        Assert.Contains("sprig-shared-postgres-16", h.Docker.Ups);
        var exec = Assert.Single(h.Docker.Execs);
        Assert.Equal("db", exec.service);
        Assert.Contains("CREATE DATABASE", exec.command);
        Assert.Contains("sprig_feature-x", exec.command);
    }

    [Fact]
    public void Create_fails_before_any_worktree_when_the_pool_is_full()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres(capacity: 1));
        h.Svc.Create(Stack(repo), "first");

        var ex = Assert.Throws<SharedCapacityException>(() => h.Svc.Create(Stack(repo), "second"));

        Assert.Contains("postgres-16 is full", ex.Message);
        Assert.False(Directory.Exists(repo.SiblingWorktree("second")));
        Assert.Null(h.Svc.Get("second"));
        Assert.DoesNotContain(new FilePortStore(h.Store.Paths).ListLeases(), l => l.Workspace == "second");
    }

    [Fact]
    public void No_shared_takes_no_slot_at_all()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres(capacity: 1));

        var record = h.Svc.Create(Stack(repo), "private-x", null, new CreateOptions { NoShared = true });

        Assert.Empty(record.Slots);
        Assert.Empty(h.Shared.Leases.List("postgres-16"));
        Assert.Empty(h.Docker.Execs);
        // And the slot is still there for someone who does want it.
        h.Svc.Create(Stack(repo), "pooled-x");
        Assert.Single(h.Shared.Leases.List("postgres-16"));
    }

    [Fact]
    public void The_last_workspace_down_stops_the_container_and_the_others_dont()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres());
        h.Svc.Create(Stack(repo), "one");
        h.Svc.Create(Stack(repo), "two");
        h.Svc.Up("one");
        h.Svc.Up("two");

        var first = Assert.Single(h.Svc.Down("one"));
        Assert.False(first.Stopped);
        Assert.Equal(["two"], first.StillUsedBy);   // asking docker, not reading a record

        var last = Assert.Single(h.Svc.Down("two"));
        Assert.True(last.Stopped);
        Assert.Empty(last.StillUsedBy);
        Assert.Contains(("sprig-shared-postgres-16", false), h.Docker.Downs);
    }

    [Fact]
    public void WhenIdle_keep_leaves_the_container_up_for_the_next_start()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres(whenIdle: "keep"));
        h.Svc.Create(Stack(repo), "one");
        h.Svc.Up("one");

        var outcome = Assert.Single(h.Svc.Down("one"));

        Assert.False(outcome.Stopped);
        Assert.DoesNotContain(("sprig-shared-postgres-16", false), h.Docker.Downs);
    }

    [Fact]
    public void Up_starts_the_shared_resource_before_the_workspaces_own_containers()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres());
        h.Svc.Create(Stack(repo), "feature-x");
        h.Docker.Ups.Clear();

        h.Svc.Up("feature-x");

        Assert.Equal(["sprig-shared-postgres-16", "sprig-feature-x"], h.Docker.Ups);
    }

    [Fact]
    public void Rm_drops_this_workspaces_database_and_frees_its_slot()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres());
        h.Svc.Create(Stack(repo), "feature-x");
        h.Docker.Execs.Clear();

        h.Svc.Remove("feature-x", force: true);

        var exec = Assert.Single(h.Docker.Execs);
        Assert.Contains("DROP DATABASE", exec.command);
        Assert.Contains("sprig_feature-x", exec.command);
        Assert.Empty(h.Shared.Leases.List("postgres-16"));
    }

    // down --volumes wipes this workspace's own data. A flag on one workspace must never delete another's
    // database, so the shared project is only ever brought down without -v.
    [Fact]
    public void Down_with_volumes_never_wipes_the_shared_volume()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres());
        h.Svc.Create(Stack(repo), "feature-x");
        h.Svc.Up("feature-x");

        h.Svc.Down("feature-x", removeVolumes: true);

        Assert.Contains(("sprig-feature-x", true), h.Docker.Downs);
        Assert.Contains(("sprig-shared-postgres-16", false), h.Docker.Downs);
        Assert.DoesNotContain(("sprig-shared-postgres-16", true), h.Docker.Downs);
    }

    [Fact]
    public void A_failed_attach_rolls_the_whole_create_back()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres());
        h.Docker.ExecFails = cmd => cmd.Contains("CREATE DATABASE");

        Assert.Throws<SharedResourceException>(() => h.Svc.Create(Stack(repo), "doomed"));

        Assert.Null(h.Svc.Get("doomed"));
        Assert.False(Directory.Exists(repo.SiblingWorktree("doomed")));
        Assert.Empty(h.Shared.Leases.List("postgres-16"));   // the slot went back
    }

    // Deleting a resource is the one operation that destroys data belonging to more than one workspace,
    // so it has to actually do it — "removed" that leaves a running postgres and an orphaned volume is
    // worse than either outcome on its own.
    [Fact]
    public void Destroy_takes_the_container_and_its_volumes()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres());
        h.Svc.Create(Stack(repo), "feature-x");

        h.Shared.Runner.Destroy(Postgres());

        Assert.Contains(("sprig-shared-postgres-16", true), h.Docker.Downs);
    }

    // Every ordinary lifecycle path keeps volumes. Only Destroy doesn't.
    [Fact]
    public void No_ordinary_lifecycle_step_ever_wipes_the_shared_volume()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres());
        h.Svc.Create(Stack(repo), "feature-x");
        h.Svc.Up("feature-x");
        h.Svc.Down("feature-x", removeVolumes: true);
        h.Svc.Remove("feature-x", force: true);

        Assert.DoesNotContain(h.Docker.Downs, d => d.project == "sprig-shared-postgres-16" && d.volumes);
    }

    // I3: a workspace materialises against the overlays pinned on its record. Toggling a resource off
    // must not make an existing workspace's teardown try to release a slot it never held, or vice versa.
    [Fact]
    public void Disabling_a_resource_leaves_an_existing_workspace_working()
    {
        using var h = new Harness();
        using var repo = new TempGitRepo("api");
        h.Save(Postgres());
        h.Svc.Create(Stack(repo), "feature-x");

        h.Save(Postgres() with { Enabled = false });

        // The live workspace still knows what it was built on...
        var record = h.Svc.Get("feature-x")!;
        Assert.Equal(["postgres-16"], record.AppliedOverlays);
        h.Svc.Up("feature-x");
        Assert.Contains("sprig-shared-postgres-16", h.Docker.Ups);

        // ...while a new one is built without it.
        var plain = h.Svc.Create(Stack(repo), "plain-x");
        Assert.Empty(plain.Slots);
        Assert.True(plain.Ports.ContainsKey("postgres_port"));
    }
}
