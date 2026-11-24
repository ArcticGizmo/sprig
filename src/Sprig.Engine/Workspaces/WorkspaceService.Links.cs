using Sprig.Engine.Orchestrations;
using Sprig.Engine.Planning;

namespace Sprig.Engine.Workspaces;

/// <summary>A workspace linked to one that was just made ready, made ready again because what the link gives it changed.</summary>
/// <param name="Restart">Its apps whose values changed, by name, which need restarting to see them.</param>
/// <param name="Error">Why it couldn't be made ready again; it keeps its old files until it is.</param>
public sealed record LinkedRefresh(WorkspaceRecord Record, IReadOnlyList<string> Restart, string? Error = null);

public sealed partial class WorkspaceService
{
    /// <summary>Reads a workspace's definition from its file, for a linked workspace's main that has never come up and
    /// for workspaces linked to one that changed. The loader's, unless a test gives its own.</summary>
    internal Func<string, LoadedOrchestration>? LoadDefinition { get; init; }

    LoadedOrchestration? Load(string orchestrationPath)
        => LoadDefinition is { } load ? load(orchestrationPath) : loader?.Load(orchestrationPath);

    /// <summary>
    /// Have <paramref name="workspace"/> take its inputs from <paramref name="link"/>'s workspace, replacing a link it
    /// had to any workspace of the same repo or orchestration: one branch of a repo at a time is what anyone means. It
    /// takes effect the next time it's made ready.
    /// </summary>
    public WorkspaceRecord Link(string orchestration, string workspace, WorkspaceLink link)
    {
        if (workspace == WorkspaceRecord.MainName)
            throw new WorkspaceException("main runs the checkout as it is and takes no values, so it can't be linked to anything; check a branch out in a slot to link it");
        var record = store.Get(orchestration, workspace) ?? throw new WorkspaceException($"{orchestration} has no workspace named '{workspace}'");
        if (link.Key == record.Key) throw new WorkspaceException($"{record.SlotName} can't be linked to itself");
        if (link.Workspace != WorkspaceRecord.MainName && store.Get(link.Orchestration, link.Workspace) is null)
            throw new WorkspaceException($"{link.Orchestration} has no workspace named '{link.Workspace}'");
        var links = (record.Links ?? []).Where(l => l.Orchestration != link.Orchestration).Append(link).ToArray();
        record = record with { Links = links, UpdatedAt = DateTimeOffset.UtcNow };
        store.Save(record);
        return record;
    }

    /// <summary>Stop <paramref name="workspace"/> taking inputs from <paramref name="target"/>'s workspace (it has one link
    /// per repo or orchestration, so the ID is enough), or from every workspace when it's null. It takes effect the next
    /// time it's made ready.</summary>
    public WorkspaceRecord Unlink(string orchestration, string workspace, string? target = null)
    {
        var record = store.Get(orchestration, workspace) ?? throw new WorkspaceException($"{orchestration} has no workspace named '{workspace}'");
        var links = record.Links ?? [];
        if (target is not null && links.All(l => l.Orchestration != target))
            throw new WorkspaceException($"{record.SlotName} isn't linked to any workspace of {target}");
        var kept = target is null ? [] : links.Where(l => l.Orchestration != target).ToArray();
        record = record with { Links = kept.Length > 0 ? kept : null, UpdatedAt = DateTimeOffset.UtcNow };
        store.Save(record);
        return record;
    }

    /// <summary>The workspaces that take inputs from <paramref name="target"/>.</summary>
    public IReadOnlyList<WorkspaceRecord> LinkedTo(WorkspaceRecord target)
        => store.List().Where(r => r.Links?.Any(l => l.Key == target.Key) == true).ToArray();

    /// <summary>How people know a link's workspace: its slot's folder name, or the ID with "main".</summary>
    public string LinkLabel(WorkspaceLink link)
        => link.Workspace == WorkspaceRecord.MainName ? $"{link.Orchestration} main"
            : store.Get(link.Orchestration, link.Workspace)?.SlotName ?? $"{link.Orchestration}--{link.Workspace}";

