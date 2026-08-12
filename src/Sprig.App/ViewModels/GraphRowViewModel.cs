using Sprig.Core.Graph;

namespace Sprig.App.ViewModels;

/// <summary>One row of the branch-graph list beside the drawn lanes: the commit's short sha, subject, author,
/// relative age, its colour-coded branch pills, and whether it's the current branch. Selecting the row picks
/// the commit as the start point; selecting a pill picks that branch.</summary>
public sealed class GraphRowViewModel
{
    public GraphRowViewModel(GraphNode node, string? currentBranch)
    {
        Node = node;
        IsCurrent = currentBranch is not null && node.Commit.Refs.Contains(currentBranch);
        Refs = node.Commit.Refs
            .Select(r => new GraphRefViewModel(r, Classify(r, currentBranch)))
            .ToList();
    }

    public GraphNode Node { get; }
    public string Sha => Node.Commit.Sha;
    public string ShortSha => Node.Commit.Sha.Length >= 8 ? Node.Commit.Sha[..8] : Node.Commit.Sha;
    public string Subject => Node.Commit.Subject;
    public string Author => Node.Commit.Author;
    public IReadOnlyList<GraphRefViewModel> Refs { get; }
    public bool HasRefs => Refs.Count > 0;
    public bool IsCurrent { get; }
    public string When => Node.Commit.When is { } t ? RelativeTime.Format(t) : "";

    static GraphRefViewModel.RefKind Classify(string reference, string? currentBranch)
    {
        if (reference == currentBranch) return GraphRefViewModel.RefKind.Current;
        var tail = reference.Contains('/') ? reference[(reference.LastIndexOf('/') + 1)..] : reference;
        return tail is "main" or "master" ? GraphRefViewModel.RefKind.Default : GraphRefViewModel.RefKind.Other;
    }
}
