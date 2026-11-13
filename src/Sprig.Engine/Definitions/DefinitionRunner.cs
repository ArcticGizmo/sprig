using System.Diagnostics;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;
using Sprig.Model;

namespace Sprig.Engine.Definitions;

public abstract record DefinitionResult(string Path, string Sha256, TimeSpan Elapsed);

/// <summary>The model read from the file.</summary>
public sealed record Evaluated(string Path, string Sha256, TimeSpan Elapsed, DefinitionModel Model)
    : DefinitionResult(Path, Sha256, Elapsed);

/// <summary>Not read: this exact content hasn't been approved. <paramref name="ApprovedSha256"/> is set when an earlier
/// version was. Approve it under <paramref name="TrustPath"/>, which differs from <paramref name="Path"/> for a copy of a
/// repo's definition in a workspace's worktree.</summary>
public sealed record NeedsTrust(string Path, string Sha256, TimeSpan Elapsed, string? ApprovedSha256, string TrustPath)
    : DefinitionResult(Path, Sha256, Elapsed)
{
    public bool IsCopy => Path != TrustPath;
}

/// <summary>Read, but it doesn't describe anything sprig can run: each problem names its line.</summary>
public sealed record Invalid(string Path, string Sha256, TimeSpan Elapsed, IReadOnlyList<PlanProblem> Problems)
    : DefinitionResult(Path, Sha256, Elapsed);

/// <summary>
/// Turns a definition file into its model, once its content is approved: a definition holds commands sprig will run,
/// so one from someone else (a clone, a pulled branch) is never used unseen.
/// </summary>
/// <param name="read">How a file's text becomes its model; <see cref="DefinitionReader.Read"/> unless a test stands in.</param>
public sealed class DefinitionRunner(ITrustStore trust, ISprigPaths paths, Git.GitClient? git = null,
    Func<string, string, ReadResult>? read = null)
{
    readonly TrustRules _trust = new(trust, new StoredDefinitions(paths), git);
    readonly Func<string, string, ReadResult> _read = read ?? DefinitionReader.Read;

    /// <param name="trustAs">The file whose approvals count for this one; a worktree's copy of a repo's sprig.yaml is
    /// approved as the repo's own, so each version is approved once per repo rather than once per worktree.</param>
    public DefinitionResult Evaluate(string definitionPath, string? trustAs = null)
    {
        var clock = Stopwatch.StartNew();
        var path = Path.GetFullPath(definitionPath);
        var trustPath = trustAs is null ? path : Path.GetFullPath(trustAs);
        var text = File.ReadAllText(path);
        var sha = DefinitionHash.Of(text);

        var decision = _trust.Check(path, sha, trustPath);
        if (decision.State != TrustState.Trusted) return new NeedsTrust(path, sha, clock.Elapsed, decision.ApprovedSha256, trustPath);

        var result = _read(text, path);
        return result.Model is { } model
            ? new Evaluated(path, sha, clock.Elapsed, model)
            : new Invalid(path, sha, clock.Elapsed, result.Problems);
    }
}
