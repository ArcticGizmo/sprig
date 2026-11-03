using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;
using Sprig.Model;

namespace Sprig.Engine.Trellises;

/// <param name="Repos">Each repo that evaluated, keyed by the path the trellis names it by.</param>
/// <param name="Untrusted">Definitions that weren't run because their current content hasn't been approved.</param>
public sealed record LoadedTrellis(
    string TrellisPath,
    TrellisModel? Trellis,
    IReadOnlyDictionary<string, RepoModel> Repos,
    IReadOnlyList<NeedsTrust> Untrusted,
    IReadOnlyList<PlanProblem> Problems,
    IReadOnlyList<DefinitionResult> Results)
{
    public bool Complete => Trellis is not null && Untrusted.Count == 0 && Problems.Count == 0;
}

/// <summary>Evaluates a trellis and the <c>sprig.cs</c> of every repo it names. Repos are evaluated in parallel; each
/// goes through the same directive, trust and cache checks as any definition.</summary>
public sealed class TrellisLoader(DefinitionRunner runner)
{
    public const string RepoDefinitionFile = "sprig.cs";

    public LoadedTrellis Load(string trellisPath)
    {
        var path = LongPath.Of(trellisPath);
        var problems = new List<PlanProblem>();
        var results = new List<DefinitionResult>();
        var untrusted = new List<NeedsTrust>();
        var repos = new Dictionary<string, RepoModel>(StringComparer.Ordinal);

        var trellisResult = runner.Evaluate(path);
        results.Add(trellisResult);
        var trellis = Accept(trellisResult, problems, untrusted) switch
        {
            TrellisModel t => t,
            RepoModel => Fail($"{Path.GetFileName(path)} describes a repo, not a trellis (it calls Repo.Describe)", path, problems),
            _ => null,
        };
        if (trellis is null) return new LoadedTrellis(path, null, repos, untrusted, problems, results);

        var dir = Path.GetDirectoryName(path)!;
        var definitions = new List<(RepoRefModel reference, string file)>();
        foreach (var reference in trellis.Repos)
        {
            var repoDir = LongPath.Of(Path.Combine(dir, reference.Path));
            var file = Path.Combine(repoDir, RepoDefinitionFile);
            if (!Directory.Exists(repoDir))
                problems.Add(new PlanProblem(ProblemSeverity.Error, $"the repo folder '{repoDir}' doesn't exist", reference.Location));
            else if (!File.Exists(file))
                problems.Add(new PlanProblem(ProblemSeverity.Error, $"'{reference.Path}' has no {RepoDefinitionFile} to describe it", reference.Location));
            else
                definitions.Add((reference, file));
        }

        var evaluated = definitions.AsParallel().AsOrdered()
            .Select(d => (d.reference, result: runner.Evaluate(d.file)))
            .ToList();
        foreach (var (reference, result) in evaluated)
        {
            results.Add(result);
            switch (Accept(result, problems, untrusted))
            {
                case RepoModel repo: repos[reference.Path] = repo; break;
                case TrellisModel: problems.Add(new PlanProblem(ProblemSeverity.Error, $"'{reference.Path}/{RepoDefinitionFile}' describes a trellis, not a repo", reference.Location)); break;
            }
        }

        return new LoadedTrellis(path, trellis, repos, untrusted, problems, results);
    }

    static DefinitionModel? Accept(DefinitionResult result, List<PlanProblem> problems, List<NeedsTrust> untrusted)
    {
        switch (result)
        {
            case Evaluated e:
                return e.Model;
            case NeedsTrust t:
                untrusted.Add(t);
                return null;
            case Rejected r:
                foreach (var v in r.Violations)
                    problems.Add(new PlanProblem(ProblemSeverity.Error, $"{v.Directive}: {v.Reason}", new SourceLocation(r.Path, v.Line)));
                return null;
            case Failed f:
                problems.Add(new PlanProblem(ProblemSeverity.Error, $"{Path.GetFileName(f.Path)} {f.Reason}{CompilerErrors(f.Output)}", new SourceLocation(f.Path, 0)));
                return null;
            default:
                return null;
        }
    }

    /// <summary>The compiler's error lines from <c>dotnet run</c> output, without the build noise around them.</summary>
    static string CompilerErrors(string output)
    {
        var errors = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(": error ", StringComparison.Ordinal))
            .Select(l => l.Contains(" [", StringComparison.Ordinal) ? l[..l.LastIndexOf(" [", StringComparison.Ordinal)] : l)
            .Distinct()
            .Take(5)
            .ToArray();
        return errors.Length == 0 ? "" : "\n  " + string.Join("\n  ", errors);
    }

    static TrellisModel? Fail(string message, string path, List<PlanProblem> problems)
    {
        problems.Add(new PlanProblem(ProblemSeverity.Error, message, new SourceLocation(path, 0)));
        return null;
    }
}
