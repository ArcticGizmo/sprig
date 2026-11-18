using Sprig.Engine.Compose;
using Sprig.Engine.Definitions;
using Sprig.Engine.Hosting;
using Sprig.Engine.Orchestrations;
using Sprig.Engine.Planning;
using Sprig.Engine.Processes;

namespace Sprig.Engine.Workspaces;

/// <summary>A container of a workspace, as docker knows it.</summary>
/// <param name="Key"><c>owner/name</c>, as the plan names the container or compose service.</param>
public sealed record InfraTarget(string Key, ComposeProject Project, string Service);

/// <summary>A container or compose service a main checkout uses, which the user runs themselves.</summary>
/// <param name="Ports">Where it should be listening, as the plan has them.</param>
/// <param name="Answering">Something listens on every one of its ports; null when it has none, so there's no telling.</param>
public sealed record OwnInfra(string Key, IReadOnlyList<int> Ports, bool? Answering);

public sealed partial class WorkspaceService
{
    /// <summary>The compose project for the containers a workspace's definitions declare in code.</summary>
    internal static string ContainersProject(WorkspaceRecord record) => ComposeYaml.ProjectName("sprig", record.Orchestration, record.Name);

    /// <summary>The compose project for one repo's own compose file, in a workspace.</summary>
    internal static string RepoProject(WorkspaceRecord record, string repo) => ComposeYaml.ProjectName("sprig", record.Orchestration, record.Name, repo);

    /// <summary>Where each of the plan's containers and compose services runs, under the names <c>up</c> gave them. None
    /// for a main checkout, whose infra the user runs (see <see cref="OwnInfraOf"/>).</summary>
    public IReadOnlyList<InfraTarget> InfraTargets(WorkspaceRecord record, Plan plan)
    {
        if (record.IsMain) return [];
        var folder = store.Folder(record.Orchestration, record.Name);
        ComposeProject Project(string name, string file, string dir) => record.Compose.FirstOrDefault(p => p.Name == name) ?? new ComposeProject(name, file, dir);

        var targets = new List<InfraTarget>();
        if (plan.Containers.Count > 0)
        {
            var project = Project(ContainersProject(record), Path.Combine(folder, "containers.compose.yml"), folder);
            targets.AddRange(plan.Containers.Select(c => new InfraTarget($"{c.Owner}/{c.Name}", project, ComposeYaml.ServiceName(c.Owner, c.Name))));
        }
        foreach (var compose in plan.Compose)
        {
            if (record.Repos.FirstOrDefault(r => r.RepoPath == compose.RepoPath) is not { } repo) continue;
            var project = Project(RepoProject(record, repo.Name), "", repo.Dir);
            targets.AddRange(compose.Services.Select(s => new InfraTarget($"{compose.Owner}/{s.Name}", project, s.Name)));
        }
        return targets;
    }

    /// <summary>The compose projects a workspace's containers run under. None for a main checkout, whose infra is the
    /// user's and runs under names sprig doesn't know.</summary>
    public static IReadOnlyList<string> InfraProjects(WorkspaceRecord record)
        => record.IsMain ? []
            : record.Compose.Select(p => p.Name).Append(ContainersProject(record)).Concat(record.Repos.Select(r => RepoProject(record, r.Name)))
                .Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Whether what the workspace was last made ready from reads differently now (see <see cref="WorkspaceRecord.ReadyFrom"/>).
    /// Starting everything makes it ready again first when it does, so what starts is what's defined now. Content is
    /// compared, not file times, so saving a file unchanged, or a checkout touching it, doesn't count. One never made
    /// ready, or made ready before the files were kept, has changed.
    /// </summary>
    public static bool DefinitionsChanged(WorkspaceRecord record)
        => record.ReadyFrom is not { Count: > 0 } files || files.Any(f => ContentHash(f.Key) != f.Value);

