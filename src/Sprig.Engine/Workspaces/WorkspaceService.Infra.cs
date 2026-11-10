using Sprig.Engine.Compose;
using Sprig.Engine.Planning;
using Sprig.Engine.Processes;

namespace Sprig.Engine.Workspaces;

/// <summary>A container of a workspace, as docker knows it.</summary>
/// <param name="Key"><c>owner/name</c>, as the plan names the container or compose service.</param>
public sealed record InfraTarget(string Key, ComposeProject Project, string Service);

public sealed partial class WorkspaceService
{
    /// <summary>The compose project for the containers a workspace's definitions declare in code.</summary>
    internal static string ContainersProject(WorkspaceRecord record) => ComposeYaml.ProjectName("sprig", record.Orchestration, record.Name);

    /// <summary>The compose project for one repo's own compose file, in a workspace.</summary>
    internal static string RepoProject(WorkspaceRecord record, string repo) => ComposeYaml.ProjectName("sprig", record.Orchestration, record.Name, repo);

    /// <summary>Where each of the plan's containers and compose services runs, under the names <c>up</c> gave them.</summary>
    public IReadOnlyList<InfraTarget> InfraTargets(WorkspaceRecord record, Plan plan)
    {
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

    /// <summary>The infra <paramref name="apps"/> use (see <see cref="InfraFor"/>) that isn't running, so starting them can
    /// start it first; empty when it all is. When docker can't say, all of it, so starting it says what's wrong.</summary>
    public IReadOnlyList<InfraTarget> MissingInfra(WorkspaceRecord record, Plan plan, IReadOnlyCollection<string>? apps)
    {
        var needed = InfraFor(plan, apps);
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
