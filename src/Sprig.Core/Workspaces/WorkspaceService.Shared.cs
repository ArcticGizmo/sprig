using Sprig.Core.Planning;
using Sprig.Core.Shared;
using Sprig.Core.Store;
using Sprig.Core.Substitution;

namespace Sprig.Core.Workspaces;

/// <summary>
/// The shared-resource half of the workspace lifecycle: attaching a slot at create, refcounting the
/// container between up and down, and detaching at remove.
///
/// <para>Two counters, deliberately not the same one. <b>Attached</b> runs create → rm: it is what capacity
/// limits, and it owns the workspace's data, so a stopped workspace keeps its database exactly as it keeps
/// its worktree. <b>Running</b> runs up → down and decides whether the container should exist — and it is
/// derived by asking docker, not by trusting a record, so a crash or a manual <c>docker compose down</c>
/// can't strand a shared postgres with a phantom user.</para>
/// </summary>
public sealed partial class WorkspaceService
{
    /// <summary>
    /// Reserve a slot on every shared resource the plan uses and carve out this workspace's namespace.
    /// Runs before any worktree exists, so a full pool costs you nothing but the message.
    /// </summary>
    IReadOnlyList<SharedSlot> Attach(WorkspacePlan plan, BoundPlan bound, string workspace)
    {
        if (shared is null) return [];

        var acquired = new List<SharedSlot>();
        try
        {
            foreach (var (name, repos) in ResourcesUsedBy(plan))
            {
                var resource = shared.Resources.Get(name)
                    ?? throw new SharedResourceException(
                        $"shared resource '{name}' shaped this plan but its definition has gone missing");

                var namespaces = Namespaces(resource, repos, bound);
                var known = instances.LoadAll().Select(i => i.Workspace).Append(workspace).ToList();

                var slot = shared.Leases.Acquire(resource, workspace, namespaces, known);
                acquired.Add(slot);

                shared.Runner.EnsureUp(resource);
                shared.Runner.Attach(resource, slot);
            }
            return acquired;
        }
        catch
        {
            // Undo our own partial work before the exception leaves. Create's rollback only knows about
            // slots we managed to return, so a second resource failing must not strand the first one's.
            RollBackAttach(acquired);
            throw;
        }
    }

    /// <summary>Undo <see cref="Attach"/> after a failed create — drop the namespaces, free the slots.</summary>
    void RollBackAttach(IReadOnlyList<SharedSlot> slots)
    {
        if (shared is null) return;
        foreach (var slot in slots)
        {
            if (shared.Runner.Definition(slot.Resource) is { } resource)
                TryQuiet(() => shared.Runner.Detach(resource, slot));
            TryQuiet(() => shared.Leases.Release(slot.Resource, slot.Workspace));
        }
    }