    /// <summary>Every file <paramref name="loaded"/> was read from, with its content's hash: each definition it ran,
    /// wherever it's kept (a repo's folder, a worktree, sprig's own store), and the compose files they name.</summary>
    internal static IReadOnlyDictionary<string, string> ReadyFromFiles(LoadedOrchestration loaded)
    {
        var files = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var result in loaded.Results) files[result.Path] = result.Sha256;
        if (!files.ContainsKey(loaded.OrchestrationPath) && ContentHash(loaded.OrchestrationPath) is { } own) files[loaded.OrchestrationPath] = own;
        foreach (var (repoPath, repo) in loaded.Repos)
            foreach (var compose in repo.ComposeFiles)
                if (loaded.ReadFrom.GetValueOrDefault(repoPath) is { } dir && Path.Combine(dir, compose.File) is var path && ContentHash(path) is { } hash)
                    files[path] = hash;
        return files;
    }

    /// <summary>As definitions are hashed for approval, so a CRLF checkout of the same text is the same; null for a file
    /// that's gone or can't be read.</summary>
    static string? ContentHash(string file)
    {
        try { return DefinitionHash.Of(File.ReadAllText(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Every compose project with a container running; null when docker doesn't answer.</summary>
    public IReadOnlySet<string>? RunningProjects()
    {
        try { return docker.RunningProjects(); }
        catch (ProcessException) { return null; }
    }

    /// <summary>
    /// How a workspace is doing, in a word: <c>running</c> while any of its apps or containers is, else <c>stopped</c>;
    /// or <c>starting</c>, <c>failed</c> or <c>free</c> from its record. <c>paused</c> while it has a pause and none of
    /// its apps is going, even with its containers up, as they are after quitting sprig. Whether it's been made ready
    /// isn't shown: Start does that when it has to.
    /// </summary>
    /// <param name="runningProjects">From <see cref="RunningProjects"/>; null when docker can't say, so only apps count.</param>
    public static string RunState(WorkspaceRecord record, IEnumerable<Hosting.RunnerStatus> runners, IReadOnlySet<string>? runningProjects)
    {
        if (record.IsFree) return "free";
        if (record.State == WorkspaceState.Starting) return "starting";
        var appsGoing = runners.Any(r => r.Workspace == record.Key && r.State.IsAlive());
        if (record.Paused is not null && !appsGoing) return "paused";
        var running = appsGoing || (runningProjects is not null && InfraProjects(record).Any(runningProjects.Contains));
        if (running) return "running";
        return record.State == WorkspaceState.Failed ? "failed" : "stopped";
    }

    /// <summary>What each running container of <paramref name="projects"/> uses, by project and service. Null when docker
    /// doesn't answer.</summary>
    public IReadOnlyDictionary<(string Project, string Service), Hosting.ResourceUsage>? InfraUsage(IReadOnlySet<string> projects)
    {
        try { return docker.Usage(projects); }
        catch (ProcessException) { return null; }
    }

    /// <summary>How each container is doing, by <see cref="InfraTarget.Key"/>; a container docker doesn't have (the
    /// workspace is down) is left out. Null when docker doesn't answer.</summary>
    public IReadOnlyDictionary<string, ComposeService>? InfraStates(IReadOnlyList<InfraTarget> targets)
    {
        var states = new Dictionary<string, ComposeService>(StringComparer.Ordinal);
        try
        {
            foreach (var project in targets.GroupBy(t => t.Project.Name))
            {
                var services = docker.Services(project.Key);
                foreach (var target in project)
                    if (services.FirstOrDefault(s => s.Service == target.Service) is { } service) states[target.Key] = service;
            }
        }
        catch (ProcessException)
        {
            return null;
        }
        return states;
    }

    /// <summary>
    /// The containers and compose services <paramref name="apps"/> use, by <see cref="InfraTarget.Key"/>: what they, and
    /// the apps and jobs they depend on, take values from or wait for, and what that infra uses in turn. A value from
    /// another app doesn't count, since that app isn't started for it. Null when <paramref name="apps"/> is: every one.
    /// </summary>
    /// <param name="apps">By key (<c>repo/app</c>) or name.</param>
    public static IReadOnlySet<string>? InfraFor(Plan plan, IReadOnlyCollection<string>? apps)
    {
        if (apps is null) return null;
        var keys = apps.SelectMany(n => plan.Apps.Where(a => a.Key == n || a.Name == n).Select(a => a.Key)).Distinct().ToArray();
        var topology = WorkspaceTopology.Of(plan);
        var infra = topology.Nodes.Where(n => n.IsInfra).Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        var uses = topology.Links.ToLookup(l => l.From, l => l.To, StringComparer.Ordinal);
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(Hosting.RunnerSpecs.Closure(plan, keys).Select(a => a.Key));
        while (pending.Count > 0)
            foreach (var to in uses[pending.Pop()])
                if (infra.Contains(to) && needed.Add(to)) pending.Push(to);
        return needed;
    }

    /// <summary>The apps that need the container or compose service <paramref name="infraKey"/> (by
    /// <see cref="InfraTarget.Key"/>): those whose start would start it (see <see cref="InfraFor"/>), by key.</summary>
    public static IReadOnlyList<string> UsersOf(Plan plan, string infraKey)
        => plan.Apps.Where(a => InfraFor(plan, [a.Key])!.Contains(infraKey)).Select(a => a.Key).ToArray();

    /// <summary>
    /// A main checkout's containers and compose services (of <paramref name="needed"/>, by key; null for all), and whether
    /// each is listening where the plan says. Main never starts or stops them: they're the user's, run however they run
    /// them, and sprig only looks.
    /// </summary>
    public IReadOnlyList<OwnInfra> OwnInfraOf(Plan plan, IReadOnlySet<string>? needed = null)
    {
        var listening = _listening();
        var keys = plan.Containers.Select(c => (c.Owner, c.Name))
            .Concat(plan.Compose.SelectMany(f => f.Services.Select(s => (f.Owner, s.Name))))
            .Where(k => needed?.Contains($"{k.Owner}/{k.Name}") ?? true);
        return keys.Select(k =>
        {
            var ports = plan.Ports.Where(p => p.Owner == k.Owner && p.Resource == k.Name).Select(p => p.Port).OfType<int>().ToArray();
            return new OwnInfra($"{k.Owner}/{k.Name}", ports, ports.Length == 0 ? null : ports.All(listening.Contains));
        }).ToArray();
    }

    /// <summary>Why a main checkout can't start: the infra it uses that isn't listening, since it's the user's to start.</summary>
    static WorkspaceException OwnInfraMissing(IReadOnlyList<OwnInfra> missing)
    {
        var what = string.Join(", ", missing.Select(m => $"{m.Key} (on {string.Join(", ", m.Ports)})"));
        return new WorkspaceException($"the main checkout runs with your own infra, and {(missing.Count == 1 ? "this isn't" : "these aren't")} listening: {what}. " +
                                      "Start it the way you usually do (docker compose up -d, say), then start again");
    }

    /// <summary>The infra <paramref name="apps"/> use (see <see cref="InfraFor"/>) that isn't running, so starting them can
    /// start it first; empty when it all is. When docker can't say, all of it, so starting it says what's wrong. A main
    /// checkout's infra is the user's to start, so for main what isn't listening is an error instead.</summary>
    public IReadOnlyList<InfraTarget> MissingInfra(WorkspaceRecord record, Plan plan, IReadOnlyCollection<string>? apps)
    {
        var needed = InfraFor(plan, apps);
        if (record.IsMain)
        {
            if (OwnInfraOf(plan, needed).Where(o => o.Answering == false).ToArray() is { Length: > 0 } missing) throw OwnInfraMissing(missing);
            return [];
        }
        var targets = InfraTargets(record, plan).Where(t => needed?.Contains(t.Key) ?? true).ToArray();
        if (targets.Length == 0) return [];
        var states = InfraStates(targets);
        return targets.Where(t => states?.GetValueOrDefault(t.Key) is not { State: "running" }).ToArray();
    }

    /// <summary>Start <paramref name="targets"/> (from <see cref="MissingInfra"/>), a step per compose project, each
    /// waited for until it's healthy. What else is running is left running.</summary>
    public void StartInfra(IReadOnlyList<InfraTarget> targets, IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default)
    {
        var step = new Steps(progress);
        foreach (var project in targets.GroupBy(t => t.Project.Name))
        {
            step.Start($"Start {project.Key}");
            try
            {
                docker.Up(project.First().Project, line => step.Output(line), cancellation, project.Select(t => t.Service).ToArray());
            }
            catch (ComposeUpException e)
            {
                throw step.Fail(new WorkspaceException(e.Message));
            }
            step.Done(string.Join(", ", project.Select(t => t.Key)));
        }
    }

    public void StartContainer(InfraTarget target) => docker.Start(target.Project, target.Service);

    public void StopContainer(InfraTarget target) => docker.Stop(target.Project, target.Service);
}
