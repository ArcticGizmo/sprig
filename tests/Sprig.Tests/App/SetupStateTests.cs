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
        Assert.False(s.MapsNext);
        Assert.False(s.WorkspacesNext);
        Assert.Equal("Add a repo  →", s.NextCta);
        Assert.StartsWith("STEP 1 OF 3", s.NextKicker);
    }

    [Fact]
    public void Repos_but_no_stacks_points_at_composing_a_map()
    {
        var s = new SetupState(2, 0, 0);

        Assert.Equal(SetupStage.ReposReady, s.Stage);
        Assert.True(s.ReposDone);
        Assert.True(s.MapsNext);
        Assert.False(s.WorkspacesNext);
        Assert.Equal("Compose your repos into a map", s.NextTitle);
        Assert.Equal("Compose a map  →", s.NextCta);
        Assert.Equal("NEXT BEST ACTION", s.NextKicker);
    }

    [Fact]
    public void Map_but_no_workspaces_points_at_spinning_one_up()
    {
        var s = new SetupState(2, 1, 0);

        Assert.Equal(SetupStage.MapReady, s.Stage);
        Assert.True(s.MapsDone);
        Assert.True(s.WorkspacesNext);
        Assert.False(s.ReposNext);
        Assert.False(s.MapsNext);
        Assert.Equal("Spin up your first workspace", s.NextTitle);
        Assert.Equal("New workspace  →", s.NextCta);
    }

    [Fact]
    public void Everything_present_is_running_with_no_next_step_highlighted()
    {
        var s = new SetupState(2, 1, 3);

        Assert.Equal(SetupStage.Running, s.Stage);
        Assert.True(s.ReposDone);
        Assert.True(s.MapsDone);
        Assert.True(s.WorkspacesDone);
        Assert.False(s.ReposNext);
        Assert.False(s.MapsNext);
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
    [InlineData(1, "1 map")]
    [InlineData(2, "2 maps")]
    public void Maps_count_label_pluralises(int n, string expected)
        => Assert.Equal(expected, new SetupState(1, n, 0).MapsCountLabel);

    [Theory]
    [InlineData(0, "none yet")]
    [InlineData(1, "1 running")]
    [InlineData(4, "4 running")]
    public void Workspaces_count_label_pluralises(int n, string expected)
        => Assert.Equal(expected, new SetupState(1, 1, n).WorkspacesCountLabel);
}
