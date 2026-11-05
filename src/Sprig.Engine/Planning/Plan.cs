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
    IReadOnlyList<PlanProblem> Problems,
    IReadOnlyList<PlannedLink>? Links = null)
{
    public bool HasErrors => Problems.Any(p => p.Severity == ProblemSeverity.Error);

    /// <summary>Each app value that reaches it from another repo or the trellis, or through an import, once per
    /// provider. Values an app takes from its own repo's resources by name aren't wires.</summary>
    public IReadOnlyList<PlannedWire> Wires() => Apps
        .SelectMany(app => app.Env.SelectMany(env => (env.Value.Sources ?? [])
            .Where(s => s.Import is not null || s.Owner != app.Owner)
            .Select(s => new PlannedWire(app.Owner, app.Name, env.Key, s.Import, s.Owner, s.Resource, s.Endpoint))))
        .Distinct()
        .ToArray();
}

/// <summary>A value that reaches an app from somewhere else.</summary>
/// <param name="Import">The import it came through; null for a reference that doesn't cross one.</param>
/// <param name="ProviderOwner">The repo (or trellis) it comes from; null when nothing fills the import.</param>
/// <param name="Provider">The app, container or service it comes from; null for a literal value.</param>
public sealed record PlannedWire(string ConsumerOwner, string Consumer, string Key, string? Import, string? ProviderOwner, string? Provider, string? Endpoint)
{
    public bool Filled => ProviderOwner is not null;
}

/// <summary>Something a value reached while it was filled in.</summary>
/// <param name="Owner">The repo (or trellis) that has it; null when the value needs an import nothing fills.</param>
/// <param name="Resource">The app, container or service whose endpoint it reached; null for a literal value supplied
/// through an import.</param>
/// <param name="Import">The import it crossed through, as the app that asked for it names it.</param>
public sealed record ValueSource(string? Owner, string? Resource, string? Endpoint, string? Import);

/// <param name="Owner">The repo (or trellis) the resource belongs to.</param>
public sealed record PlannedPort(string Owner, string Resource, string Endpoint, int? Port, string? Pin, int? TargetPort)
{
    /// <summary>Names the endpoint in a sprout's port leases.</summary>
    public string Key => LeaseKey(Owner, Resource, Endpoint);

    public static string LeaseKey(string owner, string resource, string endpoint) => $"{owner}/{resource}.{endpoint}";
}

/// <param name="Value">The resolved value, or null when it couldn't be resolved (see the plan's problems).</param>
/// <param name="Via">How the value was reached, outermost first, e.g. "import catalog-url ← shop-services". For
/// people; <paramref name="Sources"/> says the same as data.</param>
/// <param name="Sources">The endpoints and imports the value reached.</param>
public sealed record PlannedValue(string Template, string? Value, IReadOnlyList<string> Via, IReadOnlyList<ValueSource>? Sources = null);

public sealed record PlannedEnv(string Key, PlannedValue Value, string? File, SourceLocation Location);

/// <param name="RepoPath">The repo, as the trellis names it.</param>
/// <param name="Outputs">Files that also get the app's process env vars.</param>
/// <param name="Job">Runs to completion rather than staying up.</param>
/// <param name="DependsOn">Apps and jobs (as <c>owner/name</c>) to have ready first: the ones it waits for, plus apps
/// whose urls or ports reach it through its values, unless that would make a loop.</param>
/// <param name="Health">HTTP paths that must answer with a success status before it's ready.</param>
/// <param name="ReadyLog">A regular expression a line of its output must match before it's ready.</param>
/// <param name="Restart">Whether, and how often, to start it again after it exits on its own.</param>
/// <param name="Links">Its quick links, each resolved to an absolute url.</param>
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
    IReadOnlyList<string>? DependsOn = null,
    IReadOnlyList<PlannedHealth>? Health = null,
    string? ReadyLog = null,
    RestartModel? Restart = null,
    IReadOnlyList<PlannedLink>? Links = null)
{
    public string Key => $"{Owner}/{Name}";
}

/// <summary>A quick link: a page worth opening by name.</summary>
public sealed record PlannedLink(string Label, string Url);

/// <summary>A path on one of an app's endpoints that answers with a success status once the app is ready.</summary>
public sealed record PlannedHealth(string Endpoint, string Path);

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
