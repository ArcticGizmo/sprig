using Sprig.Engine.Compose;
using Sprig.Engine.Planning;
using Sprig.Engine.Processes;
using Sprig.Engine.Settings;
using Sprig.Engine.Workspaces;
using Sprig.Engine.Store;
using Sprig.Engine.Orchestrations;
using Sprig.Engine.Git;

namespace Sprig.Tests.Workspaces;

/// <summary>Workspaces of repos run on their own, linked so one takes another's outputs. Real git, no infra.</summary>
public sealed class WorkspaceLinksTests : IDisposable
{
    readonly TempStore _store = new();
    readonly string _root = LongPath.Of(Path.Combine(Path.GetTempPath(), "sprig-links-" + Guid.NewGuid().ToString("N")));
    readonly ProcessRunner _processes = new();
    readonly Dictionary<string, LoadedOrchestration> _definitions = new(StringComparer.OrdinalIgnoreCase);
    readonly WorkspaceService _workspaces;

    public WorkspaceLinksTests()
    {
        Directory.CreateDirectory(_root);
        var settings = new FileSettingsStore(_store.Paths);
        settings.Save(new SprigSettings { PortRangeStart = 9400, PortRangeEndExclusive = 9500 });
        _workspaces = new WorkspaceService(new WorkspaceStore(_store.Paths), new PortLeases(_store.Paths), settings, new GitClient(_processes),
            new DockerCompose(_processes), listening: () => new HashSet<int>())
        {
            LoadDefinition = path => _definitions[path],
        };
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal); // git marks its objects read-only
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    void Git(string dir, params string[] args) => _processes.Run("git", ["-C", dir, .. args]).EnsureSuccess();

    sealed class SyncProgressList(List<WorkspaceEvent> events) : IProgress<WorkspaceEvent>
    {
        public void Report(WorkspaceEvent value) => events.Add(value);
    }

