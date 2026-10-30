using Sprig.App.ViewModels;
using Sprig.Core.Store;

namespace Sprig.Tests.App;

/// <summary>The workspace card leads with purpose: the label when there is one, else the live branch, else the
/// slot name.</summary>
public class WorkspaceItemViewModelTests
{
    static InstanceRecord Record(string? label = null, string? note = null, string? worktree = null) => new()
    {
        Workspace = "app-1",
        Label = label,
        Note = note,
        Repos = [new InstanceRepo { Name = "api", SourcePath = worktree ?? "nowhere", WorktreePath = worktree ?? "nowhere" }],
    };

    [Fact]
    public void Label_is_the_headline_with_slot_and_branch_beneath()
    {
        using var repo = new TempGitRepo();
        var vm = new WorkspaceItemViewModel(Record(label: "Risk matrix overrides", note: "waiting on review", worktree: repo.Path));

        Assert.Equal("Risk matrix overrides", vm.Headline);
        Assert.Equal("app-1 · main", vm.Subtitle);
        Assert.True(vm.HasNote);
    }

    [Fact]
    public void Unlabelled_shows_the_live_branch()
    {
        using var repo = new TempGitRepo();
        var vm = new WorkspaceItemViewModel(Record(worktree: repo.Path));

        Assert.Equal("main", vm.Headline);
        Assert.Equal("app-1", vm.Subtitle);
        Assert.False(vm.HasNote);
    }

    [Fact]
    public void A_free_workspace_headlines_its_branch_and_offers_its_last_label_for_reuse()
    {
        using var repo = new TempGitRepo();
        var vm = new WorkspaceItemViewModel(Record(worktree: repo.Path) with { ReleasedLabel = "Risk matrix overrides" });

        Assert.Equal("main", vm.Headline);
        Assert.Equal("Risk matrix overrides", vm.ReuseHint);
    }

    [Fact]
    public void Reuse_hint_falls_back_to_the_headline()
    {
        using var repo = new TempGitRepo();
        Assert.Equal("main", new WorkspaceItemViewModel(Record(worktree: repo.Path)).ReuseHint);
    }

    [Fact]
    public void Unreadable_worktree_falls_back_to_the_slot_name()
    {
        var vm = new WorkspaceItemViewModel(Record());
        Assert.Equal("", vm.LiveBranch);
        Assert.Equal("app-1", vm.Headline);
    }
}
