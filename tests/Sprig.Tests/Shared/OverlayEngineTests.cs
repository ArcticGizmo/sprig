using System.Collections.Generic;
using System.Linq;
using Sprig.Core.Config;
using Sprig.Core.Planning;
using Sprig.Core.Shared;
using Sprig.Core.Substitution;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Shared;

/// <summary>
/// The overlay engine is the whole shared-infrastructure mechanism: a pure plan → plan transform, applied
/// machine-locally, that never touches a tracked file. These tests pin the two properties the design rests
/// on — that skipping it leaves the plan untouched, and that a target which has moved fails loudly.
/// </summary>
public class OverlayEngineTests
{
    // A repo that pins the database name inside a connection string — the realistic hard case, and the
    // reason an overlay sometimes has to reach past the input layer into an env template.
    const string ApiConfig = """
        {
          "schema": 2, "name": "dotnet-api",
          "inputs": [ { "name": "port", "example": "5000" }, { "name": "dbPort", "example": "5432" } ],
          "env": [ { "file": ".env", "set": {
              "PORT": "${sprig.port}",
              "ConnectionStrings__Default": "Host=localhost;Port=${sprig.dbPort};Database=librarydb;Username=library;Password=library_pass"
          } } ],
          "compose": [ { "file": "docker-compose.yml", "overrides": [
              { "path": ["services","postgres","ports","0"], "template": "${sprig.dbPort}:5432" }
          ] } ]
        }
        """;

    static ResolvedRepo Repo(string json = ApiConfig)
    {
        var config = SprigConfigLoader.Parse(json);
        return new ResolvedRepo(config.Name, "C:/code/api", config);
    }

