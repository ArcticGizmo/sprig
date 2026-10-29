using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

public class WorkspacePurposeTests
{
    static (WorkspaceService svc, InstanceStore store) Build(TempStore s)
    {
        var svc = new WorkspaceService(
            new GitService(new ProcessRunner()), new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService { Available = false }, s.Paths);
        return (svc, new InstanceStore(s.Paths));
    }

    // A record whose worktrees are plain directories under the store root, each with a .sprig.json declaring
    // `modules` (name → repo-relative path).
    static string Seed(TempStore s, InstanceStore store, string workspace, string repo, params (string Name, string Path)[] modules)
    {
        var worktree = Path.Combine(s.Root, "worktrees", $"{repo}--{workspace}");
        Directory.CreateDirectory(worktree);
        var modJson = string.Join(",", modules.Select(m => $$"""{ "name": "{{m.Name}}", "path": "{{m.Path}}" }"""));
        File.WriteAllText(Path.Combine(worktree, ".sprig.json"), $$"""{ "schema": 1, "name": "{{repo}}", "modules": [{{modJson}}] }""");
        foreach (var m in modules.Where(m => m.Path.Length > 0))
            Directory.CreateDirectory(Path.Combine(worktree, m.Path));
        var existing = store.TryLoad(workspace);
        var repos = (existing?.Repos ?? []).Append(new InstanceRepo { Name = repo, SourcePath = worktree, WorktreePath = worktree });
        store.Save((existing ?? new InstanceRecord { Workspace = workspace }) with { Repos = repos.ToList() });
        return worktree;
    }

    [Fact]
    public void Label_and_note_are_set_trimmed_and_cleared()
    {
        using var s = new TempStore();
        var (svc, store) = Build(s);
        Seed(s, store, "ws1", "api");

        svc.SetLabel("ws1", "  Risk matrix overrides ");
        svc.SetNote("ws1", "waiting on review");
        var r = store.TryLoad("ws1")!;
        Assert.Equal("Risk matrix overrides", r.Label);
        Assert.Equal("waiting on review", r.Note);

        svc.SetLabel("ws1", null);
        svc.SetNote("ws1", "   ");
        r = store.TryLoad("ws1")!;
        Assert.Null(r.Label);
        Assert.Null(r.Note);
    }

    [Fact]
    public void Setting_purpose_on_an_unknown_workspace_throws()
    {
        using var s = new TempStore();
        var (svc, _) = Build(s);
        Assert.Throws<WorkspaceException>(() => svc.SetLabel("nope", "x"));
    }

    [Fact]
    public void Locate_names_the_repo_and_deepest_module()
    {
        using var s = new TempStore();
        var (svc, store) = Build(s);
        var wt = Seed(s, store, "ws1", "client", ("root", ""), ("spa", "apps/spa"));
        Directory.CreateDirectory(Path.Combine(wt, "apps", "spa", "src"));

        var at = svc.Locate(Path.Combine(wt, "apps", "spa", "src"))!;
        Assert.Equal("ws1", at.Record.Workspace);
        Assert.Equal("client", at.Repo.Name);
        Assert.Equal("spa", at.Module);

        Assert.Equal("root", svc.Locate(wt)!.Module);
    }

    [Fact]
    public void Locate_picks_the_right_repo_in_a_multi_repo_workspace()
    {
        using var s = new TempStore();
        var (svc, store) = Build(s);
        Seed(s, store, "ws1", "api");
        var client = Seed(s, store, "ws1", "client");

        Assert.Equal("client", svc.Locate(client)!.Repo.Name);
    }

    [Fact]
    public void Locate_ignores_a_sibling_sharing_a_name_prefix()
    {
        using var s = new TempStore();
        var (svc, store) = Build(s);
        var wt = Seed(s, store, "ws1", "api");
        var lookalike = wt + "-old";
        Directory.CreateDirectory(lookalike);

        Assert.Null(svc.Locate(lookalike));
        Assert.Null(svc.Locate(s.Root));
    }
}
