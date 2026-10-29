using System.Text.Json;
using Sprig.Core.Compose;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

public class WorkspaceColourTests
{
    [Fact]
    public void A_slot_keeps_the_same_default_colour()
    {
        Assert.Equal(WorkspaceColour.Default("app-3"), WorkspaceColour.Default("app-3"));
        Assert.True(WorkspaceColour.IsValid(WorkspaceColour.Default("app-3")));
    }

    [Fact]
    public void An_explicit_colour_wins()
        => Assert.Equal("#123456", WorkspaceColour.For(new InstanceRecord { Workspace = "app-3", Colour = "#123456" }));

    [Theory]
    [InlineData("#ffffff", "#000000")]
    [InlineData("#000000", "#ffffff")]
    [InlineData("#2563eb", "#ffffff")]
    [InlineData("#fde047", "#000000")]
    public void Title_text_contrasts_with_the_colour(string background, string expected)
        => Assert.Equal(expected, WorkspaceColour.Foreground(background));

    static WorkspaceService Build(TempStore s) => new(new GitService(new ProcessRunner()), new FilePortStore(s.Paths),
        new InstanceStore(s.Paths), new EnvClobberService(), new ComposeGenerator(), new FakeDockerService(), s.Paths);

    [Fact]
    public void SetColour_validates_and_clears()
    {
        using var s = new TempStore();
        new InstanceStore(s.Paths).Save(new InstanceRecord { Workspace = "ws1" });
        var svc = Build(s);

        Assert.Equal("#abcdef", svc.SetColour("ws1", "#ABCDEF").Colour);
        Assert.Throws<WorkspaceException>(() => svc.SetColour("ws1", "blue"));
        Assert.Null(svc.SetColour("ws1", null).Colour);
    }

    [Fact]
    public void Code_workspace_lives_in_the_store_with_title_and_colour()
    {
        using var s = new TempStore();
        using var repo = new TempGitRepo();
        var record = new InstanceRecord
        {
            Workspace = "ws1", Label = "Audit log", Colour = "#2563eb",
            Repos = [new InstanceRepo { Name = "api", SourcePath = repo.Path, WorktreePath = repo.Path }],
        };

        var file = Build(s).WriteCodeWorkspace(record);

        Assert.StartsWith(s.Paths.InstanceDir("ws1"), file);
        Assert.False(File.Exists(Path.Combine(repo.Path, "ws1.code-workspace"))); // never in the repo
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var settings = doc.RootElement.GetProperty("settings");
        Assert.StartsWith("Audit log · ws1", settings.GetProperty("window.title").GetString());
        Assert.Equal("#2563eb", settings.GetProperty("workbench.colorCustomizations").GetProperty("titleBar.activeBackground").GetString());
        Assert.Equal(repo.Path, doc.RootElement.GetProperty("folders")[0].GetProperty("path").GetString());
    }
}
