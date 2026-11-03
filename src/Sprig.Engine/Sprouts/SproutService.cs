using System.Text.RegularExpressions;
using Sprig.Engine.Compose;
using Sprig.Engine.Git;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Engine.Store;
using Sprig.Engine.Trellises;
using Sprig.Model;

namespace Sprig.Engine.Sprouts;

public enum StepState { Running, Done, Skipped, Failed, Output }

/// <summary>Progress from <see cref="SproutService"/>: a step starting or ending, or a line of a tool's output.</summary>
public sealed record SproutEvent(string Step, StepState State, string? Detail = null);

/// <summary>A sprout couldn't come up. <see cref="Problems"/> holds the plan's problems when that's why.</summary>
public sealed class SproutException(string message, IReadOnlyList<PlanProblem>? problems = null) : Exception(message)
{
    public IReadOnlyList<PlanProblem> Problems { get; } = problems ?? [];
}

public sealed record RemoveOptions(bool DeleteBranch = false, bool Force = false);

/// <summary>
/// Brings sprouts up, down and away. <c>up</c> is one step: worktrees (or the repos in place), leased ports, env files,
/// a compose project per infra source, started and healthy. Each sprout's record says exactly what it did, so
/// <c>down</c> and <c>rm</c> undo that and nothing else.
/// </summary>
public sealed partial class SproutService(
    SproutStore store,
    PortLeases leases,
    ISettingsStore settings,
    GitClient git,
    DockerCompose docker,
    Func<IReadOnlySet<int>>? listening = null)
{
    readonly Func<IReadOnlySet<int>> _listening = listening ?? PortAllocator.Listening;

    public static string? CheckName(string sprout)
        => NameRule().IsMatch(sprout) ? null
            : $"'{sprout}' can't name a sprout: use letters, digits, '.', '_' and '-', starting with a letter or digit";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$")]
    private static partial Regex NameRule();

    /// <summary>A sprout name from a branch name: <c>feat/x</c> becomes <c>feat-x</c>.</summary>
    public static string NameFromBranch(string branch) => Regex.Replace(branch, "[^A-Za-z0-9._-]+", "-").Trim('-', '.', '_');

    public IReadOnlyList<SproutRecord> List() => store.List();

    public SproutRecord? Get(string trellis, string sprout) => store.Get(trellis, sprout);

    public Plan? LastPlan(string trellis, string sprout) => store.GetPlan(trellis, sprout);

    /// <param name="branch">The branch for the sprout's worktrees; defaults to the sprout's name. Must be null when the
    /// trellis runs its repos in place.</param>
    public SproutRecord Up(LoadedTrellis loaded, string sprout, string? branch, IProgress<SproutEvent>? progress = null,
        CancellationToken cancellation = default)
    {
        if (!loaded.Complete || loaded.Trellis is not { } trellis)
            throw new SproutException($"{Path.GetFileName(loaded.TrellisPath)} isn't ready to sprout", loaded.Problems);
        if (CheckName(sprout) is { } bad) throw new SproutException(bad);

        var existing = store.Get(trellis.Name, sprout);
        if (existing is not null && !SamePath(existing.TrellisPath, loaded.TrellisPath))
            throw new SproutException($"sprout '{sprout}' already belongs to the trellis at {existing.TrellisPath}");

        var inPlace = trellis.Worktrees is null;
        branch = inPlace ? null : branch ?? existing?.Branch ?? sprout;
        if (inPlace && branch is not null) throw new SproutException($"{trellis.Name} runs its repos in place, so a sprout can't have its own branch");
        if (branch is not null && existing?.Branch is { } had && had != branch)
            throw new SproutException($"sprout '{sprout}' is on branch '{had}'; remove it first to change branch");
        if (branch is not null && !git.IsValidBranchName(branch)) throw new SproutException($"'{branch}' isn't a valid branch name");

        var settingsNow = settings.Get();
        var step = new Steps(progress);

        // A dry run first, so a broken trellis fails before anything is created.
        step.Start("Plan");
        var problems = new List<PlanProblem>();
        var dryPlan = Planner.Build(new PlanInput(trellis, loaded.Repos, sprout, settingsNow, _listening(),
            ComposeFiles: loaded.ComposeFiles(problems)));
        problems.AddRange(dryPlan.Problems);
        if (!dryPlan.HasErrors) problems.AddRange(CheckFiles(dryPlan, loaded.RepoDirs));
        if (problems.Any(p => p.Severity == ProblemSeverity.Error))
            throw step.Fail(new SproutException("the plan has errors", problems));
        var needsDocker = dryPlan.Containers.Count > 0 || dryPlan.Compose.Count > 0;
        if (needsDocker && docker.Unavailable() is { } why) throw step.Fail(new SproutException(why));
        step.Done($"{dryPlan.Apps.Count} apps, {dryPlan.Containers.Count} containers, {dryPlan.Compose.Count} compose files");

        if (inPlace) CheckNoOtherInPlace(trellis.Name, sprout, loaded.RepoDirs.Values);

        var now = DateTimeOffset.UtcNow;
        var record = existing ?? new SproutRecord(trellis.Name, loaded.TrellisPath, sprout, branch, trellis.PortPolicy,
            [], [], [], SproutState.Starting, now, now);
        record = record with
        {
            PortPolicy = trellis.PortPolicy,
            State = SproutState.Starting,
            UpdatedAt = now,
            Error = null,
            Folder = inPlace ? null : record.Folder ?? SproutFolder(loaded, trellis.Name, sprout, settingsNow),
        };

        try
        {
            record = record with { Repos = Repos(loaded, record, settingsNow, step, cancellation) };
            store.Save(record);

            var dirs = record.Repos.ToDictionary(r => r.RepoPath, r => r.Dir, StringComparer.Ordinal);
            step.Start("Ports");
            var plan = LeasePorts(loaded, record, existing, dirs, settingsNow);
            step.Done(string.Join(", ", plan.Ports.Select(p => $"{p.Resource}.{p.Endpoint} {p.Port}")));

            step.Start("Files");
            record = record with { Files = WriteFiles(plan, record) };
            store.Save(record);
            store.SavePlan(plan);
            step.Done($"{record.Files.Count} files");

            record = record with { Compose = StartInfra(plan, record, step, cancellation) };
            record = record with { State = SproutState.Up, UpdatedAt = DateTimeOffset.UtcNow };
            store.Save(record);
            return record;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            step.FailCurrent(e.Message);
            store.Save(record with { State = SproutState.Failed, Error = e.Message, UpdatedAt = DateTimeOffset.UtcNow });
            throw e as SproutException ?? new SproutException(e.Message);
        }
    }

    /// <summary>Stop the sprout's containers, keeping their volumes. Under the random port policy its ports go back
    /// too.</summary>
    public SproutRecord Down(string trellis, string sprout, IProgress<SproutEvent>? progress = null, CancellationToken cancellation = default)
    {
        var record = store.Get(trellis, sprout) ?? throw new SproutException($"{trellis} has no sprout named '{sprout}'");
        var step = new Steps(progress);
        foreach (var project in record.Compose)
        {
            step.Start($"Stop {project.Name}");
            docker.Down(project, volumes: false, line => step.Output(line), cancellation);
            step.Done();
        }
        if (record.PortPolicy == "random") leases.Release(record.Key);
        record = record with { State = SproutState.Down, UpdatedAt = DateTimeOffset.UtcNow };
        store.Save(record);
        return record;
    }

    /// <summary>
    /// Remove the sprout: its containers and volumes, the values it wrote, its worktrees (refusing ones with changes
    /// unless forced) and, when asked, its branches (refusing ones with unpushed commits unless forced).
    /// </summary>
    public void Remove(string trellis, string sprout, RemoveOptions options, IProgress<SproutEvent>? progress = null,
        CancellationToken cancellation = default)
    {
        var record = store.Get(trellis, sprout) ?? throw new SproutException($"{trellis} has no sprout named '{sprout}'");
        var step = new Steps(progress);

        if (!options.Force)
        {
            var dirty = record.Repos.Where(r => r.Worktree && Directory.Exists(r.Dir) && git.HasUncommittedChanges(r.Dir)).ToArray();
            if (dirty.Length > 0)
                throw new SproutException("these worktrees have changes that aren't committed; commit or discard them, or force it:\n  "
                    + string.Join("\n  ", dirty.Select(r => r.Dir)));
            if (options.DeleteBranch && record.Branch is { } b)
            {
                var unpushed = record.Repos.Where(r => r.Worktree && git.BranchExists(r.Source, b) && git.CommitsOnlyOn(r.Source, b) > 0).ToArray();
                if (unpushed.Length > 0)
                    throw new SproutException($"'{b}' has commits no other branch or remote has, in " + string.Join(", ", unpushed.Select(r => r.Name))
                        + "; push them, keep the branch, or force it");
            }
        }

        foreach (var project in record.Compose)
        {
            step.Start($"Remove {project.Name}");
            docker.Down(project, volumes: true, line => step.Output(line), cancellation);
            step.Done();
        }

        step.Start("Files");
        foreach (var file in record.Files.Where(f => !InWorktree(record, f.Path)))
            EnvFiles.Remove(file);
        step.Done();

        foreach (var repo in record.Repos.Where(r => r.Worktree))
        {
            step.Start($"Worktree {repo.Name}");
            if (Directory.Exists(repo.Dir)) git.RemoveWorktree(repo.Source, repo.Dir, force: true);
            if (options.DeleteBranch && record.Branch is { } branch && git.BranchExists(repo.Source, branch))
                git.DeleteBranch(repo.Source, branch, force: true);
            step.Done(options.DeleteBranch ? "removed, with its branch" : "removed");
        }
        DeleteIfEmpty(record.Folder);

        leases.Release(record.Key);
        store.Delete(trellis, sprout);
    }

    // ---- up, step by step ----

    IReadOnlyList<SproutRepo> Repos(LoadedTrellis loaded, SproutRecord record, SprigSettings settingsNow, Steps step, CancellationToken cancellation)
    {
        var trellis = loaded.Trellis!;
        var repos = new List<SproutRepo>();
        if (record.Branch is not { } branch)
        {
            foreach (var reference in trellis.Repos)
                repos.Add(new SproutRepo(reference.Path, loaded.Repos[reference.Path].Name, loaded.RepoDirs[reference.Path],
                    loaded.RepoDirs[reference.Path], Worktree: false, BranchCreated: false));
            return repos;
        }

        var root = record.Folder ?? throw new InvalidOperationException("a worktree sprout has a folder");
        var common = CommonParent(loaded.RepoDirs.Values);
        foreach (var reference in trellis.Repos)
        {
            cancellation.ThrowIfCancellationRequested();
            var source = loaded.RepoDirs[reference.Path];
            var name = loaded.Repos[reference.Path].Name;
            var dir = LongPath.Of(Path.Combine(root, Path.GetRelativePath(common, source)));
            step.Start($"Worktree {name}");

            var before = record.Repos.FirstOrDefault(r => r.RepoPath == reference.Path);
            if (Directory.Exists(dir))
            {
                if (!git.Worktrees(source).Any(w => SamePath(w, dir)))
                    throw new SproutException($"{dir} already exists and isn't a worktree of {source}");
                repos.Add(new SproutRepo(reference.Path, name, source, dir, Worktree: true, before?.BranchCreated ?? false));
                step.Done($"{dir} (already there)");
                continue;
            }
            if (!git.IsRepo(source)) throw new SproutException($"{source} isn't a git repo, so it can't have a worktree");
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            var created = git.AddWorktree(source, dir, branch, trellis.Worktrees!.From);
            repos.Add(new SproutRepo(reference.Path, name, source, dir, Worktree: true, created || (before?.BranchCreated ?? false)));
            step.Done($"{dir} on {branch}{(created ? $" (new, from {trellis.Worktrees.From})" : "")}");
        }
        return repos;
    }

    Plan LeasePorts(LoadedTrellis loaded, SproutRecord record, SproutRecord? existing, IReadOnlyDictionary<string, string> dirs, SprigSettings settingsNow)
    {
        var problems = new List<PlanProblem>();
        var compose = loaded.ComposeFiles(problems, dirs);
        if (problems.Count > 0) throw new SproutException("the sprout's compose files couldn't be read", problems);

        // Anything but a clean down may have left its containers listening on the leased ports.
        var wasUp = existing is not null && existing.State != SproutState.Down;
        var keep = record.PortPolicy != "random" || wasUp;
        var listening = _listening();

        var plan = leases.Update(record.Key, (mine, others) =>
        {
            // A running sprout listens on its own ports; they mustn't count against it.
            var inUse = listening.Where(p => !(wasUp && mine.Values.Contains(p))).Concat(others).ToHashSet();
            var built = Planner.Build(new PlanInput(loaded.Trellis!, loaded.Repos, record.Name, settingsNow, inUse,
                Leased: keep ? mine : null, ComposeFiles: compose, Random: record.PortPolicy == "random" ? Random.Shared : null));
            if (built.HasErrors) return (built, null);
            return (built, built.Ports.Where(p => p.Port is not null).ToDictionary(p => p.Key, p => p.Port!.Value));
        });
        if (plan.HasErrors) throw new SproutException("the plan has errors", plan.Problems);
        return plan;
    }

    /// <summary>A file a sprout writes values into, and the definition line that asked for it.</summary>
    sealed record FileTarget(string RepoPath, string RepoDir, string Path, string Kind, string? Profile,
        List<KeyValuePair<string, string>> Values, SourceLocation Location);

    static List<FileTarget> Targets(Plan plan, IReadOnlyDictionary<string, string> repoDirs)
    {
        var targets = new List<FileTarget>();
        foreach (var app in plan.Apps)
        {
            var repoDir = repoDirs[app.RepoPath];
            var appDir = Path.GetFullPath(Path.Combine(repoDir, app.Path));
            var process = app.Env.Where(e => e.File is null).Select(e => KeyValuePair.Create(e.Key, e.Value.Value ?? "")).ToList();

            foreach (var group in app.Env.Where(e => e.File is not null).GroupBy(e => e.File!))
                Add(app.RepoPath, repoDir, Path.GetFullPath(Path.Combine(appDir, group.Key)), "dotenv", null,
                    group.Select(e => KeyValuePair.Create(e.Key, e.Value.Value ?? "")), group.First().Location);
            foreach (var output in app.Outputs)
                Add(app.RepoPath, repoDir, Path.GetFullPath(Path.Combine(appDir, output.File)), output.Kind, output.Profile, process, output.Location);
        }
        return targets;

        void Add(string repoPath, string repoDir, string path, string kind, string? profile, IEnumerable<KeyValuePair<string, string>> values, SourceLocation at)
        {
            var same = targets.FindIndex(t => SamePath(t.Path, path));
            if (same < 0) targets.Add(new FileTarget(repoPath, repoDir, path, kind, profile, values.ToList(), at));
            else if (targets[same].Kind != kind) throw new SproutException($"{path} is written as both {targets[same].Kind} and {kind}");
            else targets[same].Values.AddRange(values);
        }
    }

    /// <summary>
    /// Every file the plan would write values into must be one the repo already keeps out of git: not tracked, and
    /// covered by its ignore rules. That's the repo saying the file is for local values, so sprig never has to hide
    /// anything from git itself. <paramref name="repoDirs"/> are the folders the sprout would use, by repo path.
    /// </summary>
    public IReadOnlyList<PlanProblem> CheckFiles(Plan plan, IReadOnlyDictionary<string, string> repoDirs)
    {
        var problems = new List<PlanProblem>();
        foreach (var target in Targets(plan, repoDirs))
        {
            // Without git there's nothing to commit by accident.
            if (!git.IsRepo(target.RepoDir)) continue;
            var relative = Path.GetRelativePath(target.RepoDir, target.Path).Replace('\\', '/');
            if (git.IsTracked(target.RepoDir, target.Path))
                problems.Add(new PlanProblem(ProblemSeverity.Error,
                    $"git tracks {relative}, so sprig won't write a sprout's values into it. Point the output at a local file the app reads instead" +
                    (target.Kind == "launchSettings" ? ", e.g. EnvOutput.AppSettings(\"appsettings.Local.json\")." : "."), target.Location));
            else if (!git.IsIgnored(target.RepoDir, target.Path))
                problems.Add(new PlanProblem(ProblemSeverity.Error,
                    $"{relative} isn't ignored by git, so a sprout's values could be committed. Add it to the repo's .gitignore" + target.Kind switch
                    {
                        "appSettings" => $", and have the app load it: builder.Configuration.AddJsonFile(\"{Path.GetFileName(target.Path)}\", optional: true).",
                        _ => ".",
                    }, target.Location));
        }
        return problems;
    }

    IReadOnlyList<WrittenFile> WriteFiles(Plan plan, SproutRecord record)
    {
        var dirs = record.Repos.ToDictionary(r => r.RepoPath, r => r.Dir, StringComparer.Ordinal);
        // Checked again in the sprout's own folders (a branch can have its own .gitignore), and all before any write,
        // so a refusal leaves nothing half done.
        var problems = CheckFiles(plan, dirs);
        if (problems.Count > 0) throw new SproutException("sprig won't write into these files", problems);

        var written = new List<WrittenFile>();
        foreach (var target in Targets(plan, dirs))
        {
            var repo = record.Repos.Single(r => r.RepoPath == target.RepoPath);
            var path = target.Path;
            var created = record.Files.FirstOrDefault(f => SamePath(f.Path, path))?.Created ?? !File.Exists(path);
            var values = Dedupe(target.Values);
            switch (target.Kind)
            {
                case "dotenv":
                    var seed = repo.Worktree ? Path.Combine(repo.Source, Path.GetRelativePath(repo.Dir, path)) : null;
                    EnvFiles.WriteDotEnv(path, values, seed);
                    written.Add(new WrittenFile(path, target.Kind, created));
                    break;
                case "launchSettings":
                    EnvFiles.WriteLaunchProfile(path, target.Profile ?? "sprig", values);
                    written.Add(new WrittenFile(path, target.Kind, created, target.Profile ?? "sprig"));
                    break;
                case "appSettings":
                    written.Add(new WrittenFile(path, target.Kind, created, Keys: EnvFiles.WriteAppSettings(path, values)));
                    break;
            }
        }

        // Undo what an earlier up wrote that this one no longer does.
        foreach (var gone in record.Files.Where(f => !written.Any(w => SamePath(w.Path, f.Path))))
            EnvFiles.Remove(gone);
        return written;
    }

    IReadOnlyList<ComposeProject> StartInfra(Plan plan, SproutRecord record, Steps step, CancellationToken cancellation)
    {
        var folder = store.Folder(record.Trellis, record.Name);
        var projects = new List<ComposeProject>();
        var checkedBy = new Dictionary<string, string[]>();

        if (plan.Containers.Count > 0)
        {
            var file = Path.Combine(folder, "containers.compose.yml");
            File.WriteAllText(file, ComposeYaml.ForContainers(plan.Containers));
            var project = new ComposeProject(ComposeYaml.ProjectName("sprig", record.Trellis, record.Name), file, folder);
            projects.Add(project);
            checkedBy[project.Name] = plan.Containers.Where(c => c.Health is not null).Select(c => c.Name).ToArray();
        }
        foreach (var compose in plan.Compose)
        {
            var repo = record.Repos.Single(r => r.RepoPath == compose.RepoPath);
            var source = Path.Combine(repo.Dir, compose.File);
            var file = Path.Combine(folder, $"{ComposeYaml.ProjectName(repo.Name, Path.GetFileNameWithoutExtension(compose.File))}.compose.yml");
            File.WriteAllText(file, ComposeYaml.ForSprout(File.ReadAllText(source), compose));
            var project = new ComposeProject(ComposeYaml.ProjectName("sprig", record.Trellis, record.Name, repo.Name), file,
                Path.GetDirectoryName(Path.GetFullPath(source))!);
            projects.Add(project);
            checkedBy[project.Name] = compose.Services.Where(s => s.Health is not null).Select(s => s.Name).ToArray();
        }

        // Projects an earlier up started that this one no longer has.
        foreach (var gone in record.Compose.Where(c => projects.All(p => p.Name != c.Name)))
            docker.Down(gone, volumes: false, line => step.Output(line), cancellation);

        foreach (var project in projects)
        {
            step.Start($"Start {project.Name}");
            docker.Up(project, line => step.Output(line), cancellation);
            var healthy = checkedBy[project.Name];
            step.Done(healthy.Length > 0 ? $"running; healthy: {string.Join(", ", healthy)}" : "running");
        }
        return projects;
    }

    // ---- helpers ----

    void CheckNoOtherInPlace(string trellis, string sprout, IEnumerable<string> dirs)
    {
        var mine = dirs.ToArray();
        foreach (var other in store.List().Where(r => !(r.Trellis == trellis && r.Name == sprout)))
        {
            var clash = other.Repos.Where(r => !r.Worktree).FirstOrDefault(r => mine.Any(d => SamePath(d, r.Dir)));
            if (clash is not null)
                throw new SproutException($"{clash.Dir} already runs in place for sprout '{other.Name}' of {other.Trellis}; remove that sprout first");
        }
    }

    static string SproutFolder(LoadedTrellis loaded, string trellis, string sprout, SprigSettings settingsNow)
    {
        var parent = settingsNow.SproutsFolder is { Length: > 0 } configured ? configured : CommonParent(loaded.RepoDirs.Values);
        return LongPath.Of(Path.Combine(parent, $"{trellis}--{sprout}"));
    }

    /// <summary>The deepest folder holding every repo; the sprout's folder mirrors the repos' layout under it.</summary>
    internal static string CommonParent(IEnumerable<string> dirs)
    {
        var parents = dirs.Select(d => Path.GetDirectoryName(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar))!).ToArray();
        if (parents.Length == 0) throw new SproutException("the trellis names no repos");
        var common = parents[0];
        while (!parents.All(p => IsUnder(p, common)))
            common = Path.GetDirectoryName(common) ?? throw new SproutException("the trellis's repos share no folder");
        return common;
    }

    static bool IsUnder(string path, string folder)
        => SamePath(path, folder) || path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison);

    static bool InWorktree(SproutRecord record, string path)
        => record.Repos.Any(r => r.Worktree && IsUnder(path, r.Dir));

    static void DeleteIfEmpty(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return;
        try
        {
            foreach (var child in Directory.EnumerateDirectories(dir)) DeleteIfEmpty(child);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (IOException) { /* something else is in there; leave it */ }
    }

    /// <summary>Last value wins, as it would for a process env var set twice.</summary>
    static List<KeyValuePair<string, string>> Dedupe(IEnumerable<KeyValuePair<string, string>> values)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var kv in values)
        {
            result.RemoveAll(r => r.Key == kv.Key);
            result.Add(kv);
        }
        return result;
    }

    static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), Comparison);

    /// <summary>Reports steps to the caller, remembering the one in progress so a failure can be pinned on it.</summary>
    sealed class Steps(IProgress<SproutEvent>? progress)
    {
        string? _current;

        public void Start(string name)
        {
            _current = name;
            progress?.Report(new SproutEvent(name, StepState.Running));
        }

        public void Done(string? detail = null)
        {
            if (_current is null) return;
            progress?.Report(new SproutEvent(_current, StepState.Done, detail));
            _current = null;
        }

        public void Output(string line) => progress?.Report(new SproutEvent(_current ?? "", StepState.Output, line));

        public void FailCurrent(string message)
        {
            if (_current is null) return;
            progress?.Report(new SproutEvent(_current, StepState.Failed, message));
            _current = null;
        }

        public SproutException Fail(SproutException e)
        {
            FailCurrent(e.Message);
            return e;
        }
    }
}
