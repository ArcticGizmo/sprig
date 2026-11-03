using Sprig.Model;

namespace Sprig.Engine.Planning;

public enum ProblemSeverity { Error, Warning }

public sealed record PlanProblem(ProblemSeverity Severity, string Message, SourceLocation? Location);

/// <summary>Everything a sprout of a trellis would get: ports, env vars, containers and compose changes, each with the
/// value it resolves to and where that value came from.</summary>
public sealed record Plan(
    string Trellis,
    string Sprout,
    string PortPolicy,
    string? WorktreesFrom,
    IReadOnlyList<PlannedPort> Ports,
    IReadOnlyList<PlannedApp> Apps,
    IReadOnlyList<PlannedContainer> Containers,
    IReadOnlyList<PlannedCompose> Compose,
    IReadOnlyList<PlannedGroup> Groups,
    IReadOnlyList<PlanProblem> Problems)
{
    public bool HasErrors => Problems.Any(p => p.Severity == ProblemSeverity.Error);
}

/// <param name="Owner">The repo (or trellis) the resource belongs to.</param>
public sealed record PlannedPort(string Owner, string Resource, string Endpoint, int? Port, string? Pin, int? TargetPort)
{
    /// <summary>Names the endpoint in a sprout's port leases.</summary>
    public string Key => LeaseKey(Owner, Resource, Endpoint);

    public static string LeaseKey(string owner, string resource, string endpoint) => $"{owner}/{resource}.{endpoint}";
}

/// <param name="Value">The resolved value, or null when it couldn't be resolved (see the plan's problems).</param>
/// <param name="Via">How the value was reached, outermost first, e.g. "import catalog-url ← shop-services".</param>
public sealed record PlannedValue(string Template, string? Value, IReadOnlyList<string> Via);

public sealed record PlannedEnv(string Key, PlannedValue Value, string? File, SourceLocation Location);

/// <param name="RepoPath">The repo, as the trellis names it.</param>
/// <param name="Outputs">Files that also get the app's process env vars.</param>
/// <param name="Job">Runs to completion rather than staying up.</param>
/// <param name="DependsOn">Apps and jobs (as <c>owner/name</c>) to have ready first: the ones it waits for, plus apps
/// whose urls or ports reach it through its values, unless that would make a loop.</param>
public sealed record PlannedApp(
    string Owner,
    string RepoPath,
    string Name,
    string Kind,
    string Path,
    IReadOnlyList<PlannedEnv> Env,
    PlannedValue? Run,
    IReadOnlyList<string> WaitFor,
    IReadOnlyList<EnvOutputModel> Outputs,
    bool Job = false,
    IReadOnlyList<string>? DependsOn = null)
{
    public string Key => $"{Owner}/{Name}";
}

/// <summary>A host port mapped to a port inside a container.</summary>
public sealed record PlannedMapping(string Endpoint, int? Host, int Target)
{
    public override string ToString() => $"{Host?.ToString() ?? "?"}:{Target}";
}

/// <param name="Ports">For display, e.g. <c>8001:5432</c>.</param>
/// <param name="Volumes">For display, e.g. <c>data → /var/lib/postgresql/data</c>.</param>
public sealed record PlannedContainer(
    string Owner,
    string Name,
    string Image,
    IReadOnlyList<string> Ports,
    IReadOnlyList<PlannedEnv> Env,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<PlannedMapping> Mappings,
    IReadOnlyList<VolumeModel> Mounts,
    string? Health);

/// <param name="Generated">Sprig's own change (such as the ports it maps), shown with the definition's overrides.</param>
public sealed record PlannedOverride(string Path, PlannedValue Value, bool Generated = false);

/// <param name="Services">The services the repo names, which a sprout keeps; the file's other services are left out.</param>
public sealed record PlannedCompose(
    string Owner,
    string RepoPath,
    string File,
    IReadOnlyList<PlannedOverride> Overrides,
    IReadOnlyList<PlannedComposeService> Services);

/// <param name="Health">Replaces the file's own health check when set.</param>
public sealed record PlannedComposeService(string Name, IReadOnlyList<PlannedMapping> Mappings, string? Health = null);

/// <param name="Apps">The apps the group starts, as <c>repo/app</c>.</param>
public sealed record PlannedGroup(string Name, IReadOnlyList<string> Apps);
