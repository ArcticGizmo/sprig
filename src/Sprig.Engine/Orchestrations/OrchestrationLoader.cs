using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;
using Sprig.Model;

namespace Sprig.Engine.Orchestrations;

/// <param name="Repos">Each repo that evaluated, keyed by the path the orchestration names it by.</param>
/// <param name="Untrusted">Definitions that weren't run because their current content hasn't been approved.</param>
/// <param name="RepoDirs">Each repo's own folder, keyed like <see cref="Repos"/>.</param>
/// <param name="WorkspaceDirs">The workspace folders the repos' definitions were read from, when they weren't read from the
/// repos' own folders.</param>
public sealed record LoadedOrchestration(
    string OrchestrationPath,
    OrchestrationModel? Orchestration,
    IReadOnlyDictionary<string, RepoModel> Repos,
    IReadOnlyList<NeedsTrust> Untrusted,
    IReadOnlyList<PlanProblem> Problems,
    IReadOnlyList<DefinitionResult> Results,
    IReadOnlyDictionary<string, string> RepoDirs,
    IReadOnlyDictionary<string, string>? WorkspaceDirs = null)
{
    /// <summary>What its workspaces are keyed by: the registry's ID for the file, never the name in its code.</summary>
    public string Id { get; init; } = OrchestrationRegistry.IdFromFile(OrchestrationPath);

    public bool Complete => Orchestration is not null && Untrusted.Count == 0 && Problems.Count == 0;

    /// <summary>A repo from the Repos list run on its own, rather than an orchestration file.</summary>
    public bool Alone { get; init; }

    /// <summary>Where the repos' definitions were read from, so where their other files belong too.</summary>
    public IReadOnlyDictionary<string, string> ReadFrom => WorkspaceDirs ?? RepoDirs;

    /// <summary>What's in each repo's compose files, read from <paramref name="dirs"/> (<see cref="ReadFrom"/> when
    /// null). Files that are missing or unreadable are reported in <paramref name="problems"/>.</summary>
    public IReadOnlyDictionary<string, ComposeFileInfo> ComposeFiles(List<PlanProblem> problems, IReadOnlyDictionary<string, string>? dirs = null)
    {
        var result = new Dictionary<string, ComposeFileInfo>(StringComparer.Ordinal);
        foreach (var (repoPath, repo) in Repos)
        {
            foreach (var file in repo.ComposeFiles)
            {
                var path = Path.Combine((dirs ?? ReadFrom)[repoPath], file.File);
                try
                {
                    result[ComposeFileInfo.Key(repoPath, file.File)] = Compose.ComposeYaml.Inspect(File.ReadAllText(path));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or Compose.ComposeException)
                {
                    var why = e is FileNotFoundException or DirectoryNotFoundException ? "doesn't exist" : e.Message;
                    problems.Add(new PlanProblem(ProblemSeverity.Error, $"{repo.Name}'s {file.File} {why}", file.Location));
                }
            }
        }
        return result;
    }
}

/// <summary>Evaluates an orchestration and the <c>sprig.cs</c> of every repo it names. Repos are evaluated in parallel; each
/// goes through the same directive, trust and cache checks as any definition.</summary>
/// <param name="registry">Gives each orchestration its registered ID; without one, the ID comes from the file name.</param>
/// <param name="stored">The Repos list, for repos an orchestration names with <c>UseRepo</c>.</param>
public sealed class OrchestrationLoader(DefinitionRunner runner, OrchestrationRegistry? registry = null, StoredDefinitions? stored = null)
{
    public const string RepoDefinitionFile = "sprig.cs";

