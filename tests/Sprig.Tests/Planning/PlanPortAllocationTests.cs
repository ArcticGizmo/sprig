using System.Collections.Generic;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Planning;

/// <summary>
/// Create allocates from the <b>plan</b>, not the raw stack. Today that only means a declared port nothing
/// binds to stops being reserved; once an overlay can rewrite a binding it is what stops a pooled
/// workspace from still burning the port it no longer uses.
/// </summary>
public class PlanPortAllocationTests
{
    const string WebConfig = """
        { "schema":2, "name":"web", "inputs":[ { "name":"frontend", "example":"3000" } ],
          "env":[ { "file":".env", "set": { "PORT": "${sprig.frontend}" } } ] }
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
    public void A_declared_port_nothing_references_is_not_allocated()
    {
        using var store = new TempStore();
        using var webRepo = new TempGitRepo("web");
        var svc = Build(store);

        var stack = new ResolvedStack("web-only",
            [Resolve(webRepo, WebConfig)],
            ["frontend_port", "orphan_port"],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["web"] = new Dictionary<string, string> { ["frontend"] = "${sprig.ports.frontend_port}" },
            });

        var record = svc.Create(stack, "fe");

        Assert.True(record.Ports.ContainsKey("frontend_port"));
        Assert.False(record.Ports.ContainsKey("orphan_port"));
        Assert.Equal(record.Ports["frontend_port"].ToString(), record.Repos[0].Inputs["frontend"]);
    }

    [Fact]
    public void Preview_plan_allocates_nothing_and_leaves_no_lease_behind()
    {
        using var store = new TempStore();
        using var webRepo = new TempGitRepo("web");
        var portStore = new FilePortStore(store.Paths);
        var svc = new WorkspaceService(
            new GitService(new ProcessRunner()), portStore, new InstanceStore(store.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false },
            store.Paths);

        var stack = new ResolvedStack("web-only",
            [Resolve(webRepo, WebConfig)],
            ["frontend_port"],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["web"] = new Dictionary<string, string> { ["frontend"] = "${sprig.ports.frontend_port}" },
            });

        var plan = svc.PreviewPlan(stack, "fe");

        Assert.Equal("{frontend_port}", plan.Repos[0].Inputs["frontend"]);
        Assert.Empty(portStore.ListLeases());
        Assert.False(Directory.Exists(webRepo.SiblingWorktree("fe")));
    }
}
