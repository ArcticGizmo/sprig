using Sprig.App.ViewModels;
using Sprig.Engine.Compose;
using Sprig.Engine.Git;
using Sprig.Engine.Planning;
using Sprig.Engine.Processes;
using Sprig.Engine.Settings;
using Sprig.Engine.Workspaces;
using Sprig.Model;

namespace Sprig.Tests.Workspaces;

/// <summary>The graph's view of a workspace's containers: where each runs, how it's doing, and starting and stopping
/// one, against a docker that only answers from a script.</summary>
public sealed class WorkspaceInfraTests : IDisposable
{
    /// <summary>Answers <c>ps</c> per project from <see cref="Ps"/>, and records every call.</summary>
    sealed class FakeDocker : IProcessRunner
    {
        public Dictionary<string, string> Ps { get; } = [];
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public bool Down { get; set; }

        public ProcessResult Run(string executable, IReadOnlyList<string> arguments, string? workingDirectory = null,
            CancellationToken cancellationToken = default, Action<string>? onOutput = null)
        {
            Calls.Add(arguments);
            if (Down) return new ProcessResult(executable, arguments, 1, "", "error during connect: the docker engine isn't running");
            var project = arguments[arguments.ToList().IndexOf("-p") + 1];
            return new ProcessResult(executable, arguments, 0, arguments.Contains("ps") ? Ps.GetValueOrDefault(project, "") : "", "");
        }
    }

    readonly TempStore _store = new();
    readonly FakeDocker _docker = new();
    readonly WorkspaceService _workspaces;

    public WorkspaceInfraTests()
    {
        _workspaces = new WorkspaceService(new WorkspaceStore(_store.Paths), new PortLeases(_store.Paths), new FileSettingsStore(_store.Paths),
            new GitClient(_docker), new DockerCompose(_docker), listening: () => new HashSet<int>());
    }

    public void Dispose() => _store.Dispose();

    static OrchestrationModel Storefront => Yaml.Orchestration("""
        schema: 1
        orchestration: storefront
        repos:
          shop-services: { path: shop-services }
        containers:
          mail: { preset: mailpit }
        """);

