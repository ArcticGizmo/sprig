using Sprig.Core.Ports;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Stacks;

/// <summary>Thrown when a repo's <c>allowedPorts</c> restriction can't be mapped onto a stack port.</summary>
public sealed class PortConstraintException(string message) : Exception(message);

/// <summary>
/// Resolves per-repo <c>allowedPorts</c> input restrictions into per-<b>stack-port</b> allowed sets,
/// so allocation can honour them. A repo doesn't name ports — it declares inputs the stack binds to
/// <c>${sprig.ports.&lt;name&gt;}</c> — so this traces each restricted input through its binding to the
/// single stack port it feeds. Anything ambiguous (no port token, several port tokens, an unknown
/// port, or two restrictions on one port with nothing in common) is a hard error, never a silently
/// dropped restriction.
/// </summary>
public static class PortConstraintResolver
{
    /// <summary>
    /// Resolve constraints for a plan, ignoring inputs a shared resource has taken over. Once an overlay
    /// points an input at a fixed shared port, that input no longer feeds a stack port at all — so its
    /// <c>allowedPorts</c> has nothing left to constrain, and insisting on tracing it would fail a plan
    /// that is perfectly correct.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<int>> Resolve(Planning.WorkspacePlan plan)
    {
        var overridden = plan.Notes
            .Where(n => n.Layer == Planning.PlanLayer.Shared && n.Repo is not null)
            .Select(n => (n.Repo!, n.Target))
            .ToHashSet();

        var repos = plan.Repos
            .Select(r => r.Effective with
            {
                Config = r.EffectiveConfig with
                {
                    Inputs = [.. r.EffectiveConfig.Inputs.Where(
                        i => !overridden.Contains((r.Name, Planning.PlanTargets.Input(i.Name))))],
                },
            })
            .ToList();

        return Resolve(repos, plan.EffectiveBindings, plan.DeclaredPorts);
    }

    /// <summary>Stack port name → the set of host ports it may take. Ports with no restriction are absent.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<int>> Resolve(
        IReadOnlyList<ResolvedRepo> repos,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> bindings,
        IReadOnlyCollection<string> stackPorts)
    {
        var stackPortNames = new HashSet<string>(stackPorts, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlySet<int>>(StringComparer.Ordinal);

        foreach (var repo in repos)
        {
            bindings.TryGetValue(repo.Name, out var repoBindings);

            foreach (var input in repo.Config.Inputs)
            {
                if (string.IsNullOrWhiteSpace(input.AllowedPorts)) continue;

                if (!PortSetSpec.TryParse(input.AllowedPorts, out var allowed, out var err))
                    throw new PortConstraintException(
                        $"repo '{repo.Name}' input '{input.Name}' has an invalid allowedPorts " +
                        $"('{input.AllowedPorts}'): {err}");

                // Unbound inputs are StackWiring's job to report — skip here to avoid a second,
                // more confusing error for the same root cause.
                if (repoBindings is null || !repoBindings.TryGetValue(input.Name, out var expr)
                    || string.IsNullOrWhiteSpace(expr))
                    continue;

                var portRefs = PortExpressions.ReferencedPorts(expr);
                if (portRefs.Count == 0)
                    throw new PortConstraintException(
                        $"repo '{repo.Name}' input '{input.Name}' restricts ports to {PortSetSpec.Describe(allowed)}, " +
                        $"but its stack binding '{expr}' doesn't reference a ${{sprig.ports.*}} port, so the " +
                        "restriction can't be applied — bind the input to a stack port.");
                if (portRefs.Count > 1)
                    throw new PortConstraintException(
                        $"repo '{repo.Name}' input '{input.Name}' restricts ports, but its binding '{expr}' " +
                        $"references multiple ports ({string.Join(", ", portRefs)}); sprig can't tell which to restrict.");

                var portName = portRefs[0];
                if (!stackPortNames.Contains(portName))
                    throw new PortConstraintException(
                        $"repo '{repo.Name}' input '{input.Name}' restricts port '{portName}', " +
                        "but the stack doesn't declare that port.");

                if (result.TryGetValue(portName, out var existing))
                {
                    var intersection = existing.Where(allowed.Contains).ToHashSet();
                    if (intersection.Count == 0)
                        throw new PortConstraintException(
                            $"port '{portName}' has conflicting allowedPorts restrictions with no ports in common " +
                            $"({PortSetSpec.Describe(existing)} vs {PortSetSpec.Describe(allowed)}).");
                    result[portName] = intersection;
                }
                else
                {
                    result[portName] = allowed;
                }
            }
        }

        return result;
    }
}
