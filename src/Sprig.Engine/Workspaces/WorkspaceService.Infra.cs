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

    public void StartContainer(InfraTarget target) => docker.Start(target.Project, target.Service);

    public void StopContainer(InfraTarget target) => docker.Stop(target.Project, target.Service);
}