    /// <summary>Which shared resources shaped this plan, and which repos each of them touched.</summary>
    static IReadOnlyList<(string Resource, IReadOnlyList<string> Repos)> ResourcesUsedBy(WorkspacePlan plan)
        => [.. plan.Notes
            .Where(n => n.Layer == PlanLayer.Shared && n.Source is { } && n.Repo is { })
            .GroupBy(n => n.Source!, StringComparer.Ordinal)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Select(n => n.Repo!).Distinct(StringComparer.Ordinal).ToList()))];

    /// <summary>
    /// Resolve the resource's values once per repo it injected. Two repos resolving to the <b>same</b>
    /// namespace would quietly share a database inside a feature whose entire point is that they don't —
    /// so that's an error naming the fix rather than a cleverer default.
    /// </summary>
    static IReadOnlyList<SlotNamespace> Namespaces(SharedResourceDefinition resource,
        IReadOnlyList<string> repos, BoundPlan bound)
    {
        var namespaces = new List<SlotNamespace>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var repoName in repos)
        {
            var repo = bound.Repos.FirstOrDefault(r => r.Name == repoName);
            if (repo is null) continue;

            var values = resource.Values.Keys.ToDictionary(
                key => key,
                key => SubstitutionEngine.Resolve($"${{sprig.shared.{resource.Name}.{key}}}", repo.Scope),
                StringComparer.Ordinal);

            var fingerprint = string.Join('|', values.OrderBy(v => v.Key, StringComparer.Ordinal)
                .Select(v => $"{v.Key}={v.Value}"));
            if (seen.TryGetValue(fingerprint, out var other))
                throw new SharedResourceException(
                    $"'{repoName}' and '{other}' would both use {Describe(values)} on shared resource " +
                    $"'{resource.Name}' — add ${{sprig.repo}} to its values so each repo gets its own.");
            seen[fingerprint] = repoName;

            namespaces.Add(new SlotNamespace(repoName, values));
        }

        return namespaces;
    }

    static string Describe(IReadOnlyDictionary<string, string> values)
        => values.TryGetValue("database", out var db) ? $"database '{db}'"
            : values.Count > 0 ? $"'{values.First().Value}'" : "the same namespace";

    /// <summary>Start the shared resources a workspace depends on, before its own containers come up.</summary>
    void StartSharedFor(InstanceRecord record)
    {
        if (shared is null) return;
        foreach (var slot in record.Slots)
            if (shared.Runner.Definition(slot.Resource) is { } resource)
                shared.Runner.EnsureUp(resource);
    }

    /// <summary>
    /// Stop each shared resource this workspace was using, if nothing else is still running against it.
    /// Reports what happened per resource so the caller can say so — "postgres-16 kept running, spike-auth
    /// is still up" is the sentence that makes a pooled container feel understood rather than mysterious.
    /// </summary>
    IReadOnlyList<SharedOutcome> StopSharedFor(InstanceRecord record)
    {
        if (shared is null) return [];

        var outcomes = new List<SharedOutcome>();
        foreach (var slot in record.Slots)
        {
            if (shared.Runner.Definition(slot.Resource) is not { } resource) continue;
            if (!shared.Runner.IsManaged(resource)) continue;

            var busy = OtherRunningHolders(slot.Resource, record.Workspace);
            var stopped = shared.Runner.StopIfIdle(resource, otherUsers: busy.Count > 0);
            outcomes.Add(new SharedOutcome(slot.Resource, stopped, busy));
        }
        return outcomes;
    }

    /// <summary>
    /// The other workspaces attached to this resource whose containers are actually up right now.
    /// Asking docker rather than reading <c>LastStatus</c> is the point: a record says what sprig last
    /// did, not what is true.
    /// </summary>
    IReadOnlyList<string> OtherRunningHolders(string resource, string workspace)
    {
        if (shared is null) return [];

        var running = new List<string>();
        foreach (var holder in shared.Runner.OtherHolders(resource, workspace))
        {
            var other = instances.TryLoad(holder.Workspace);
            if (other is null) continue;

            var infra = other.Repos.Where(r => r.ComposePaths.Count > 0).ToList();
            if (infra.Count == 0) continue;

            if (infra.Any(r => docker.Ps(r.ComposePaths, r.WorktreePath, ProjectName(other.Workspace)).Count > 0))
                running.Add(holder.Workspace);
        }
        return running;
    }

    /// <summary>Detach this workspace from every shared resource it holds a slot on. Best-effort.</summary>
    IReadOnlyList<string> DetachAll(InstanceRecord record)
    {
        if (shared is null) return [];

        var problems = new List<string>();
        foreach (var slot in record.Slots)
        {
            var held = shared.Leases.Peek(slot.Resource, record.Workspace);
            if (shared.Runner.Definition(slot.Resource) is { } resource && held is not null)
            {
                try
                {
                    shared.Runner.EnsureUp(resource);
                    problems.AddRange(shared.Runner.Detach(resource, held));
                }
                catch (Exception ex)
                {
                    problems.Add($"{slot.Resource}: {ex.Message}");
                }
            }
            TryQuiet(() => shared.Leases.Release(slot.Resource, record.Workspace));
        }
        return problems;
    }

    static IReadOnlyList<InstanceSlot> ToRecords(IReadOnlyList<SharedSlot> slots)
        => [.. slots.Select(s => new InstanceSlot
        {
            Resource = s.Resource,
            Slot = s.Slot,
            Namespaces = [.. s.Namespaces.Select(n => new InstanceNamespace { Repo = n.Repo, Values = n.Values })],
        })];
}

/// <summary>What happened to one shared resource when a workspace stopped using it.</summary>
/// <param name="Stopped">True if its container was brought down.</param>
/// <param name="StillUsedBy">The workspaces keeping it alive, when it wasn't.</param>
public sealed record SharedOutcome(string Resource, bool Stopped, IReadOnlyList<string> StillUsedBy);
