using System.Collections.Generic;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

public class WorkspaceStackTests
{
    const string ApiConfig = """
        { "schema":1, "name":"api", "inputs":[ { "name":"port", "example":"5000" } ],
          "env":[ { "file":".env", "set": { "PORT": "${sprig.port}" } } ] }
        """;
    const string WebConfig = """
        { "schema":1, "name":"web", "inputs":[ { "name":"apiUrl", "example":"http://localhost:5000" } ],
          "env":[ { "file":".env", "set": { "VITE_API_URL": "${sprig.apiUrl}" } } ] }
        """;

    static WorkspaceService Build(TempStore s) => new(
        new GitService(new ProcessRunner()), new FilePortStore(s.Paths), new InstanceStore(s.Paths),
        new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths);

    static ResolvedRepo Resolve(TempGitRepo repo, string configJson)
    {
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), configJson);
        var config = SprigConfigLoader.LoadFromFile(Path.Combine(repo.Path, ".sprig.json"));
        return new ResolvedRepo(config.Name, repo.Path, config);
    }

    // web+api: both the API's PORT and the web's URL trace back to the one stack port `api_port`.
    static ResolvedStack FullStack(TempGitRepo api, TempGitRepo web) => new(
        "web+api",
        [Resolve(api, ApiConfig), Resolve(web, WebConfig)],
        ["api_port"],
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["api"] = new Dictionary<string, string> { ["port"] = "${sprig.ports.api_port}" },
            ["web"] = new Dictionary<string, string> { ["apiUrl"] = "http://localhost:${sprig.ports.api_port}" },
        });

    [Fact]
    public void Cross_repo_wiring_points_web_at_the_shared_api_port()
    {
        using var store = new TempStore();
        using var apiRepo = new TempGitRepo("api");
        using var webRepo = new TempGitRepo("web");
        var svc = Build(store);

        var record = svc.Create(FullStack(apiRepo, webRepo), "demo");

        var apiPort = record.Ports["api_port"];
        Assert.Equal(apiPort.ToString(), record.Repos.First(r => r.Name == "api").Inputs["port"]);

        var webEnv = File.ReadAllText(Path.Combine(webRepo.SiblingWorktree("demo"), ".env"));
        Assert.Contains($"VITE_API_URL=http://localhost:{apiPort}", webEnv);

        svc.Remove("demo");
        Assert.False(Directory.Exists(apiRepo.SiblingWorktree("demo")));
        Assert.False(Directory.Exists(webRepo.SiblingWorktree("demo")));
    }

    [Fact]
    public void Frontend_only_stack_stands_up_with_a_literal_binding()
    {
        using var store = new TempStore();
        using var webRepo = new TempGitRepo("web");
        var svc = Build(store);

        // web-only: apiUrl is a literal — no API repo needed.
        var stack = new ResolvedStack("web-only",
            [Resolve(webRepo, WebConfig)],
            [],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["web"] = new Dictionary<string, string> { ["apiUrl"] = "http://localhost:4000" },
            });

        svc.Create(stack, "fe");

        var webEnv = File.ReadAllText(Path.Combine(webRepo.SiblingWorktree("fe"), ".env"));
        Assert.Contains("VITE_API_URL=http://localhost:4000", webEnv);
    }

    [Fact]
    public void Unbound_input_hard_fails()
    {
        using var store = new TempStore();
        using var webRepo = new TempGitRepo("web");
        var svc = Build(store);

        // No binding supplied for web.apiUrl.
        var stack = new ResolvedStack("broken", [Resolve(webRepo, WebConfig)], [],
            new Dictionary<string, IReadOnlyDictionary<string, string>>());

        Assert.ThrowsAny<Exception>(() => svc.Create(stack, "x"));
        Assert.False(Directory.Exists(webRepo.SiblingWorktree("x"))); // rolled back
    }
}