    /// <param name="workspaceDirs">A workspace's folder for each repo, keyed by the orchestration's repo path. Its definitions are
    /// read from there, so a branch's changes count, and approved as the repo's own.</param>
    /// <param name="orchestrationPath">An orchestration file, or a Repos entry's file for that repo run on its own.</param>
    public LoadedOrchestration Load(string orchestrationPath, IReadOnlyDictionary<string, string>? workspaceDirs = null)
    {
        var path = LongPath.Of(orchestrationPath);
        var alone = stored?.RepoIdOf(path);
        var id = alone ?? registry?.IdFor(path) ?? OrchestrationRegistry.IdFromFile(path);
        var problems = new List<PlanProblem>();
        var results = new List<DefinitionResult>();
        var untrusted = new List<NeedsTrust>();
        var repos = new Dictionary<string, RepoModel>(StringComparer.Ordinal);
        var dirs = new Dictionary<string, string>(StringComparer.Ordinal);

        OrchestrationModel? orchestration;
        if (alone is not null) orchestration = Alone(alone, path);
        else
        {
            var orchestrationResult = runner.Evaluate(path);
            results.Add(orchestrationResult);
            orchestration = Accept(orchestrationResult, problems, untrusted) switch
            {
                OrchestrationModel t => t,
                RepoModel => Fail($"{Path.GetFileName(path)} describes a repo, not an orchestration (it calls Repo.Describe)", path, problems),
                _ => null,
            };
        }
        if (orchestration is null) return new LoadedOrchestration(path, null, repos, untrusted, problems, results, dirs) { Id = id, Alone = alone is not null };

        var dir = Path.GetDirectoryName(path)!;
        var definitions = new List<(RepoRefModel reference, string file, string trustAs)>();
        foreach (var reference in orchestration.Repos)
        {
            if (reference.Use is { } use)
            {
                UseEntry(reference, use);
                continue;
            }
            var repoDir = LongPath.Of(Path.Combine(dir, reference.Path));
            dirs[reference.Path] = repoDir;
            var readDir = workspaceDirs?.GetValueOrDefault(reference.Path) is { } workspaceDir ? LongPath.Of(workspaceDir) : repoDir;
            var file = Path.Combine(readDir, RepoDefinitionFile);
            if (!Directory.Exists(readDir))
                problems.Add(new PlanProblem(ProblemSeverity.Error, $"the repo folder '{readDir}' doesn't exist", reference.Location));
            else if (!File.Exists(file))
                problems.Add(new PlanProblem(ProblemSeverity.Error, readDir == repoDir
                    ? $"'{reference.Path}' has no {RepoDefinitionFile} to describe it; commit one, or add the folder on the Repos page and use it with orchestration.UseRepo(\"id\")"
                    : $"'{reference.Path}' has no {RepoDefinitionFile} in the workspace's folder {readDir}", reference.Location));
            else
                definitions.Add((reference, file, Path.Combine(repoDir, RepoDefinitionFile)));
        }

        // A repo from the Repos list: its folder and definition come from its entry, not the orchestration.
        void UseEntry(RepoRefModel reference, string use)
        {
            if (stored?.Repo(use) is not { } entry)
            {
                problems.Add(new PlanProblem(ProblemSeverity.Error, $"'{use}' isn't in sprig's Repos list", reference.Location));
                return;
            }
            var folder = entry.Folder;
            dirs[reference.Path] = folder;
            var readDir = workspaceDirs?.GetValueOrDefault(reference.Path) is { } workspaceDir ? LongPath.Of(workspaceDir) : folder;
            if (!Directory.Exists(readDir))
                problems.Add(new PlanProblem(ProblemSeverity.Error, $"the repo folder '{readDir}' doesn't exist", reference.Location));
            else if (!entry.InRepo)
                definitions.Add((reference, entry.DefinitionFile, entry.DefinitionFile));
            else if (Path.Combine(readDir, RepoDefinitionFile) is var file && File.Exists(file))
                definitions.Add((reference, file, entry.DefinitionFile));
            else
                problems.Add(new PlanProblem(ProblemSeverity.Error, $"'{use}' uses {readDir}'s own {RepoDefinitionFile}, which isn't there", reference.Location));
        }

        var evaluated = definitions.AsParallel().AsOrdered()
            .Select(d => (d.reference, result: runner.Evaluate(d.file, d.trustAs)))
            .ToList();
        foreach (var (reference, result) in evaluated)
        {
            results.Add(result);
            switch (Accept(result, problems, untrusted))
            {
                case RepoModel repo: repos[reference.Path] = repo; break;
                case OrchestrationModel: problems.Add(new PlanProblem(ProblemSeverity.Error, $"{(reference.Use is { } use ? $"the repo '{use}'" : $"'{reference.Path}/{RepoDefinitionFile}'")}'s definition describes an orchestration, not a repo", reference.Location)); break;
            }
        }

        IReadOnlyDictionary<string, string>? readFrom = workspaceDirs is null ? null
            : orchestration.Repos.ToDictionary(r => r.Path, r => workspaceDirs.GetValueOrDefault(r.Path) is { } d ? LongPath.Of(d) : dirs.GetValueOrDefault(r.Path) ?? r.Path, StringComparer.Ordinal);
        return new LoadedOrchestration(path, orchestration, repos, untrusted, problems, results, dirs, readFrom) { Id = id, Alone = alone is not null };
    }

    /// <summary>An orchestration of one repo from the Repos list, with nothing of its own, so the repo can run without an
    /// orchestration file. Its name differs from the repo's, so the orchestration and the repo are never mistaken for each other.</summary>
    static OrchestrationModel Alone(string repoId, string entryFile)
    {
        var at = new SourceLocation(entryFile, 0);
        return new OrchestrationModel(Sdk.Schema, $"{repoId}, on its own", Sdk.Version, at, Containers: [],
            Repos: [new RepoRefModel(repoId, at, Use: repoId)], Bindings: [], Groups: []);
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

    static OrchestrationModel? Fail(string message, string path, List<PlanProblem> problems)
    {
        problems.Add(new PlanProblem(ProblemSeverity.Error, message, new SourceLocation(path, 0)));
        return null;
    }
}
