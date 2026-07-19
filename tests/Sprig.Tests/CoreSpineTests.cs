using Sprig.Core.Config;
using Sprig.Core.Ports;
using Sprig.Core.Substitution;

namespace Sprig.Tests;

/// <summary>
/// M1 exit criterion: given a fixture .sprig.json + a workspace name, the engine resolves all
/// variables and allocates a stable, non-colliding port set — touching only the central store
/// (no repo/worktree/docker side effects).
/// </summary>
public class CoreSpineTests
{
    // vue-like repo: one named port, one env override, a workspace-suffixed provides value.
    const string VueLike = """
        {
          "schema": 1,
          "name": "vue-app",
          "ports": [ { "name": "frontend", "description": "dev host" } ],
          "env": [ { "file": ".env.local", "set": { "PORT": "${sprig.ports.frontend}" } } ],
          "provides": { "baseUrl": "http://localhost:${sprig.ports.frontend}" }
        }
        """;

    [Fact]
    public void Resolves_fixture_config_end_to_end_with_allocated_ports()
    {
        using var store = new TempStore();

        // 1. load + validate
        var config = SprigConfigLoader.Parse(VueLike);
        Assert.True(SprigConfigValidator.Validate(config).IsValid);

        // 2. allocate the ports the repo declares
        var portStore = new FilePortStore(store.Paths);
        var ports = portStore.Acquire("feature-x", config.Ports.Select(p => p.Name).ToList());

        // 3. build the scope and resolve every template the config carries
        var scope = SprigScope.ForWorkspace("feature-x", ports,
            provides: config.Provides);

        var resolvedPort = SubstitutionEngine.Resolve(config.Env[0].Set["PORT"], scope);
        var resolvedBaseUrl = SubstitutionEngine.Resolve(config.Provides["baseUrl"], scope);
        var suffixed = SubstitutionEngine.Resolve("vue-app--${sprig.workspace}", scope);

        var allocated = ports["frontend"];
        Assert.Equal(allocated.ToString(), resolvedPort);
        Assert.Equal($"http://localhost:{allocated}", resolvedBaseUrl);
        Assert.Equal("vue-app--feature-x", suffixed);
    }

    [Fact]
    public void Two_workspaces_of_the_same_repo_get_non_colliding_ports()
    {
        using var store = new TempStore();
        var config = SprigConfigLoader.Parse(VueLike);
        var portStore = new FilePortStore(store.Paths);
        var names = config.Ports.Select(p => p.Name).ToList();

        var a = portStore.Acquire("ws-a", names);
        var b = portStore.Acquire("ws-b", names);

        Assert.NotEqual(a["frontend"], b["frontend"]);
    }

    [Fact]
    public void Stack_computed_variable_can_reference_a_port()
    {
        using var store = new TempStore();
        var portStore = new FilePortStore(store.Paths);
        var ports = portStore.Acquire("ws", ["api"]);

        var scope = SprigScope.ForWorkspace("ws", ports,
            computed: new Dictionary<string, string> { ["apiUrl"] = "https://localhost:${sprig.ports.api}" });

        Assert.Equal($"https://localhost:{ports["api"]}",
            SubstitutionEngine.Resolve("${sprig.apiUrl}", scope));
    }
}
