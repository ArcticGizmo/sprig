using Sprig.Core.Graph;

namespace Sprig.App.ViewModels;

/// <summary>One row of the branch-graph list beside the drawn lanes: the commit's short sha, subject, author,
/// relative age, its branch ref pills, and whether it's the current branch (highlighted). Selecting the row
/// picks the commit as the start point; selecting a pill picks that branch.</summary>
public sealed class GraphRowViewModel(GraphNode node, bool isCurrent)
{
    public GraphNode Node => node;
    public string Sha => node.Commit.Sha;
    public string ShortSha => node.Commit.Sha.Length >= 8 ? node.Commit.Sha[..8] : node.Commit.Sha;
    public string Subject => node.Commit.Subject;
    public string Author => node.Commit.Author;
    public IReadOnlyList<string> Refs => node.Commit.Refs;
    public bool HasRefs => node.Commit.Refs.Count > 0;
    public bool IsCurrent => isCurrent;
    public string When => node.Commit.When is { } t ? RelativeTime.Format(t) : "";
}
