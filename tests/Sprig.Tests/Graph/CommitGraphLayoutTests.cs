using Sprig.Core.Git;
using Sprig.Core.Graph;

namespace Sprig.Tests.Graph;

/// <summary>The swimlane layout is pure over a commit list — these pin the lane assignment for the shapes
/// that matter: a straight line, a branch that merges back, and edges pointing at the right rows/lanes.</summary>
public class CommitGraphLayoutTests
{
    static GraphCommit C(string sha, params string[] parents) =>
        new(sha, parents, [], "t", null, sha);

    static int LaneOf(CommitGraph g, string sha) => g.Nodes.Single(n => n.Commit.Sha == sha).Lane;

    [Fact]
    public void Linear_history_stays_in_one_lane()
    {
        // a → b → c (newest first), each the parent of the one above.
        var g = CommitGraphLayout.Build([C("a", "b"), C("b", "c"), C("c")]);

        Assert.Equal(1, g.LaneCount);
        Assert.Equal(0, LaneOf(g, "a"));
        Assert.Equal(0, LaneOf(g, "b"));
        Assert.Equal(0, LaneOf(g, "c"));
    }

    [Fact]
    public void A_branch_takes_a_second_lane_and_merges_back_into_the_first()
    {
        // Two tips sharing a root: A (row0) and B (row1) both have parent C (row2, root).
        var g = CommitGraphLayout.Build([C("A", "C"), C("B", "C"), C("C")]);

        Assert.Equal(2, g.LaneCount);
        Assert.Equal(0, LaneOf(g, "A")); // first tip keeps lane 0
        Assert.Equal(1, LaneOf(g, "B")); // second tip opens lane 1
        Assert.Equal(0, LaneOf(g, "C")); // the shared parent converges to lane 0

        // B's line runs from its lane down into C's lane (a merge/convergence).
        Assert.Contains(g.Links, l => l.FromRow == 1 && l.FromLane == 1 && l.ToRow == 2 && l.ToLane == 0);
        // A continues straight down into C.
        Assert.Contains(g.Links, l => l.FromRow == 0 && l.FromLane == 0 && l.ToRow == 2 && l.ToLane == 0);
    }

    [Fact]
    public void Row_cells_capture_pass_through_and_convergence()
    {
        var g = CommitGraphLayout.Build([C("A", "C"), C("B", "C"), C("C")]);

        // Row 0 (A, lane 0): a line down toward its parent in lane 0.
        Assert.Contains(g.Cells[0].Segments, s => s.Kind == SegmentKind.NodeToBottom && s.ToLane == 0);
        // Row 1 (B, lane 1): A's line to C passes straight through lane 0; B descends in its own lane 1
        // (it only merges into C's lane at C's row, below).
        Assert.Contains(g.Cells[1].Segments, s => s.Kind == SegmentKind.PassThrough && s.FromLane == 0);
        Assert.Contains(g.Cells[1].Segments, s => s.Kind == SegmentKind.NodeToBottom && s.ToLane == 1);
        // Row 2 (C, lane 0): both incoming lanes converge into the node.
        Assert.Equal(2, g.Cells[2].Segments.Count(s => s.Kind == SegmentKind.TopToNode));
        Assert.All(g.Cells, c => Assert.Equal(g.LaneCount, c.LaneCount));
    }

    [Fact]
    public void A_parent_off_the_bottom_of_the_window_is_dropped_not_crashed()
    {
        // 'a' points at 'z', which isn't in the (truncated) window — the link is simply omitted.
        var g = CommitGraphLayout.Build([C("a", "z")]);

        Assert.Single(g.Nodes);
        Assert.Empty(g.Links);
    }
}
