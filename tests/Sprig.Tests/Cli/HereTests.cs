using Sprig.Cli;
using Sprig.Engine.Definitions;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Cli;

public class HereTests
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "here-tests");
    static readonly string Web = Path.Combine(Root, "shop-web");
    static readonly string Folder = Path.Combine(Root, "shop-web--feat-x");
    static readonly string Worktree = Path.Combine(Folder, "shop-web");

    static readonly WorkspaceRecord FeatX = new("shop-web", "shop-web/repo.json", "feat-x", "feat-x", "stable",
        [new WorkspaceRepo("shop-web", "shop-web", Web, Worktree, Worktree: true, BranchCreated: true)],
        [], [], WorkspaceState.Up, DateTimeOffset.Now, DateTimeOffset.Now, Folder: Folder);

    static readonly RepoEntry Listed = new("shop-web", Web, InRepo: true, Path.Combine(Web, "sprig.cs"));

    [Fact]
    public void The_repo_folder_and_below_is_the_repo_in_place()
    {
        Assert.Equal(("shop-web", (string?)null), Here.Find(Web, [FeatX], [Listed]));
        Assert.Equal(("shop-web", (string?)null), Here.Find(Path.Combine(Web, "src"), [FeatX], [Listed]));
    }

    [Fact]
    public void A_worktree_and_below_is_its_workspace()
    {
        Assert.Equal(("shop-web", "feat-x"), Here.Find(Worktree, [FeatX], [Listed]));
        Assert.Equal(("shop-web", "feat-x"), Here.Find(Path.Combine(Worktree, "src"), [FeatX], [Listed]));
    }

    [Fact]
    public void The_folder_around_a_workspaces_worktrees_is_that_workspace()
        => Assert.Equal(("shop-web", "feat-x"), Here.Find(Folder, [FeatX], [Listed]));

    [Fact]
    public void Anywhere_else_says_to_init()
        => Assert.Contains("sprig init", Assert.Throws<InvalidOperationException>(() => Here.Find(Root, [FeatX], [Listed])).Message);

    [Fact]
    public void A_workspace_name_alone_finds_the_one_workspace_with_it()
    {
        var other = FeatX with { Orchestration = "storefront", Name = "s5" };

        Assert.Same(FeatX, Here.Named([FeatX, other], "feat-x"));
        Assert.Null(Here.Named([FeatX, other], "nope"));
    }

    [Fact]
    public void A_workspace_name_that_several_have_asks_for_the_id()
    {
        var other = FeatX with { Orchestration = "storefront" };

        var error = Assert.Throws<InvalidOperationException>(() => Here.Named([FeatX, other], "feat-x"));
        Assert.Contains("'shop-web' and 'storefront'", error.Message);
        Assert.Contains("shop-web feat-x", error.Message);
    }
}
