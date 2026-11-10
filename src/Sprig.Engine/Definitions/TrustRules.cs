using Sprig.Engine.Git;

namespace Sprig.Engine.Definitions;

/// <summary>
/// The trust store's answer, with the user's own edits approved rather than asked about. Approval exists so that
/// looking at code from someone else (a clone, a pulled branch) never runs it; asking about an edit the user made
/// protects nothing. What counts as theirs is always a change to a version they approved: a definition kept in sprig,
/// wherever it was edited, and a repo's sprig.cs changed in its working tree but not committed, when the committed
/// version was approved. A copy into sprig of a file nobody approved, or a version that arrived in a commit, still asks.
/// </summary>
public sealed class TrustRules(ITrustStore trust, StoredDefinitions stored, GitClient? git = null)
{
    /// <param name="file">The file as it is on disk.</param>
    /// <param name="trustAs">The file whose approvals count for it, when that's another: a worktree's copy of a repo's
    /// sprig.cs is approved as the repo's own.</param>
    public TrustDecision Check(string file, string sha256, string? trustAs = null)
    {
        var trustPath = trustAs ?? file;
        var decision = trust.Check(trustPath, sha256);
        if (decision.State != TrustState.Changed || !IsOwnEdit(file, trustPath, sha256)) return decision;
        // Recorded, so the version stays approved once it's committed and the edit is no longer uncommitted.
        trust.Approve(trustPath, sha256);
        return new TrustDecision(TrustState.Trusted, sha256);
    }

    bool IsOwnEdit(string file, string trustPath, string sha256)
    {
        if (stored.IsKept(trustPath)) return true;
        if (git?.CommittedText(file)?.Replace("\r\n", "\n") is not { } committed) return false;
        // git's output arrives a line at a time, which loses whether the file's last line ended in a newline.
        string[] versions = committed.EndsWith('\n') ? [committed, committed[..^1]] : [committed];
        var hashes = versions.Select(DefinitionHash.Of).ToArray();
        return !hashes.Contains(sha256) && hashes.Any(h => trust.Check(trustPath, h).State == TrustState.Trusted);
    }
}
