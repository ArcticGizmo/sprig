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
        { "schema":1, "name":"api", "ports":[{"name":"http"}],
          "provides": { "baseUrl": "http://localhost:${sprig.ports.http}" } }
        """;
    const string WebConfig = """
        { "schema":1, "name":"web", "ports":[{"name":"frontend"}],
          "env":[ { "file":".env", "set": { "VITE_API_URL": "${sprig.provides.api.baseUrl}" } } ] }
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

    [Fact]
    public void Create_two_repos_wires_cross_repo_provide_and_tears_down_both()
    {
        using var store = new TempStore();
        using var apiRepo = new TempGitRepo("api");
        using var webRepo = new TempGitRepo("web");
        var svc = Build(store);

        var stack = new ResolvedStack("web+api",
            [Resolve(apiRepo, ApiConfig), Resolve(webRepo, WebConfig)],
            new Dictionary<string, string>());

        var record = svc.Create(stack, "demo");

        // Both repos materialised.
        Assert.Equal(["api", "web"], record.Repos.Select(r => r.Name).Order());
        Assert.True(Directory.Exists(apiRepo.SiblingWorktree("demo")));
        Assert.True(Directory.Exists(webRepo.SiblingWorktree("demo")));
        Assert.Equal("web+api", record.Stack);

        // Cross-repo wiring: web's .env points at the API's allocated port.
        var apiPort = record.Repos.First(r => r.Name == "api").Ports["http"];
        var webEnv = File.ReadAllText(Path.Combine(webRepo.SiblingWorktree("demo"), ".env"));
        Assert.Contains($"VITE_API_URL=http://localhost:{apiPort}", webEnv);

        // Ports are namespaced and non-colliding across repos.
        Assert.Contains("api.http", record.Ports.Keys);
        Assert.Contains("web.frontend", record.Ports.Keys);
        Assert.NotEqual(record.Ports["api.http"], record.Ports["web.frontend"]);

        // Teardown clears both worktrees and the record.
        svc.Remove("demo");
        Assert.False(Directory.Exists(apiRepo.SiblingWorktree("demo")));
        Assert.False(Directory.Exists(webRepo.SiblingWorktree("demo")));
        Assert.Null(new InstanceStore(store.Paths).TryLoad("demo"));
    }

    [Fact]
    public void Second_workspace_of_same_stack_does_not_collide()
    {
        using var store = new TempStore();
        using var apiRepo = new TempGitRepo("api");
        using var webRepo = new TempGitRepo("web");
        var svc = Build(store);

        ResolvedStack Stack() => new("web+api",
            [Resolve(apiRepo, ApiConfig), Resolve(webRepo, WebConfig)], new Dictionary<string, string>());

        var a = svc.Create(Stack(), "one");
        var b = svc.Create(Stack(), "two");

        Assert.Empty(a.Ports.Values.Intersect(b.Ports.Values));
        Assert.True(Directory.Exists(apiRepo.SiblingWorktree("one")));
        Assert.True(Directory.Exists(apiRepo.SiblingWorktree("two")));
    }
}
