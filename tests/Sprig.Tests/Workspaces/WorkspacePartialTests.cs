using System.Collections.Generic;
using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

/// <summary>
/// Partial workspaces: create a stack minus some of its repos. Drives the real registry → stack →
/// resolver → create path, because the whole point is what the resolver hands create (a narrowed
/// repo list and a narrowed port list) rather than anything create does differently itself.
/// </summary>
[Collection("git-heavy")]
public class WorkspacePartialTests
{
    // api has infra (a compose file to override) and owns api_port.
    const string ApiConfig = """
        { "schema":2, "name":"api", "inputs":[ { "name":"port", "example":"5000" } ],
          "env":[ { "file":".env", "set": { "PORT": "${sprig.port}" } } ],
          "compose":[ { "file":"docker-compose.yml", "overrides":[
              { "path":["services","api","ports","0"], "template":"${sprig.port}:8080" } ] } ] }
        """;

    const string ApiCompose = """
        services:
          api:
            image: mcr.microsoft.com/dotnet/aspnet:10.0
            ports:
              - "8080:8080"
        """;

    // web points at the api's port and owns web_port (its own dev server) — no infra.
    const string WebConfig = """
        { "schema":2, "name":"web",
          "inputs":[ { "name":"apiUrl", "example":"http://localhost:5000" },
                     { "name":"devPort", "example":"5173" } ],
          "env":[ { "file":".env", "set": {
              "VITE_API_URL": "${sprig.apiUrl}", "PORT": "${sprig.devPort}" } } ] }
        """;

    sealed record Harness(
        WorkspaceService Svc, StackResolver Resolver, InstanceStore Instances, FakeDockerService Docker);

    static Harness Build(TempStore store, TempGitRepo api, TempGitRepo web)
    {
        File.WriteAllText(Path.Combine(api.Path, ".sprig.json"), ApiConfig);
        File.WriteAllText(Path.Combine(api.Path, "docker-compose.yml"), ApiCompose);
        api.Git("add", "-A");
        api.Git("-c", "user.email=t@sprig", "-c", "user.name=sprig", "commit", "-m", "add compose");
        File.WriteAllText(Path.Combine(web.Path, ".sprig.json"), WebConfig);

        var git = new GitService(new ProcessRunner());
        var instances = new InstanceStore(store.Paths);
        var docker = new FakeDockerService { Available = true };
        var svc = new WorkspaceService(git, new FilePortStore(store.Paths), instances,
            new EnvClobberService(), new ComposeGenerator(), docker, store.Paths);

        var registry = new RepoRegistryStore(store.Paths);
        registry.Add(api.Path);
        registry.Add(web.Path);
        var stacks = new StackStore(store.Paths, registry, instances);
        stacks.Save(new StackDefinition
        {
            Name = "web+api",
            Repos = ["api", "web"],
            Ports = ["api_port", "web_port"],
            Bindings = new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["api"] = new Dictionary<string, string> { ["port"] = "${sprig.ports.api_port}" },
                ["web"] = new Dictionary<string, string>
                {
                    ["apiUrl"] = "http://localhost:${sprig.ports.api_port}",
                    ["devPort"] = "${sprig.ports.web_port}",
                },
            },
        });

        return new Harness(svc, new StackResolver(registry, stacks, git), instances, docker);
    }

    [Fact]
    public void Dropping_a_repo_leaves_its_worktree_and_ports_unprovisioned()
    {
        using var store = new TempStore();
        using var api = new TempGitRepo("api");
        using var web = new TempGitRepo("web");
        var h = Build(store, api, web);

        var record = h.Svc.Create(h.Resolver.Resolve("web+api", ["api"]), "backend-only");

        // Only the kept repo is materialised.
        Assert.Equal(["api"], record.Repos.Select(r => r.Name));
        Assert.True(Directory.Exists(api.SiblingWorktree("backend-only")));
        Assert.False(Directory.Exists(web.SiblingWorktree("backend-only")));

        // web_port had no consumer left, so it was never provisioned; api_port still is.
        Assert.Equal(["api_port"], record.Ports.Keys);
        Assert.Equal(["web_port"], record.SkippedPorts);
        Assert.Equal(["web"], record.ExcludedRepos);
        Assert.True(record.IsPartial);

        // Nothing leaked into the port store either — a re-read sees only the provisioned lease.
        Assert.Equal(["api_port"], new FilePortStore(store.Paths).Peek("backend-only")!.Keys);
    }

    [Fact]
    public void Dropping_the_infra_repo_ignores_its_compose_files_entirely()
    {
        using var store = new TempStore();
        using var api = new TempGitRepo("api");
        using var web = new TempGitRepo("web");
        var h = Build(store, api, web);

        var record = h.Svc.Create(h.Resolver.Resolve("web+api", ["web"]), "frontend-only");

        // api owned the only compose file: no generated copy, and no infra to drive.
        Assert.Equal(["web"], record.Repos.Select(r => r.Name));
        Assert.All(record.Repos, r => Assert.Empty(r.ComposePaths));
        Assert.Empty(Directory.GetFiles(store.Paths.InstanceDir("frontend-only"), "*.sprig.yml"));
        Assert.Throws<WorkspaceException>(() => h.Svc.Up("frontend-only"));
        Assert.Empty(h.Docker.Ups);

        // web still points at api_port (its binding references it), so that port is provisioned even
        // though nothing serves it — only ports with no consumer at all are dropped.
        Assert.Equal(["api_port", "web_port"], record.Ports.Keys.OrderBy(k => k));
        Assert.Empty(record.SkippedPorts);
        Assert.Equal(["api"], record.ExcludedRepos);
    }

    [Fact]
    public void A_full_create_is_unchanged_and_not_marked_partial()
    {
        using var store = new TempStore();
        using var api = new TempGitRepo("api");
        using var web = new TempGitRepo("web");
        var h = Build(store, api, web);

        var record = h.Svc.Create(h.Resolver.Resolve("web+api"), "everything");

        Assert.Equal(["api", "web"], record.Repos.Select(r => r.Name));
        Assert.Equal(["api_port", "web_port"], record.Ports.Keys.OrderBy(k => k));
        Assert.False(record.IsPartial);
        Assert.Empty(record.ExcludedRepos);
        Assert.Empty(record.SkippedPorts);

        // The kept infra still runs: one generated compose file, handed to docker on up.
        h.Svc.Up("everything");
        Assert.Single(h.Docker.Ups);
        Assert.Single(h.Docker.ComposeFilesSeen[0]);
    }

    [Fact]
    public void The_plan_only_lists_steps_for_the_selected_repos()
    {
        using var store = new TempStore();
        using var api = new TempGitRepo("api");
        using var web = new TempGitRepo("web");
        var h = Build(store, api, web);

        var plan = h.Svc.PlanCreate(h.Resolver.Resolve("web+api", ["web"]), "frontend-only");

        Assert.Contains(plan, s => s.Label == "Create worktree — web");
        Assert.DoesNotContain(plan, s => s.Label.EndsWith("— api"));
        Assert.DoesNotContain(plan, s => s.Label.StartsWith("Generate compose"));
    }

    [Fact]
    public void Deselecting_every_repo_is_refused()
    {
        using var store = new TempStore();
        using var api = new TempGitRepo("api");
        using var web = new TempGitRepo("web");
        var h = Build(store, api, web);

        Assert.Throws<StackException>(() => h.Resolver.Resolve("web+api", []));
    }
}