    /// <summary>A repo on its own: a git repo of that name with one file, and its definition loaded as sprig runs a repo
    /// from the Repos list.</summary>
    LoadedOrchestration Repo(string id, string yaml)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        Git(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, ".gitignore"), ".env.local\n");
        File.WriteAllText(Path.Combine(dir, "README.md"), id);
        Git(dir, "add", "-A");
        Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "init");

        var file = Path.Combine(_root, id + ".orchestration.yaml");
        var orchestration = Yaml.Orchestration($"schema: 1\norchestration: {id}\nrepos:\n  {id}: {{ path: {id} }}\n", file);
        var loaded = new LoadedOrchestration(file, orchestration,
            new Dictionary<string, Sprig.Model.RepoModel> { [id] = Yaml.Repo(yaml, Path.Combine(dir, "sprig.yaml")) },
            [], [], [], new Dictionary<string, string> { [id] = dir }) { Id = id, Alone = true };
        _definitions[file] = loaded;
        return loaded;
    }

    static string Services(string output = "${api.url}", string? inputs = null) => $$"""
        schema: 1
        repo: shop-services
        apps:
          api:
            run: node
            ports: { http: { host: any, main: 5000 } }
        files:
          .env.local:
            read-by: [api]
            values:
              PORT: ${api.port}
        {{inputs ?? ""}}
        outputs:
          catalog-url:
            type: url
            value: {{output}}
        """;

    const string Web = """
        schema: 1
        repo: shop-web
        apps:
          web:
            run: node
            ports: { http: any }
        inputs:
          catalog-url:
            type: url
            default: http://localhost:1
        files:
          .env.local:
            read-by: [web]
            values:
              CATALOG: ${inputs.catalog-url}
        """;

    string EnvOf(WorkspaceRecord record) => File.ReadAllText(Path.Combine(record.Repos.Single().Dir, ".env.local"));

    int ApiPort(string workspace) => _workspaces.LastPlan("shop-services", workspace)!.Ports.Single(p => p.Resource == "api").Port!.Value;

    WorkspaceLink To(LoadedOrchestration target, string workspace, IReadOnlyDictionary<string, string>? inputs = null)
        => new(target.Id, workspace, target.OrchestrationPath, inputs);

    [Fact]
    public void A_link_gives_an_input_the_other_workspaces_output_and_the_workspaces_own_value_still_wins()
    {
        var services = Repo("shop-services", Services());
        var web = Repo("shop-web", Web);
        _workspaces.Up(services, "feat-x", null);
        Assert.Contains("CATALOG=http://localhost:1", EnvOf(_workspaces.Up(web, "feat-x", null)));

        _workspaces.Link("shop-web", "feat-x", To(services, "main"));
        // One workspace of a repo at a time, so the slot replaces main.
        Assert.Equal("shop-services/feat-x", Assert.Single(_workspaces.Link("shop-web", "feat-x", To(services, "feat-x")).Links!).Key);
        var linked = _workspaces.Up(web, "feat-x", null);

        Assert.Contains($"CATALOG=http://localhost:{ApiPort("feat-x")}", EnvOf(linked));
        var plan = _workspaces.LastPlan("shop-web", "feat-x")!;
        Assert.Equal("shop-services--feat-x", plan.Linked!["catalog-url"].From);
        Assert.Contains("input catalog-url ← shop-services--feat-x", plan.Apps.Single().Env.Single(e => e.Key == "CATALOG").Value.Via);

        _workspaces.SetValues("shop-web", "feat-x", new Dictionary<string, string> { ["catalog-url"] = "https://catalog.example.com" });
        Assert.Contains("CATALOG=https://catalog.example.com", EnvOf(_workspaces.Up(web, "feat-x", null)));

        _workspaces.SetValues("shop-web", "feat-x", new Dictionary<string, string>(), unset: ["catalog-url"]);
        _workspaces.Unlink("shop-web", "feat-x");
        Assert.Contains("CATALOG=http://localhost:1", EnvOf(_workspaces.Up(web, "feat-x", null)));
    }

    [Fact]
    public void A_main_that_has_never_come_up_gives_the_ports_its_own_config_uses()
    {
        var services = Repo("shop-services", Services());
        var web = Repo("shop-web", Web);
        _workspaces.Up(web, "feat-x", null);

        _workspaces.Link("shop-web", "feat-x", To(services, "main"));

        Assert.Contains("CATALOG=http://localhost:5000", EnvOf(_workspaces.Up(web, "feat-x", null)));
        Assert.Equal("shop-services main", _workspaces.LastPlan("shop-web", "feat-x")!.Linked!["catalog-url"].From);
        Assert.Contains("can't be linked", Assert.Throws<WorkspaceException>(() => _workspaces.Link("shop-services", "main", To(web, "feat-x"))).Message);
    }

    [Fact]
    public void A_link_to_a_workspace_that_has_gone_is_broken_rather_than_falling_back_to_the_default()
    {
        var services = Repo("shop-services", Services());
        var web = Repo("shop-web", Web);
        _workspaces.Up(services, "feat-x", null);
        _workspaces.Up(web, "feat-x", null);
        _workspaces.Link("shop-web", "feat-x", To(services, "feat-x"));

        _workspaces.Remove("shop-services", "feat-x", new RemoveOptions(Force: true));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Up(web, "feat-x", null));
        Assert.Contains(e.Problems, p => p.Message.Contains("isn't there any more"));
    }

    [Fact]
    public void Two_workspaces_can_take_each_others_urls()
    {
        var services = Repo("shop-services", Services(inputs: """
            inputs:
              web-url:
                type: url
                default: http://localhost:2
            """).Replace("PORT: ${api.port}", "PORT: ${api.port}\n      ORIGIN: ${inputs.web-url}"));
        var web = Repo("shop-web", Web + """

            outputs:
              web-url:
                type: url
                value: ${web.url}
            """);
        _workspaces.Up(services, "feat-x", null);
        _workspaces.Up(web, "feat-x", null);
        _workspaces.Link("shop-web", "feat-x", To(services, "feat-x"));
        _workspaces.Link("shop-services", "feat-x", To(web, "feat-x"));

        var webRecord = _workspaces.Up(web, "feat-x", null);
        var servicesRecord = _workspaces.Up(services, "feat-x", null);

        var webPort = _workspaces.LastPlan("shop-web", "feat-x")!.Ports.Single(p => p.Resource == "web").Port;
        Assert.Contains($"CATALOG=http://localhost:{ApiPort("feat-x")}", EnvOf(webRecord));
        Assert.Contains($"ORIGIN=http://localhost:{webPort}", EnvOf(servicesRecord));
    }

    [Fact]
    public void An_output_that_passes_a_linked_input_back_round_is_a_loop()
    {
        var a = Repo("repo-a", """
            schema: 1
            repo: repo-a
            apps:
              web: { run: node }
            inputs:
              x: { default: a }
            files:
              .env.local: { read-by: [web], values: { X: "${inputs.x}" } }
            outputs:
              y: { value: "${inputs.x}" }
            """);
        var b = Repo("repo-b", """
            schema: 1
            repo: repo-b
            apps:
              web: { run: node }
            inputs:
              y: { default: b }
            files:
              .env.local: { read-by: [web], values: { Y: "${inputs.y}" } }
            outputs:
              x: { value: "${inputs.y}" }
            """);
        _workspaces.Up(a, "feat-x", null);
        _workspaces.Up(b, "feat-x", null);
        _workspaces.Link("repo-a", "feat-x", To(b, "feat-x"));
        Assert.Contains("X=b", EnvOf(_workspaces.Up(a, "feat-x", null)));

        _workspaces.Link("repo-b", "feat-x", To(a, "feat-x"));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Up(b, "feat-x", null));
        Assert.Contains(e.Problems, p => p.Message.Contains("comes back round to repo-b--feat-x"));
    }

    [Fact]
    public void Making_a_workspace_ready_rewrites_the_files_of_those_linked_to_it_when_its_outputs_change()
    {
        var services = Repo("shop-services", Services("${inputs.base}/catalog", """
            inputs:
              base:
                type: url
                default: http://a.example.com
            """));
        var web = Repo("shop-web", Web);
        _workspaces.Up(services, "feat-x", null);
        _workspaces.Up(web, "feat-x", null);
        _workspaces.Link("shop-web", "feat-x", To(services, "feat-x"));
        var webRecord = _workspaces.Up(web, "feat-x", null);
        Assert.Contains("CATALOG=http://a.example.com/catalog", EnvOf(webRecord));

        var events = new List<WorkspaceEvent>();
        _workspaces.Up(services, "feat-x", null, progress: new SyncProgressList(events));
        Assert.DoesNotContain(events, e => e.Step.StartsWith("Linked", StringComparison.Ordinal));

        _workspaces.SetValues("shop-services", "feat-x", new Dictionary<string, string> { ["base"] = "http://b.example.com" });
        _workspaces.Up(services, "feat-x", null, progress: new SyncProgressList(events));

        Assert.Contains("CATALOG=http://b.example.com/catalog", EnvOf(webRecord));
        Assert.Contains(events, e => e is { Step: "Linked shop-web--feat-x", State: StepState.Done, Detail: "files rewritten; restart web" });
    }

    [Fact]
    public void Two_links_that_give_one_input_must_be_told_which_to_use()
    {
        var services = Repo("shop-services", Services());
        var search = Repo("shop-search", Services().Replace("repo: shop-services", "repo: shop-search"));
        var web = Repo("shop-web", Web);
        _workspaces.Up(services, "feat-x", null);
        _workspaces.Up(search, "feat-x", null);
        _workspaces.Up(web, "feat-x", null);
        _workspaces.Link("shop-web", "feat-x", To(services, "feat-x"));
        _workspaces.Link("shop-web", "feat-x", To(search, "feat-x"));

        var e = Assert.Throws<WorkspaceException>(() => _workspaces.Up(web, "feat-x", null));
        Assert.Contains(e.Problems, p => p.Message.Contains("both give 'catalog-url'"));

        _workspaces.Link("shop-web", "feat-x", To(services, "feat-x", new Dictionary<string, string> { ["catalog-url"] = "catalog-url" }));
        Assert.Contains($"CATALOG=http://localhost:{ApiPort("feat-x")}", EnvOf(_workspaces.Up(web, "feat-x", null)));
    }
}
