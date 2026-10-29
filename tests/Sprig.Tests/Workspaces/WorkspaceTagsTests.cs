using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

public class WorkspaceTagsTests
{
    [Theory]
    [InlineData("agent.session", true)]
    [InlineData("ci.run-id", true)]
    [InlineData("flow2.slice", true)]
    [InlineData("session", false)]        // no namespace
    [InlineData("Agent.session", false)]  // upper case
    [InlineData("a.b.c", false)]          // one namespace only
    [InlineData(".session", false)]
    [InlineData("agent.", false)]
    [InlineData("agent session.x", false)]
    public void Keys_are_namespace_dot_name(string key, bool valid) => Assert.Equal(valid, WorkspaceTags.IsValidKey(key));

    [Fact]
    public void A_pair_value_may_be_empty_or_contain_equals()
    {
        Assert.Equal("", WorkspaceTags.ParsePair("ci.note=").Value);
        Assert.Equal("a=b", WorkspaceTags.ParsePair("ci.note=a=b").Value);
        Assert.Throws<WorkspaceException>(() => WorkspaceTags.ParsePair("ci.note"));
    }

    [Fact]
    public void Filters_match_a_value_or_mere_presence()
    {
        var r = new InstanceRecord { Workspace = "w", Tags = new Dictionary<string, string> { ["agent.session"] = "7f3a" } };
        Assert.True(WorkspaceTags.Filter.Parse("agent.session=7f3a").Matches(r));
        Assert.False(WorkspaceTags.Filter.Parse("agent.session=other").Matches(r));
        Assert.True(WorkspaceTags.Filter.Parse("agent.session").Matches(r));
        Assert.False(WorkspaceTags.Filter.Parse("ci.run").Matches(r));
    }
}
