using System.Globalization;
using Sprig.Core.Stacks;
using Sprig.Core.Substitution;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Planning;

/// <summary>
/// Builds the two-stage plan a workspace create works from.
///
/// <para><b>Stage 1 — <see cref="Plan"/>.</b> Turn a <see cref="ResolvedStack"/> into a
/// <see cref="WorkspacePlan"/>: every repo's effective config and every input's binding expression, with
/// <b>no ports allocated yet</b>. This is the object an overlay transforms (M1), and doing it before
/// allocation is what lets a rewritten binding leave its stack port unallocated rather than reserved and
/// unused.</para>
///
/// <para><b>Stage 2 — <see cref="Bind"/>.</b> Feed the allocated port numbers back in and resolve every
/// expression, producing the <see cref="BoundPlan"/> materialisation reads and <c>sprig plan</c> prints.</para>
/// </summary>
public static class WorkspacePlanner
{
    /// <summary>
    /// Build the unallocated plan. Hard-fails if the stack doesn't supply one of a repo's declared inputs —
    /// there is no partial plan, for the same reason there is no partially-resolved template.
    /// </summary>
    /// <exception cref="StackWiringException">A declared input has no binding.</exception>
    public static WorkspacePlan Plan(ResolvedStack stack, string workspace)
    {
        var repos = new List<PlannedRepo>(stack.Repos.Count);

        foreach (var repo in stack.Repos)
        {
            stack.Bindings.TryGetValue(repo.Name, out var repoBindings);
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var input in repo.Config.Inputs)
            {
                if (repoBindings is null || !repoBindings.TryGetValue(input.Name, out var expr)
                    || string.IsNullOrWhiteSpace(expr))
                    throw new StackWiringException(
                        $"repo '{repo.Name}' needs input '{input.Name}'" +
                        (input.Example is { } ex ? $" (e.g. {ex})" : "") +
                        " but the stack doesn't supply it");

                bindings[input.Name] = expr;
            }

            // The effective config starts as what's on disk. Only an overlay ever changes it, and only
            // in memory — see docs/shared-infrastructure-plan.md M1.
            repos.Add(new PlannedRepo(repo, repo.Config, bindings));
        }

        return new WorkspacePlan(workspace, stack.StackName, repos, stack.Ports, []);
    }

    /// <summary>
    /// Resolve the plan against its allocated ports. Every binding expression becomes a concrete value,
    /// each repo gets the scope its env/compose templates resolve against, and every value gains a note
    /// saying which layer produced it.
    /// </summary>
    public static BoundPlan Bind(WorkspacePlan plan, IReadOnlyDictionary<string, int> allocatedPorts)
        => BindCore(plan, SprigScope.ForWorkspace(plan.Workspace, allocatedPorts), allocatedPorts);

    /// <summary>
    /// Bind for display <b>before</b> anything is allocated: each referenced port renders as a
    /// <c>{name}</c> placeholder. A dry run must not reserve a port it isn't going to use, and showing a
    /// number sprig hasn't actually taken would be a lie the reader can't detect.
    /// </summary>
    public static BoundPlan Preview(WorkspacePlan plan)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["workspace"] = plan.Workspace };
        foreach (var name in plan.ReferencedPorts)
            values[$"ports.{name}"] = $"{{{name}}}";
        return BindCore(plan, new DictionaryVariableSource(values), new Dictionary<string, int>());
    }

    static BoundPlan BindCore(WorkspacePlan plan, IVariableSource portScope,
        IReadOnlyDictionary<string, int> allocatedPorts)
    {
        var notes = new List<PlanNote>();

        // The stack owns the ports, so they are its notes.
        foreach (var name in plan.ReferencedPorts)
            if (allocatedPorts.TryGetValue(name, out var port))
                notes.Add(new PlanNote(PlanLayer.Stack, PlanTargets.Port(name),
                    port.ToString(CultureInfo.InvariantCulture)));

        // Overlay decisions recorded at plan time, indexed so an input's note can inherit its layer.
        var planned = plan.Notes.ToDictionary(n => (n.Repo, n.Target));

        var repos = new List<BoundRepo>(plan.Repos.Count);
        foreach (var repo in plan.Repos)
        {
            var inputValues = new Dictionary<string, string>(StringComparer.Ordinal) { ["workspace"] = plan.Workspace };
            var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (input, expr) in repo.Bindings)
            {
                var value = SubstitutionEngine.Resolve(expr, portScope);
                inputValues[input] = value;
                resolved[input] = value;

                var target = PlanTargets.Input(input);
                var origin = planned.GetValueOrDefault((repo.Name, target));
                notes.Add(new PlanNote(origin?.Layer ?? PlanLayer.Stack, target, value)
                {
                    Repo = repo.Name,
                    Expression = expr == value ? null : expr,
                    Source = origin?.Source,
                    Replaced = Display(origin?.Replaced, portScope),
                });
            }

            var scope = new DictionaryVariableSource(inputValues);
            repos.Add(new BoundRepo(repo.Source, repo.EffectiveConfig, resolved, scope));

            // Carry through the plan-time notes that aren't about inputs (env keys, compose paths,
            // suppressed services), resolving each against this repo's scope now that it exists.
            foreach (var note in plan.Notes)
            {
                if (!string.Equals(note.Repo, repo.Name, StringComparison.Ordinal)) continue;
                if (note.Target.StartsWith("input:", StringComparison.Ordinal)) continue;
                notes.Add(note with
                {
                    Value = Display(note.Value, scope) ?? note.Value,
                    Expression = note.Value,
                    Replaced = Display(note.Replaced, scope),
                });
            }
        }

        return new BoundPlan(plan.Workspace, plan.StackName, allocatedPorts, repos,
            plan.UnreferencedPorts, notes);
    }

    /// <summary>
    /// Resolve a template for display, falling back to the raw text when it can't be resolved. The case
    /// that matters is a <b>displaced</b> expression: once an overlay rewrites the binding that referenced
    /// <c>${sprig.ports.postgres_port}</c>, that port is never allocated, so the value it "would have had"
    /// genuinely doesn't exist. Showing the expression is honest; inventing a number would not be.
    /// </summary>
    static string? Display(string? template, IVariableSource scope)
    {
        if (template is null) return null;
        try { return SubstitutionEngine.Resolve(template, scope); }
        catch (SubstitutionException) { return template; }
    }
}
