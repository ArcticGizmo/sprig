using System.Collections.Generic;
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
/// The overlay end to end: a real workspace created against a machine-local shared resource, and the same
/// workspace created with <c>--no-shared</c>. Both must work, because "off" being a valid configuration is
/// the property the whole approach is built to protect.
/// </summary>
public class SharedOverlayCreateTests
{
    const string ApiConfig = """
        {
          "schema": 2, "name": "api",
          "inputs": [ { "name": "port", "example": "5000" }, { "name": "dbPort", "example": "5432" } ],
          "env": [ { "file": ".env", "set": {
              "PORT": "${sprig.port}",
              "DB": "postgres://localhost:${sprig.dbPort}/librarydb"
          } } ]
        }
        """;

    static (WorkspaceService Svc, SharedResourceStore Resources) Build(TempStore s)
    {
        // No compose fragment on these resources, so nothing here needs docker: the overlay rewrites
        // values, and the container lifecycle has nothing to manage.
        var shared = new SharedInfrastructure(s.Paths, new FakeDockerService { Available = false });
        var svc = new WorkspaceService(
            new GitService(new ProcessRunner()), new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false },
            s.Paths, null, shared);
        return (svc, shared.Resources);
    }

    static ResolvedStack Stack(TempGitRepo repo)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), ApiConfig);
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

    static SharedResourceDefinition Postgres() => new()
    {
        Name = "postgres-16",
        Values = new Dictionary<string, string>
        {
            ["port"] = "5432",
            ["database"] = "sprig_${sprig.workspace}",
        },
        Injects =
        [
            new ResourceInjection
            {
                Repo = "api",
                Inputs = new Dictionary<string, string> { ["dbPort"] = "${sprig.shared.port}" },
                Env =
                [
                    new InjectedEnv
                    {
                        File = ".env",
                        Set = new Dictionary<string, string>
                        {
                            ["DB"] = "postgres://localhost:${sprig.shared.port}/${sprig.shared.database}",
                        },
                    },
                ],
            },
        ],
    };

    [Fact]
    public void A_created_workspace_points_at_the_shared_resource_and_frees_the_port()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo("api");
        var (svc, resources) = Build(store);
        resources.Save(Postgres());

        var record = svc.Create(Stack(repo), "feature-x");

        Assert.Equal("5432", record.Repos[0].Inputs["dbPort"]);
        Assert.False(record.Ports.ContainsKey("postgres_port"));   // nothing references it any more
        Assert.True(record.Ports.ContainsKey("api_port"));

        var env = File.ReadAllText(Path.Combine(repo.SiblingWorktree("feature-x"), ".env"));
        Assert.Contains("DB=postgres://localhost:5432/sprig_feature-x", env);
        Assert.Contains($"PORT={record.Ports["api_port"]}", env);
    }

    [Fact]
    public void No_shared_gives_you_exactly_the_workspace_you_would_have_had()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo("api");
        var (svc, resources) = Build(store);
        resources.Save(Postgres());

        var record = svc.Create(Stack(repo), "private-x", null, new CreateOptions { NoShared = true });

        var dbPort = record.Ports["postgres_port"];
        Assert.Equal(dbPort.ToString(), record.Repos[0].Inputs["dbPort"]);

        var env = File.ReadAllText(Path.Combine(repo.SiblingWorktree("private-x"), ".env"));
        Assert.Contains($"DB=postgres://localhost:{dbPort}/librarydb", env);
    }

    [Fact]
    public void A_disabled_resource_leaves_creates_alone()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo("api");
        var (svc, resources) = Build(store);
        resources.Save(Postgres() with { Enabled = false });

        var record = svc.Create(Stack(repo), "feature-y");

        Assert.True(record.Ports.ContainsKey("postgres_port"));
        Assert.Equal(record.Ports["postgres_port"].ToString(), record.Repos[0].Inputs["dbPort"]);
    }

    [Fact]
    public void An_overlay_whose_target_has_moved_fails_before_any_worktree_exists()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo("api");
        var (svc, resources) = Build(store);
        var broken = Postgres();
        resources.Save(broken with
        {
            Injects =
            [
                broken.Injects[0] with
                {
                    Env =
                    [
                        new InjectedEnv
                        {
                            File = ".env",
                            Set = new Dictionary<string, string> { ["RENAMED_AWAY"] = "x" },
                        },
                    ],
                },
            ],
        });

        Assert.Throws<SharedResourceException>(() => svc.Create(Stack(repo), "doomed"));
        Assert.False(Directory.Exists(repo.SiblingWorktree("doomed")));
        Assert.Null(svc.Get("doomed"));
    }

    // M2 end to end: the point of the whole feature is that the repo's own postgres does not start.
    [Fact]
    public void The_generated_compose_leaves_out_the_service_the_resource_provides()
    {
        const string composeConfig = """
            {
              "schema": 2, "name": "api",
              "inputs": [ { "name": "port", "example": "5000" }, { "name": "dbPort", "example": "5432" } ],
              "compose": [ { "file": "docker-compose.yml", "overrides": [
                  { "path": ["services","api","ports","0"], "template": "${sprig.port}:5000" }
              ] } ]
            }
            """;
        const string composeYaml = """
            services:
              api:
                image: api:latest
                depends_on: [postgres]
                ports: ["5000:5000"]
              postgres:
                image: postgres:16
                volumes: [pgdata:/var/lib/postgresql/data]
            volumes:
              pgdata:
            """;

        using var store = new TempStore();
        using var repo = new TempGitRepo("api");
        var (svc, resources) = Build(store);

        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), composeConfig);
        File.WriteAllText(Path.Combine(repo.Path, "docker-compose.yml"), composeYaml);
        var config = SprigConfigLoader.LoadFromFile(Path.Combine(repo.Path, ".sprig.json"));
        var stack = new ResolvedStack("api", [new ResolvedRepo(config.Name, repo.Path, config)],
            ["api_port", "postgres_port"],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["api"] = new Dictionary<string, string>
                {
                    ["port"] = "${sprig.ports.api_port}",
                    ["dbPort"] = "${sprig.ports.postgres_port}",
                },
            });

        var postgres = Postgres();
        resources.Save(postgres with
        {
            Injects =
            [
                new ResourceInjection
                {
                    Repo = "api",
                    Inputs = new Dictionary<string, string> { ["dbPort"] = "${sprig.shared.port}" },
                    Suppress = [new InjectedSuppress { File = "docker-compose.yml", Services = ["postgres"] }],
                },
            ],
        });

        var record = svc.Create(stack, "feature-x");

        var generated = File.ReadAllText(Assert.Single(record.Repos[0].ComposePaths));
        Assert.DoesNotContain("postgres:16", generated);
        Assert.DoesNotContain("pgdata", generated);        // orphaned by the suppression
        Assert.DoesNotContain("depends_on", generated);    // emptied, so the key went with it
        Assert.Contains("api:latest", generated);
        Assert.Contains($"{record.Ports["api_port"]}:5000", generated);
    }

    [Fact]
    public void The_plan_preview_shows_the_override_without_creating_anything()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo("api");
        var (svc, resources) = Build(store);
        resources.Save(Postgres());

        var plan = svc.PreviewPlan(Stack(repo), "feature-x");

        Assert.True(plan.HasOverrides);
        Assert.Equal("5432", plan.Repos[0].Inputs["dbPort"]);
        Assert.Equal("{api_port}", plan.Repos[0].Inputs["port"]);
        Assert.Equal(["postgres_port"], plan.UnreferencedPorts);
        Assert.False(Directory.Exists(repo.SiblingWorktree("feature-x")));
    }
}