    /// <summary>
    /// What <paramref name="record"/>'s links give the inputs of <paramref name="loaded"/>'s repos, from each linked
    /// workspace's last plan. An input the workspace sets itself is left out, since its own value wins. What's wrong
    /// with a link (its workspace is gone, two links give one input, a value would come back round to this workspace)
    /// goes in <paramref name="problems"/>.
    /// </summary>
    public IReadOnlyDictionary<string, LinkedValue>? ResolveLinks(LoadedOrchestration loaded, WorkspaceRecord record, List<PlanProblem> problems)
    {
        if (record.Links is not { Count: > 0 } links) return null;
        var inputs = loaded.Repos.Values.SelectMany(r => r.Settings).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, LinkedValue>(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);

        foreach (var link in links)
        {
            var label = LinkLabel(link);
            if (TargetPlan(link, label, problems) is not { } plan) continue;
            foreach (var input in link.Inputs?.Keys.Where(i => !inputs.Contains(i)) ?? [])
                problems.Add(Warning($"the link to {label} points '{input}' at its {link.OutputFor(input)}, but no repo here has an input '{input}'"));

            foreach (var input in inputs.Where(i => record.Values?.ContainsKey(i) != true))
            {
                var byName = link.Inputs?.ContainsKey(input) == true;
                var name = link.OutputFor(input);
                if (plan.Output(name) is not { } output)
                {
                    if (byName) problems.Add(Error($"the link to {label} points '{input}' at its {name}, which it doesn't output"));
                    continue;
                }
                if (output.Value.Value is not { } value)
                {
                    problems.Add(Error($"{label}'s {name} has no value, so '{input}' can't take it; give {label} its own inputs first"));
                    continue;
                }
                if (ComesBackTo(record.Key, link.Key, output, []))
                {
                    problems.Add(Error($"'{input}' would take {label}'s {name}, which comes back round to {record.SlotName} through a link, so neither has a value"));
                    continue;
                }

                var candidate = new LinkedValue(value, label, link.Key, name, output.Type);
                if (result.TryGetValue(input, out var had))
                {
                    if (named.Contains(input) && !byName) continue;
                    if (named.Contains(input) == byName)
                    {
                        problems.Add(Error($"{had.From} and {label} both give '{input}'; unlink one, or point it at one of them: sprig link <id> [branch] --input {input}={name}"));
                        continue;
                    }
                }
                result[input] = candidate;
                if (byName) named.Add(input);
            }
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>The linked workspace's last plan, or for a main that has never come up, main planned now: its ports are
    /// the repos' own, so they're the same either way.</summary>
    Plan? TargetPlan(WorkspaceLink link, string label, List<PlanProblem> problems)
    {
        var target = store.Get(link.Orchestration, link.Workspace);
        var main = link.Workspace == WorkspaceRecord.MainName;
        if (target is null && !main)
        {
            problems.Add(Error($"it's linked to {label}, which isn't there any more; unlink it (sprig unlink {link.Orchestration}) or link it to another workspace"));
            return null;
        }
        if (target is { IsFree: true })
            problems.Add(Warning($"{label} is free: its ports and outputs are kept, but no branch is checked out in it"));
        if (store.GetPlan(link.Orchestration, link.Workspace) is { } plan) return plan;
        if (main && Load(link.OrchestrationPath) is { } loaded)
        {
            if (!loaded.Complete || loaded.Orchestration is null)
            {
                problems.Add(Error($"{label} can't be planned to read its outputs: " +
                                   string.Join("; ", loaded.Problems.Select(p => p.Message).Concat(loaded.Untrusted.Select(u => $"{u.Path} isn't approved")))));
                return null;
            }
            var compose = new List<PlanProblem>();
            return Planner.Build(new PlanInput(loaded.Orchestration, loaded.Repos, WorkspaceRecord.MainName, settings.Get(), new HashSet<int>(),
                ComposeFiles: loaded.ComposeFiles(compose), Main: true));
        }
        problems.Add(Error($"{label} hasn't been made ready yet, so it has no ports to give; check it out or start it first"));
        return null;
    }

    /// <summary>Whether <paramref name="output"/> of the workspace keyed <paramref name="at"/> takes its value, through
    /// links, from the workspace keyed <paramref name="start"/>.</summary>
    bool ComesBackTo(string start, string at, PlannedOutput output, HashSet<string> seen)
    {
        if (!seen.Add(at + "|" + output.Name)) return false;
        foreach (var source in output.Value.Sources ?? [])
        {
            if (source.FromWorkspace is not { } from) continue;
            if (from == start) return true;
            var owner = store.List().FirstOrDefault(r => r.Key == at);
            var link = owner?.Links?.FirstOrDefault(l => l.Key == from);
            if (link is null || source.Setting is null) continue;
            var upstream = store.GetPlan(link.Orchestration, link.Workspace)?.Output(link.OutputFor(source.Setting));
            if (upstream is not null && ComesBackTo(start, from, upstream, seen)) return true;
        }
        return false;
    }

    // Workspaces being made ready again for a change in one they link to, so two linked to each other don't go round.
    readonly HashSet<string> _refreshing = new(StringComparer.Ordinal);

    /// <summary>
    /// Make ready again each workspace linked to <paramref name="target"/> that's ready now, when what its links give it
    /// differs from what it was last made ready with. Nothing is started or restarted: each says which of its apps need
    /// restarting. One that's stopped picks the change up when it next starts.
    /// </summary>
    public IReadOnlyList<LinkedRefresh> RefreshLinked(WorkspaceRecord target, CancellationToken cancellation = default)
    {
        var refreshed = new List<LinkedRefresh>();
        lock (_refreshing)
            if (!_refreshing.Add(target.Key)) return refreshed;
        try
        {
            foreach (var record in LinkedTo(target).Where(r => r.State == WorkspaceState.Up))
            {
                lock (_refreshing)
                    if (_refreshing.Contains(record.Key)) continue;
                cancellation.ThrowIfCancellationRequested();
                if (Load(record.OrchestrationPath) is not { } loaded) continue;
                loaded = loaded with { Id = record.Orchestration };
                var before = store.GetPlan(record.Orchestration, record.Name);
                var now = ResolveLinks(loaded, record, []);
                if (SameLinked(before?.Linked, now)) continue;
                try
                {
                    var again = Up(loaded, record.Name, null, cancellation: cancellation);
                    refreshed.Add(new LinkedRefresh(again, ChangedApps(before, store.GetPlan(record.Orchestration, record.Name)!)));
                }
                catch (WorkspaceException e)
                {
                    refreshed.Add(new LinkedRefresh(store.Get(record.Orchestration, record.Name) ?? record, [], e.Message));
                }
            }
        }
        finally
        {
            lock (_refreshing) _refreshing.Remove(target.Key);
        }
        return refreshed;
    }

    static bool SameLinked(IReadOnlyDictionary<string, LinkedValue>? a, IReadOnlyDictionary<string, LinkedValue>? b)
        => (a?.Count ?? 0) == (b?.Count ?? 0) && (a ?? new Dictionary<string, LinkedValue>()).All(kv => b!.TryGetValue(kv.Key, out var other) && other == kv.Value);

    /// <summary>Apps whose values differ between the two plans, by name.</summary>
    public static IReadOnlyList<string> ChangedApps(Plan? before, Plan after) => after.Apps
        .Where(a => before?.Apps.FirstOrDefault(b => b.Key == a.Key) is not { } old
                    || !old.Env.Select(e => (e.Key, e.File, e.Value.Value)).SequenceEqual(a.Env.Select(e => (e.Key, e.File, e.Value.Value))))
        .Select(a => a.Name)
        .ToArray();

    static PlanProblem Error(string message) => new(ProblemSeverity.Error, message, null);

    static PlanProblem Warning(string message) => new(ProblemSeverity.Warning, message, null);
}
