using Sprig.Core.Git;

namespace Sprig.Core.Graph;

/// <summary>A commit placed in the graph: its row (index in the newest-first list) and its lane (column).</summary>
public sealed record GraphNode(GraphCommit Commit, int Row, int Lane);

/// <summary>A line to draw from a commit down to one of its parents, in lane/row coordinates (kept for tests
/// and whole-graph consumers). The per-row renderer uses <see cref="GraphRowRender"/> instead.</summary>
public sealed record GraphLink(int FromRow, int FromLane, int ToRow, int ToLane);

/// <summary>How a lane segment runs within one row's cell.</summary>
public enum SegmentKind
{
    /// <summary>A lane passing straight through the row (top edge → bottom edge, same lane).</summary>
    PassThrough,
    /// <summary>An incoming lane converging into this row's commit (top edge → the dot).</summary>
    TopToNode,
    /// <summary>An outgoing lane leaving this row's commit toward a parent below (the dot → bottom edge).</summary>
    NodeToBottom,
}

/// <summary>One lane line within a row cell, in lane indices; the renderer maps lanes to x and the row's
/// actual height to y.</summary>
public sealed record GraphSegment(int FromLane, int ToLane, SegmentKind Kind);

/// <summary>Everything needed to draw one row's slice of the graph independently: the dot's lane, the total
/// lane count (for width), and the lane segments crossing this row. Because each row draws itself, rows can
/// be any height (e.g. a wrapped message) and the dots still line up.</summary>
public sealed record GraphRowRender(int NodeLane, int LaneCount, IReadOnlyList<GraphSegment> Segments);

/// <summary>The laid-out graph: nodes (with lanes), links, per-row render cells, and how many lanes wide.</summary>
public sealed record CommitGraph(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphLink> Links,
    IReadOnlyList<GraphRowRender> Cells,
    int LaneCount);

/// <summary>
/// Swimlane layout for a newest-first commit list (à la GitKraken): assign every commit a lane so a branch's
/// mainline stays in one column and branches/merges shift lanes. Pure and deterministic — no git, no drawing.
/// <para>
/// Walks top→bottom tracking, per lane, the sha that lane is next expecting (reserved by an already-seen
/// child). A commit takes the lane that expected it (converging any others into it); a tip with no expectant
/// lane opens a fresh one. Its first parent continues its lane; extra parents (a merge) open new lanes.
/// </para>
/// </summary>
public static class CommitGraphLayout
{
    public static CommitGraph Build(IReadOnlyList<GraphCommit> commits)
    {
        var rowOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < commits.Count; i++) rowOf[commits[i].Sha] = i;

        var lanes = new List<string?>();          // lanes[i] = the sha lane i is next expecting, or null (free)
        var laneOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodes = new List<GraphNode>(commits.Count);
        var cells = new List<GraphRowRender>(commits.Count);
        var maxLane = 0;

        int FirstFree()
        {
            for (var i = 0; i < lanes.Count; i++) if (lanes[i] is null) return i;
            lanes.Add(null);
            return lanes.Count - 1;
        }
        int Find(string sha)
        {
            for (var i = 0; i < lanes.Count; i++) if (lanes[i] == sha) return i;
            return -1;
        }

        // Cells reference the final lane count; fill NodeLane/Segments now, stamp LaneCount at the end.
        var pendingSegments = new List<List<GraphSegment>>(commits.Count);

        for (var row = 0; row < commits.Count; row++)
        {
            var c = commits[row];
            var before = new List<string?>(lanes); // snapshot incoming lanes for this row's segments

            var myLane = Find(c.Sha);
            var converged = new List<int>();
            if (myLane != -1)
            {
                for (var i = 0; i < lanes.Count; i++)
                    if (i != myLane && lanes[i] == c.Sha) converged.Add(i);
            }
            else
            {
                myLane = FirstFree();
            }

            laneOf[c.Sha] = myLane;
            nodes.Add(new GraphNode(c, row, myLane));
            maxLane = Math.Max(maxLane, myLane);

            foreach (var j in converged) lanes[j] = null;

            var parentLanes = new List<int>();
            if (c.Parents.Count == 0)
            {
                lanes[myLane] = null; // a root — the lane ends
            }
            else
            {
                lanes[myLane] = c.Parents[0]; // first parent stays in this lane (the branch's mainline)
                parentLanes.Add(myLane);
                for (var k = 1; k < c.Parents.Count; k++)
                {
                    var pk = c.Parents[k];
                    var existing = Find(pk);
                    if (existing != -1) { parentLanes.Add(existing); continue; }
                    var idx = FirstFree();
                    lanes[idx] = pk;
                    parentLanes.Add(idx);
                    maxLane = Math.Max(maxLane, idx);
                }
            }

            // Build this row's segments from the before-snapshot and the parent lanes just assigned.
            var segs = new List<GraphSegment>();
            for (var i = 0; i < before.Count; i++)
            {
                if (before[i] is null) continue;
                if (before[i] == c.Sha) segs.Add(new GraphSegment(i, myLane, SegmentKind.TopToNode));
                else segs.Add(new GraphSegment(i, i, SegmentKind.PassThrough));
            }
            foreach (var pl in parentLanes) segs.Add(new GraphSegment(myLane, pl, SegmentKind.NodeToBottom));
            pendingSegments.Add(segs);
        }

        var laneCount = maxLane + 1;
        for (var row = 0; row < nodes.Count; row++)
            cells.Add(new GraphRowRender(nodes[row].Lane, laneCount, pendingSegments[row]));

        var links = new List<GraphLink>();
        foreach (var n in nodes)
            foreach (var parent in n.Commit.Parents)
                if (rowOf.TryGetValue(parent, out var pr) && laneOf.TryGetValue(parent, out var pl))
                    links.Add(new GraphLink(n.Row, n.Lane, pr, pl));

        return new CommitGraph(nodes, links, cells, laneCount);
    }
}
