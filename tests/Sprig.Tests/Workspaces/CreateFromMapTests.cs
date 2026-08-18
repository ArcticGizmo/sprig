using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Maps;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Setup;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

/// <summary>M4 â€” CreateFromMap materialises a map selection: worktrees, per-module env/compose against each
/// module's capability scope, setup, and a record carrying the map + selection. Local (sibling) wiring
/// resolves entirely within the repo; an unmet need fails with rollback.</summary>
public class CreateFromMapTests
{
    // A monorepo: the api module PROVIDES mono-api (its own port + a derived url); the web module NEEDS it.
    // Only capability references are used, so it stands up with no map (pure provides/needs wiring).
    const string MonoConfig = """
        { "schema": 1, "name": "mono",
          "modules": [
            { "name": "api", "path": "apps/api",
              "provides": [ { "capability": "mono-api",
                "ports": { "port": true }, "shapes": { "url": "http://localhost:${sprig.mono-api.port}" } } ],
              "env": [ { "file": ".env", "set": { "PORT": "${sprig.mono-api.port}" } } ],
              "compose": [ { "file": "docker-compose.yml", "overrides": [
                  { "path": ["services","api","ports","0"], "template": "${sprig.mono-api.port}:3000" } ] } ] },
            { "name": "web", "path": "apps/web",
              "needs": [ { "capability": "mono-api" } ],
              "env": [ { "file": ".env.local", "set": { "API": "${sprig.mono-api.url}" } } ] } ] }
        """;

    const string ApiComposeYml = """
        services:
          api:
            image: node
            ports:
              - "3000:3000"
        """;

    static WorkspaceService Build(TempStore s)
        => new(new GitService(new ProcessRunner()), new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths,
            new SetupRunner(new RecordingProcessRunner { ExitCode = 0 }));

    static ResolvedRepo Resolve(TempGitRepo repo)
        => new("mono", repo.Path, SprigConfigLoader.LoadFromFile(Path.Combine(repo.Path, ".sprig.json")));

    [Fact]
    public void Monorepo_materialises_with_local_wiring_resolved_across_modules()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), MonoConfig);
        Directory.CreateDirectory(Path.Combine(repo.Path, "apps", "api"));
        File.WriteAllText(Path.Combine(repo.Path, "apps", "api", "docker-compose.yml"), ApiComposeYml);

        var svc = Build(store);
        var record = svc.CreateFromMap("feat-a", null, [Resolve(repo)]);
        var worktree = repo.SiblingWorktree("feat-a");

        var port = record.Ports["mono.mono-api.port"];
        // The api's own port, and the web's URL derived from that SAME port (local, cross-module wiring).
        Assert.Contains($"PORT={port}", File.ReadAllText(Path.Combine(worktree, "apps", "api", ".env")));
        Assert.Contains($"API=http://localhost:{port}", File.ReadAllText(Path.Combine(worktree, "apps", "web", ".env.local")));

        // Per-module compose was generated against the module's scope.
        var composePath = Assert.Single(record.Repos[0].ComposePaths);
        Assert.Contains($"{port}:3000", File.ReadAllText(composePath));

        Assert.Null(record.Map);
        Assert.Equal(["mono"], record.SelectedRepos);
    }

    [Fact]
    public void A_map_workspace_can_be_claimed_reapplying_stored_module_scopes()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), MonoConfig);
        Directory.CreateDirectory(Path.Combine(repo.Path, "apps", "api"));
        File.WriteAllText(Path.Combine(repo.Path, "apps", "api", "docker-compose.yml"), ApiComposeYml);

        var svc = Build(store);
        var created = svc.CreateFromMap("feat-a", null, [Resolve(repo)]);
        var port = created.Ports["mono.mono-api.port"];

        // Claim cuts a branch, resets to base, and reapplies env/compose â€” for a map workspace that means
        // rebuilding each module's scope from the stored InstanceModule values (no map re-resolution).
        var claimed = svc.Claim("feat-a", "work", fresh: false);
        var worktree = repo.SiblingWorktree("feat-a");

        Assert.Equal("work", claimed.Branch);
        Assert.Contains($"API=http://localhost:{port}", File.ReadAllText(Path.Combine(worktree, "apps", "web", ".env.local")));
        Assert.Contains($"{port}:3000", File.ReadAllText(Assert.Single(claimed.Repos[0].ComposePaths)));
    }

    [Fact]
    public void An_unmet_need_fails_with_rollback()
    {
        using var store = new TempStore();
        using var repo = new TempGitRepo();
        // A single module that NEEDS a capability nobody provides â€” an unsatisfiable selection.
        File.WriteAllText(Path.Combine(repo.Path, ".sprig.json"), """
            { "schema": 1, "name": "mono",
              "modules": [ { "name": "web", "path": "apps/web",
                "needs": [ { "capability": "absent-api" } ],
                "env": [ { "file": ".env", "set": { "API": "${sprig.absent-api.url}" } } ] } ] }
            """);

        var svc = Build(store);
        var ex = Assert.Throws<WorkspaceException>(() => svc.CreateFromMap("feat-a", null, [Resolve(repo)]));
        Assert.Contains("absent-api", ex.Message);

        // Rolled back: no record, no lingering port lease, no worktree.
        Assert.Null(new InstanceStore(store.Paths).TryLoad("feat-a"));
        Assert.Null(new FilePortStore(store.Paths).Peek("feat-a"));
        Assert.False(Directory.Exists(repo.SiblingWorktree("feat-a")));
    }
}
