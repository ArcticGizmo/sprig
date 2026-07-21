using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

public class SetupStateTests
{
    [Fact]
    public void No_repos_is_the_empty_stage_and_points_at_adding_a_repo()
    {
        var s = new SetupState(0, 0, 0);

        Assert.Equal(SetupStage.Empty, s.Stage);
        Assert.True(s.ReposNext);
        Assert.False(s.ReposDone);
        Assert.False(s.StacksNext);
        Assert.False(s.WorkspacesNext);
        Assert.Equal("Add a repo  →", s.NextCta);
        Assert.StartsWith("STEP 1 OF 3", s.NextKicker);
    }

    [Fact]
    public void Repos_but_no_stacks_points_at_wiring_a_stack()
    {
        var s = new SetupState(2, 0, 0);

        Assert.Equal(SetupStage.ReposReady, s.Stage);
        Assert.True(s.ReposDone);
        Assert.True(s.StacksNext);
        Assert.False(s.WorkspacesNext);
        Assert.Equal("Wire your repos into a stack", s.NextTitle);
        Assert.Equal("Wire a stack  →", s.NextCta);
        Assert.Equal("NEXT BEST ACTION", s.NextKicker);
    }

    [Fact]
    public void Stack_but_no_workspaces_points_at_spinning_one_up()
    {
        var s = new SetupState(2, 1, 0);

        Assert.Equal(SetupStage.StackReady, s.Stage);
        Assert.True(s.StacksDone);
        Assert.True(s.WorkspacesNext);
        Assert.False(s.ReposNext);
        Assert.False(s.StacksNext);
        Assert.Equal("Spin up your first workspace", s.NextTitle);
        Assert.Equal("New workspace  →", s.NextCta);
    }

    [Fact]
    public void Everything_present_is_running_with_no_next_step_highlighted()
    {
        var s = new SetupState(2, 1, 3);

        Assert.Equal(SetupStage.Running, s.Stage);
        Assert.True(s.ReposDone);
        Assert.True(s.StacksDone);
        Assert.True(s.WorkspacesDone);
        Assert.False(s.ReposNext);
        Assert.False(s.StacksNext);
        Assert.False(s.WorkspacesNext);
        Assert.Equal("Spin up another workspace", s.NextTitle);
        Assert.Equal("New workspace  →", s.NextCta);
    }

    [Theory]
    [InlineData(0, "none yet")]
    [InlineData(1, "1 registered")]
    [InlineData(3, "3 registered")]
    public void Repos_count_label_pluralises(int n, string expected)
        => Assert.Equal(expected, new SetupState(n, 0, 0).ReposCountLabel);

    [Theory]
    [InlineData(0, "none yet")]
    [InlineData(1, "1 stack")]
    [InlineData(2, "2 stacks")]
    public void Stacks_count_label_pluralises(int n, string expected)
        => Assert.Equal(expected, new SetupState(1, n, 0).StacksCountLabel);

    [Theory]
    [InlineData(0, "none yet")]
    [InlineData(1, "1 running")]
    [InlineData(4, "4 running")]
    public void Workspaces_count_label_pluralises(int n, string expected)
        => Assert.Equal(expected, new SetupState(1, 1, n).WorkspacesCountLabel);
}