    (WorkspaceRecord, Plan) Shop()
    {
        var repo = Yaml.Repo("""
            schema: 1
            repo: shop-services
            containers:
              pg: { preset: postgres }
            compose:
              docker-compose.yml:
                services:
                  search:
                    ports: { http: { container: 7700 } }
            """);
        var plan = Planner.Build(new PlanInput(Storefront, new Dictionary<string, RepoModel> { ["shop-services"] = repo },
            "1", new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9100 }, new HashSet<int>()));
        var now = DateTimeOffset.Now;
        var dir = Path.Combine(_store.Root, "storefront--1", "shop-services");
        var record = new WorkspaceRecord("storefront", Path.Combine(_store.Root, "storefront.orchestration.yaml"), "1", "feat-a", "stable",
            [new WorkspaceRepo("shop-services", "shop-services", Path.Combine(_store.Root, "shop-services"), dir, true, true)], [], [], WorkspaceState.Up, now, now);
        return (record, plan);
    }

    [Fact]
    public void Each_container_is_found_under_the_project_and_service_names_up_gives_it()
    {
        var (record, plan) = Shop();

        var targets = _workspaces.InfraTargets(record, plan).ToDictionary(t => t.Key);

        Assert.Equal(("sprig-storefront-1", "shop-services-pg"), (targets["shop-services/pg"].Project.Name, targets["shop-services/pg"].Service));
        Assert.Equal(("sprig-storefront-1", "storefront-mail"), (targets["storefront/mail"].Project.Name, targets["storefront/mail"].Service));
        Assert.Equal(("sprig-storefront-1-shop-services", "search"), (targets["shop-services/search"].Project.Name, targets["shop-services/search"].Service));
    }

    [Fact]
    public void States_come_from_one_ps_per_project_and_a_container_docker_hasn_t_got_is_left_out()
    {
        var (record, plan) = Shop();
        _docker.Ps["sprig-storefront-1"] = """
            {"Service":"shop-services-pg","State":"running","Health":"healthy","ExitCode":0}
            """;
        _docker.Ps["sprig-storefront-1-shop-services"] = """
            {"Service":"search","State":"exited","Health":"","ExitCode":1}
            """;

        var states = _workspaces.InfraStates(_workspaces.InfraTargets(record, plan))!;

        Assert.Equal("healthy", states["shop-services/pg"].Health);
        Assert.Equal("exited", states["shop-services/search"].State);
        Assert.False(states.ContainsKey("storefront/mail"));
        Assert.Equal(2, _docker.Calls.Count(c => c.Contains("ps")));
    }

    [Fact]
    public void A_docker_that_doesn_t_answer_gives_no_states_rather_than_all_stopped()
    {
        var (record, plan) = Shop();
        _docker.Down = true;

        Assert.Null(_workspaces.InfraStates(_workspaces.InfraTargets(record, plan)));
    }

    [Fact]
    public void Starting_and_stopping_one_container_names_only_its_service()
    {
        var (record, plan) = Shop();
        var pg = _workspaces.InfraTargets(record, plan).Single(t => t.Key == "shop-services/pg");

        _workspaces.StopContainer(pg);
        _workspaces.StartContainer(pg);

        Assert.Equal(["compose", "-p", "sprig-storefront-1", "stop", "shop-services-pg"], _docker.Calls[^2]);
        Assert.Equal(["compose", "-p", "sprig-storefront-1", "start", "shop-services-pg"], _docker.Calls[^1]);
    }

    /// <summary>The front end waits for search; the api waits for a migration job, which waits for the database. The
    /// orchestration's mail is used by nothing.</summary>
    (WorkspaceRecord, Plan) Grouped()
    {
        var repo = Yaml.Repo("""
            schema: 1
            repo: shop-services
            containers:
              pg: { preset: postgres }
            compose:
              docker-compose.yml:
                services:
                  search:
                    ports: { http: { container: 7700 } }
            apps:
              migrate:
                run: dotnet
                path: src/Migrate
                once: true
                wait-for: [pg]
              api:
                run: dotnet
                path: src/Api
                ports: { http: any }
                wait-for: [migrate]
              web:
                run: node
                path: web
                ports: { http: any }
                wait-for: [search]
            groups:
              front: [web]
              back: [api]
            """);
        var plan = Planner.Build(new PlanInput(Storefront, new Dictionary<string, RepoModel> { ["shop-services"] = repo },
            "1", new SprigSettings { PortRangeStart = 9000, PortRangeEndExclusive = 9100 }, new HashSet<int>()));
        var now = DateTimeOffset.Now;
        var record = new WorkspaceRecord("storefront", Path.Combine(_store.Root, "storefront.orchestration.yaml"), "1", "feat-a", "stable",
            [new WorkspaceRepo("shop-services", "shop-services", Path.Combine(_store.Root, "shop-services"), Path.Combine(_store.Root, "storefront--1", "shop-services"), true, true)],
            [], [], WorkspaceState.Up, now, now);
        return (record, plan);
    }

    [Fact]
    public void A_group_uses_the_infra_its_apps_and_what_they_depend_on_wait_for_and_nothing_else()
    {
        var (_, plan) = Grouped();

        Assert.Equal(["shop-services/search"], WorkspaceService.InfraFor(plan, new Starting("front").AppsIn(plan))!);
        Assert.Equal(["shop-services/pg"], WorkspaceService.InfraFor(plan, new Starting("back").AppsIn(plan))!);
        Assert.Equal(["shop-services/pg"], WorkspaceService.InfraFor(plan, new Starting(Apps: ["api"]).AppsIn(plan))!);
        Assert.Null(WorkspaceService.InfraFor(plan, new Starting().AppsIn(plan)));
    }

    [Fact]
    public void Starting_another_group_starts_only_the_infra_it_adds_that_is_not_running()
    {
        var (record, plan) = Grouped();
        _docker.Ps["sprig-storefront-1"] = """
            {"Service":"shop-services-pg","State":"running","Health":"healthy","ExitCode":0}
            """;

        Assert.Empty(_workspaces.MissingInfra(record, plan, new Starting("back").AppsIn(plan)));
        var missing = _workspaces.MissingInfra(record, plan, new Starting("front").AppsIn(plan));
        Assert.Equal(["shop-services/search"], missing.Select(t => t.Key));

        _workspaces.StartInfra(missing);

        var up = _docker.Calls.Single(c => c.Contains("up"));
        Assert.Equal(("sprig-storefront-1-shop-services", "search"), (up[up.ToList().IndexOf("-p") + 1], up[^1]));
    }

    [Theory]
    [InlineData("running", "healthy", 0, "healthy", NodeHealth.Good, true)]
    [InlineData("running", "", 0, "running", NodeHealth.Good, true)]
    [InlineData("running", "starting", 0, "starting", NodeHealth.Busy, true)]
    [InlineData("running", "unhealthy", 0, "unhealthy", NodeHealth.Bad, true)]
    [InlineData("exited", "", 0, "stopped", NodeHealth.Off, false)]
    [InlineData("exited", "", 143, "stopped", NodeHealth.Off, false)]
    [InlineData("exited", "", 1, "exited", NodeHealth.Bad, false)]
    [InlineData("restarting", "", 1, "restarting", NodeHealth.Bad, true)]
    public void A_container_node_reads_docker_s_state_as_one_colour(string state, string health, int exit, string shown, NodeHealth colour, bool alive)
    {
        var node = new GraphNode(new TopologyNode("svc/pg", "svc", "pg", TopologyKind.Container, "postgres:17"), null, null);

        node.Apply(new ComposeService("svc-pg", state, health, exit), known: true);

        Assert.Equal((shown, colour, alive), (node.State, node.Health, node.IsAlive));
    }

    [Fact]
    public void A_container_docker_hasn_t_got_or_can_t_say_about_isn_t_shown_as_failed()
    {
        var node = new GraphNode(new TopologyNode("svc/pg", "svc", "pg", TopologyKind.Container, "postgres:17"), null, null);

        node.Apply(null, known: true);
        Assert.Equal(("not created", NodeHealth.Off), (node.State, node.Health));

        node.Apply(null, known: false);
        Assert.Equal(("unknown", NodeHealth.Unknown), (node.State, node.Health));
    }
}
