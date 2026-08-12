using Sprig.Core.Git;

namespace Sprig.Core.Graph;

/// <summary>A commit placed in the graph: its row (index in the newest-first list) and its lane (column).</summary>
public sealed record GraphNode(GraphCommit Commit, int Row, int Lane);

/// <summary>A line to draw from a commit down to one of its parents, in lane/row coordinates. The parent is
/// always at a later (larger) row. A link whose parent is off the bottom of the window is omitted.</summary>
public sealed record GraphLink(int FromRow, int FromLane, int ToRow, int ToLane);

/// <summary>The laid-out graph: nodes (with lanes), the links between them, and how many lanes wide it is.</summary>
public sealed record CommitGraph(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphLink> Links, int LaneCount);

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

        for (var row = 0; row < commits.Count; row++)
        {
            var c = commits[row];

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

            // Other lanes that were waiting for this commit merge into its lane and end here.
            foreach (var j in converged) lanes[j] = null;

            if (c.Parents.Count == 0)
            {
                lanes[myLane] = null; // a root — the lane ends
            }
            else
            {
                lanes[myLane] = c.Parents[0]; // first parent stays in this lane (the branch's mainline)
                for (var k = 1; k < c.Parents.Count; k++)
                {
                    var pk = c.Parents[k];
                    if (Find(pk) != -1) continue; // already reserved by another child — they'll converge
                    var idx = FirstFree();
                    lanes[idx] = pk;
                    maxLane = Math.Max(maxLane, idx);
                }
            }
        }

        var links = new List<GraphLink>();
        foreach (var n in nodes)
            foreach (var parent in n.Commit.Parents)
                if (rowOf.TryGetValue(parent, out var pr) && laneOf.TryGetValue(parent, out var pl))
                    links.Add(new GraphLink(n.Row, n.Lane, pr, pl));

        return new CommitGraph(nodes, links, maxLane + 1);
    }
}