    static WorkspacePlan BasePlan() => WorkspacePlanner.Plan(
        new ResolvedStack("web+api", [Repo()], ["api_port", "postgres_port"],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["dotnet-api"] = new Dictionary<string, string>
                {
                    ["port"] = "${sprig.ports.api_port}",
                    ["dbPort"] = "${sprig.ports.postgres_port}",
                },
            }),
        "feature-x");

    /// <summary>The postgres-16 resource from the design doc, injecting at both layers.</summary>
    static SharedResourceDefinition Postgres(bool enabled = true) => new()
    {
        Name = "postgres-16",
        Enabled = enabled,
        Values = new Dictionary<string, string>
        {
            ["host"] = "localhost",
            ["port"] = "5432",
            ["database"] = "sprig_${sprig.workspace}",
            ["user"] = "sprig",
            ["password"] = "sprig",
        },
        Injects =
        [
            new ResourceInjection
            {
                Repo = "dotnet-api",
                // The preferred layer: the repo already declares dbPort.
                Inputs = new Dictionary<string, string> { ["dbPort"] = "${sprig.shared.port}" },
                // One layer deeper, because no input carries the database name.
                Env =
                [
                    new InjectedEnv
                    {
                        File = ".env",
                        Set = new Dictionary<string, string>
                        {
                            ["ConnectionStrings__Default"] =
                                "Host=${sprig.shared.host};Port=${sprig.shared.port};Database=${sprig.shared.database};Username=${sprig.shared.user};Password=${sprig.shared.password}",
                        },
                    },
                ],
                Suppress = [new InjectedSuppress { File = "docker-compose.yml", Services = ["postgres"] }],
            },
        ],
    };

    [Fact]
    public void Applying_no_resources_returns_the_plan_untouched()
    {
        var plan = BasePlan();
        Assert.Same(plan, OverlayEngine.Apply(plan, []));
    }

    [Fact]
    public void A_disabled_resource_changes_nothing()
    {
        var plan = BasePlan();
        Assert.Same(plan, OverlayEngine.Apply(plan, [Postgres(enabled: false)]));
    }

    [Fact]
    public void A_resource_that_reaches_no_repo_in_the_plan_is_a_no_op()
    {
        var plan = BasePlan();
        var elsewhere = Postgres() with
        {
            Injects = [Postgres().Injects[0] with { Repo = "some-other-repo" }],
        };

        var applied = OverlayEngine.Apply(plan, [elsewhere]);

        Assert.Empty(applied.Notes);
        Assert.Equal("${sprig.ports.postgres_port}", applied.Repos[0].Bindings["dbPort"]);
    }

    [Fact]
    public void Overriding_an_input_frees_the_stack_port_it_used_to_reference()
    {
        var applied = OverlayEngine.Apply(BasePlan(), [Postgres()]);

        Assert.Equal(["api_port"], applied.ReferencedPorts);
        Assert.Equal(["postgres_port"], applied.UnreferencedPorts);
    }

    [Fact]
    public void The_repos_own_config_on_disk_is_never_mutated()
    {
        var plan = BasePlan();
        var before = plan.Repos[0].Source.Config;

        var applied = OverlayEngine.Apply(plan, [Postgres()]);

        // The effective config changed; the config the repo actually declares did not.
        Assert.NotSame(before, applied.Repos[0].EffectiveConfig);
        Assert.Same(before, applied.Repos[0].Source.Config);
        Assert.Contains("Database=librarydb", before.Env[0].Set["ConnectionStrings__Default"]);
    }

    [Fact]
    public void Bound_values_come_out_pointing_at_the_shared_resource()
    {
        var applied = OverlayEngine.Apply(BasePlan(), [Postgres()]);
        var bound = WorkspacePlanner.Bind(applied, new Dictionary<string, int> { ["api_port"] = 8021 });

        var repo = Assert.Single(bound.Repos);
        Assert.Equal("5432", repo.Inputs["dbPort"]);
        Assert.Equal("8021", repo.Inputs["port"]);

        // The env template the worktree will actually be written from.
        var template = repo.EffectiveConfig.Env[0].Set["ConnectionStrings__Default"];
        Assert.Equal(
            "Host=localhost;Port=5432;Database=sprig_feature-x;Username=sprig;Password=sprig",
            SubstitutionEngine.Resolve(template, repo.Scope));

        // Untouched keys still resolve the ordinary way.
        Assert.Equal("8021", SubstitutionEngine.Resolve(repo.EffectiveConfig.Env[0].Set["PORT"], repo.Scope));
    }

    [Fact]
    public void Every_override_is_recorded_with_its_layer_and_what_it_displaced()
    {
        var applied = OverlayEngine.Apply(BasePlan(), [Postgres()]);
        var bound = WorkspacePlanner.Bind(applied, new Dictionary<string, int> { ["api_port"] = 8021 });

        Assert.True(bound.HasOverrides);

        var input = Assert.Single(bound.NotesFor("dotnet-api"), n => n.Target == PlanTargets.Input("dbPort"));
        Assert.Equal(PlanLayer.Shared, input.Layer);
        Assert.Equal("5432", input.Value);
        Assert.Equal("postgres-16", input.Source);
        Assert.Equal("${sprig.ports.postgres_port}", input.Replaced);   // the port is gone, so show the expression

        var env = Assert.Single(bound.NotesFor("dotnet-api"),
            n => n.Target == PlanTargets.EnvKey(".env", "ConnectionStrings__Default"));
        Assert.Equal("env:.env#ConnectionStrings__Default", env.Target);  // '.env' is not 'env'
        Assert.Equal(PlanLayer.Shared, env.Layer);
        Assert.Contains("Database=sprig_feature-x", env.Value);
        // The displaced template is shown as written. Resolving it would substitute dbPort — which this
        // same overlay just rewrote — and report a "was" that was never true.
        Assert.Equal(
            "Host=localhost;Port=${sprig.dbPort};Database=librarydb;Username=library;Password=library_pass",
            env.Replaced);

        var suppressed = Assert.Single(bound.NotesFor("dotnet-api"),
            n => n.Target == PlanTargets.ComposeService("docker-compose.yml", "postgres"));
        Assert.Equal(PlanLayer.Shared, suppressed.Layer);
        Assert.Equal(new ComposeSuppression("docker-compose.yml", "postgres", "postgres-16"),
            Assert.Single(bound.Repos[0].Suppress));

        // Values nobody overrode keep saying so.
        var untouched = Assert.Single(bound.NotesFor("dotnet-api"), n => n.Target == PlanTargets.Input("port"));
        Assert.Equal(PlanLayer.Stack, untouched.Layer);
    }

    [Fact]
    public void Overriding_an_input_the_repo_doesnt_declare_is_a_hard_failure()
    {
        var bad = Postgres() with
        {
            Injects =
            [
                new ResourceInjection
                {
                    Repo = "dotnet-api",
                    Inputs = new Dictionary<string, string> { ["dbName"] = "whatever" },
                },
            ],
        };

        var ex = Assert.Throws<SharedResourceException>(() => OverlayEngine.Apply(BasePlan(), [bad]));
        Assert.Contains("dbName", ex.Message);
        Assert.Contains("only replace a value that already resolves", ex.Message);
    }

    // R1: the overlay reaches into repo internals across a boundary the repo's owner doesn't know exists.
    // A rename must fail loudly here rather than silently reverting to a private postgres that isn't up.
    [Fact]
    public void An_env_key_that_has_been_renamed_fails_loudly_rather_than_being_skipped()
    {
        var renamed = ApiConfig.Replace("ConnectionStrings__Default", "ConnectionStrings__Primary");
        var plan = WorkspacePlanner.Plan(
            new ResolvedStack("web+api", [Repo(renamed)], ["api_port", "postgres_port"],
                new Dictionary<string, IReadOnlyDictionary<string, string>>
                {
                    ["dotnet-api"] = new Dictionary<string, string>
                    {
                        ["port"] = "${sprig.ports.api_port}",
                        ["dbPort"] = "${sprig.ports.postgres_port}",
                    },
                }),
            "feature-x");

        var ex = Assert.Throws<SharedResourceException>(() => OverlayEngine.Apply(plan, [Postgres()]));
        Assert.Contains("ConnectionStrings__Default", ex.Message);
        Assert.Contains("renamed", ex.Message);
    }

    [Fact]
    public void Add_true_lets_an_overlay_introduce_a_key_on_purpose()
    {
        var adding = Postgres() with
        {
            Injects =
            [
                new ResourceInjection
                {
                    Repo = "dotnet-api",
                    Env =
                    [
                        new InjectedEnv
                        {
                            File = ".env",
                            Add = true,
                            Set = new Dictionary<string, string> { ["DB_HOST"] = "${sprig.shared.host}" },
                        },
                    ],
                },
            ],
        };

        var bound = WorkspacePlanner.Bind(OverlayEngine.Apply(BasePlan(), [adding]),
            new Dictionary<string, int> { ["api_port"] = 8021, ["postgres_port"] = 8034 });

        var template = bound.Repos[0].EffectiveConfig.Env[0].Set["DB_HOST"];
        Assert.Equal("localhost", SubstitutionEngine.Resolve(template, bound.Repos[0].Scope));
    }

    // R4: two overlays writing one value can't be resolved automatically, and last-writer-wins in a layer
    // people forget exists is the expensive kind of bug.
    [Fact]
    public void Two_resources_writing_the_same_target_is_a_conflict_naming_both()
    {
        var first = Postgres();
        var second = Postgres() with { Name = "postgres-14" };

        var ex = Assert.Throws<SharedResourceException>(() => OverlayEngine.Apply(BasePlan(), [first, second]));
        Assert.Contains("postgres-16", ex.Message);
        Assert.Contains("postgres-14", ex.Message);
        Assert.Contains("disable one", ex.Message);
    }

    [Fact]
    public void Suppressing_a_compose_file_the_repo_doesnt_declare_is_a_hard_failure()
    {
        var bad = Postgres() with
        {
            Injects =
            [
                new ResourceInjection
                {
                    Repo = "dotnet-api",
                    Suppress = [new InjectedSuppress { File = "compose.other.yml", Services = ["postgres"] }],
                },
            ],
        };

        var ex = Assert.Throws<SharedResourceException>(() => OverlayEngine.Apply(BasePlan(), [bad]));
        Assert.Contains("compose.other.yml", ex.Message);
    }

    [Fact]
    public void A_compose_path_override_replaces_the_repos_own()
    {
        var pointing = Postgres() with
        {
            Injects =
            [
                new ResourceInjection
                {
                    Repo = "dotnet-api",
                    Compose =
                    [
                        new InjectedCompose
                        {
                            File = "docker-compose.yml",
                            Overrides =
                            [
                                new ComposeOverride
                                {
                                    Path = ["services", "postgres", "ports", "0"],
                                    Template = "${sprig.shared.port}:5432",
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var applied = OverlayEngine.Apply(BasePlan(), [pointing]);

        var over = Assert.Single(applied.Repos[0].EffectiveConfig.Compose[0].Overrides);
        Assert.Equal("${sprig.shared.port}:5432", over.Template);

        var note = Assert.Single(applied.Notes,
            n => n.Target == PlanTargets.ComposePath("docker-compose.yml", ["services", "postgres", "ports", "0"]));
        Assert.Equal("${sprig.dbPort}:5432", note.Replaced);
    }

    // The property the whole revision exists to protect: off is always a working configuration.
    [Fact]
    public void The_unlayered_plan_is_exactly_what_you_get_without_the_feature()
    {
        var plain = BasePlan();
        var overlaid = OverlayEngine.Apply(plain, [Postgres()]);

        Assert.NotEqual(plain.Repos[0].Bindings["dbPort"], overlaid.Repos[0].Bindings["dbPort"]);

        // Re-planning from the same stack without overlays reproduces the original, byte for byte.
        var again = BasePlan();
        Assert.Equal(plain.Repos[0].Bindings, again.Repos[0].Bindings);
        Assert.Equal(plain.ReferencedPorts, again.ReferencedPorts);
        Assert.Empty(again.Notes);
        Assert.Empty(again.Repos[0].Suppress);
    }
}
