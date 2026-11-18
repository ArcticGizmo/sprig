using Sprig.Engine.Hosting;
using Sprig.Engine.Planning;

namespace Sprig.Engine.Workspaces;

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Stop everything the workspace runs, as <see cref="Down"/> does, remembering what was up so Resume can start just
    /// that again. Pausing again after starting something by hand adds it to what's remembered.
    /// </summary>
    public WorkspaceRecord Pause(string orchestration, string workspace, IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default)
    {
        var record = store.Get(orchestration, workspace) ?? throw new WorkspaceException($"{orchestration} has no workspace named '{workspace}'");
        if (record.IsFree) throw new WorkspaceException($"{record.SlotName} is free, so nothing is running to pause");
        var plan = store.GetPlan(orchestration, workspace);
        var apps = GoingApps(record, plan, host?.TrySend(new HostRequest("list"))?.Runners ?? []);
        var infra = plan is null ? [] : RunningInfra(record, plan);
        if (apps.Count == 0 && infra.Count == 0)
            throw new WorkspaceException(record.Paused is null ? $"nothing of {record.SlotName} is running to pause" : $"{record.SlotName} is already paused");
        var paused = new PausedRun(Union(record.Paused?.Apps, apps), Union(record.Paused?.Infra, infra), DateTimeOffset.UtcNow);
        record = Down(orchestration, workspace, progress, cancellation) with { Paused = paused, UpdatedAt = DateTimeOffset.UtcNow };
        store.Save(record);
        return record;
    }

    /// <summary>
    /// Before quitting stops every app: record each workspace's going apps as a pause, so the next sprig can resume
    /// them. Its containers keep running, since quitting leaves them be, so none are added.
    /// </summary>
    public void RememberRunning(IReadOnlyList<RunnerStatus> runners)
    {
        foreach (var key in runners.Where(r => r.State.IsAlive()).Select(r => r.Workspace).Distinct())
        {
            if (List().FirstOrDefault(r => r.Key == key) is not { IsFree: false } record) continue;
            var apps = GoingApps(record, store.GetPlan(record.Orchestration, record.Name), runners);
            if (apps.Count == 0) continue;
            var now = DateTimeOffset.UtcNow;
            store.Save(record with { Paused = new PausedRun(Union(record.Paused?.Apps, apps), record.Paused?.Infra ?? [], now), UpdatedAt = now });
        }
    }

    /// <summary>Forget the pause, once what it remembered is going again.</summary>
    public WorkspaceRecord? EndPause(string orchestration, string workspace)
    {
        var record = store.Get(orchestration, workspace);
        if (record?.Paused is null) return record;
        record = record with { Paused = null, UpdatedAt = DateTimeOffset.UtcNow };
        store.Save(record);
        return record;
    }

    /// <summary>The remembered apps that aren't going now and the plan still has, by key, for Resume to start.</summary>
    public static IReadOnlyList<string> ResumeApps(WorkspaceRecord record, Plan plan, IEnumerable<RunnerStatus> runners)
    {
        if (record.Paused is not { } paused) return [];
        var going = runners.Where(r => r.Workspace == record.Key && r.State.IsAlive()).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        return paused.Apps.Where(k => !going.Contains(k) && plan.Apps.Any(a => a.Key == k)).ToArray();
    }

    /// <summary>The remembered containers that aren't running, for Resume to start after the apps, which start the ones
    /// they use themselves; this is for those no app uses.</summary>
    public IReadOnlyList<InfraTarget> ResumeInfra(WorkspaceRecord record, Plan plan)
    {
        if (record.Paused is not { Infra.Count: > 0 } paused) return [];
        var targets = InfraTargets(record, plan).Where(t => paused.Infra.Contains(t.Key)).ToArray();
        if (targets.Length == 0) return [];
        var states = InfraStates(targets);
        return targets.Where(t => states?.GetValueOrDefault(t.Key) is not { State: "running" or "restarting" }).ToArray();
    }

    /// <summary>The workspace's apps that are going, by key. Jobs are left out: the apps run them again as they start,
    /// and one that had finished has nothing to resume.</summary>
    static IReadOnlyList<string> GoingApps(WorkspaceRecord record, Plan? plan, IEnumerable<RunnerStatus> runners)
        => runners.Where(r => r.Workspace == record.Key && r.State.IsAlive())
            .Where(r => plan?.Apps.FirstOrDefault(a => a.Key == r.Name) is not { Job: true })
            .Select(r => r.Name).Distinct().ToArray();

    IReadOnlyList<string> RunningInfra(WorkspaceRecord record, Plan plan)
    {
        var targets = InfraTargets(record, plan);
        if (targets.Count == 0) return [];
        var states = InfraStates(targets);
        return targets.Where(t => states?.GetValueOrDefault(t.Key) is { State: "running" or "restarting" }).Select(t => t.Key).ToArray();
    }

    static IReadOnlyList<string> Union(IReadOnlyList<string>? had, IReadOnlyList<string> now)
        => (had ?? []).Concat(now).Distinct(StringComparer.Ordinal).ToArray();
}
