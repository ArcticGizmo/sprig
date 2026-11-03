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
public sealed record PlannedPort(string Owner, string Resource, string Endpoint, int? Port, string? Pin, int? TargetPort);

/// <param name="Value">The resolved value, or null when it couldn't be resolved (see the plan's problems).</param>
/// <param name="Via">How the value was reached, outermost first, e.g. "import catalog-url ← shop-services".</param>
public sealed record PlannedValue(string Template, string? Value, IReadOnlyList<string> Via);

public sealed record PlannedEnv(string Key, PlannedValue Value, string? File, SourceLocation Location);

public sealed record PlannedApp(
    string Owner,
    string Name,
    string Kind,
    string Path,
    IReadOnlyList<PlannedEnv> Env,
    PlannedValue? Run,
    IReadOnlyList<string> WaitFor);

public sealed record PlannedContainer(
    string Owner,
    string Name,
    string Image,
    IReadOnlyList<string> Ports,
    IReadOnlyList<PlannedEnv> Env,
    IReadOnlyList<string> Volumes);

public sealed record PlannedOverride(string Path, PlannedValue Value);

public sealed record PlannedCompose(string Owner, string File, IReadOnlyList<PlannedOverride> Overrides);

/// <param name="Apps">The apps the group starts, as <c>repo/app</c>.</param>
public sealed record PlannedGroup(string Name, IReadOnlyList<string> Apps);
