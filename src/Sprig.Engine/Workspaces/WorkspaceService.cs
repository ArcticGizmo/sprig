using System.Text.RegularExpressions;
using Sprig.Engine.Compose;
using Sprig.Engine.Git;
using Sprig.Engine.Planning;
using Sprig.Engine.Settings;
using Sprig.Engine.Store;
using Sprig.Engine.Orchestrations;
using Sprig.Model;

namespace Sprig.Engine.Workspaces;

public enum StepState { Running, Done, Skipped, Failed, Output }

/// <summary>Progress from <see cref="WorkspaceService"/>: a step starting or ending, or a line of a tool's output.</summary>
public sealed record WorkspaceEvent(string Step, StepState State, string? Detail = null);

/// <summary>A workspace couldn't come up. <see cref="Problems"/> holds the plan's problems when that's why,
/// <see cref="Untrusted"/> the definitions in its worktrees that need approving first, and <see cref="Asks"/> the
/// settings it needs values for.</summary>
public sealed class WorkspaceException(string message, IReadOnlyList<PlanProblem>? problems = null,
    IReadOnlyList<Definitions.NeedsTrust>? untrusted = null, IReadOnlyList<PlannedAsk>? asks = null) : Exception(message)
{
    public IReadOnlyList<PlanProblem> Problems { get; } = problems ?? [];
    public IReadOnlyList<Definitions.NeedsTrust> Untrusted { get; } = untrusted ?? [];
    public IReadOnlyList<PlannedAsk> Asks { get; } = asks ?? [];

    /// <summary>The workspace that's up in the same checkout, when that's why: taking it over takes it down first.</summary>
    public WorkspaceRecord? HeldBy { get; init; }

    public static WorkspaceException NeedsValues(IReadOnlyList<PlannedAsk> asks)
        => new($"{(asks.Count == 1 ? "an input needs" : "inputs need")} a value first: " +
               string.Join(", ", asks.Select(a => $"{a.Name} ({a.Type}, for {a.Owner})")), asks: asks);
}

public sealed record RemoveOptions(bool DeleteBranch = false, bool Force = false);

/// <summary>What's about to be started in a workspace, so only the infra it uses is: one of the definitions' groups, or
/// apps by key or name. Each brings what it depends on.</summary>
public sealed record Starting(string? Group = null, IReadOnlyCollection<string>? Apps = null)
{
    /// <summary>The apps it names in <paramref name="plan"/>; null for every one.</summary>
    public IReadOnlyCollection<string>? AppsIn(Plan plan) => Group is { } group ? Hosting.RunnerSpecs.Group(plan, group) : Apps;
}

/// <summary>How one workspace runs, chosen when it's made and kept for its life.</summary>
/// <param name="InPlace">Run the repos where they're checked out, rather than in worktrees of the workspace's own.</param>
/// <param name="From">The branch new worktree branches start from; null for each repo's own default branch.</param>
/// <param name="RandomPorts">Fresh ports each time it comes up, rather than keeping the ones it was given.</param>
/// <param name="Track">The remote an existing branch is taken from when a repo has no local one; null for whichever
/// has it, origin first.</param>
public sealed record WorkspaceOptions(bool InPlace = false, string? From = null, bool RandomPorts = false,
    string? Track = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string PortPolicy => RandomPorts ? "random" : "stable";
}

/// <summary>Where and how a branch is checked out.</summary>
/// <param name="Slot">The slot to use; null for the one the branch is already in, else the lowest free one, else a new
/// one.</param>
/// <param name="From">Where a branch that doesn't exist yet starts; null for each repo's own default branch.</param>
/// <param name="EmptyData">Reusing a free slot, remove its infra's volumes first, rather than keep the data in them.</param>
/// <param name="FixSlot">When the slot is broken (see <see cref="WorkspaceService.BrokenSlotFor"/>), delete the folders in
/// its way and make it again, rather than refuse.</param>
/// <param name="NewBranch">The branch is to be made: refused when it exists already, locally or on a remote, rather than
/// checked out.</param>
/// <param name="Track">The remote to take an existing branch from (see <see cref="WorkspaceOptions.Track"/>).</param>
public sealed record CheckOutOptions(int? Slot = null, string? From = null, bool EmptyData = false, bool FixSlot = false,
    bool NewBranch = false, string? Track = null);

/// <summary>A branch the repos of an orchestration have, for picking one.</summary>
/// <param name="Repos">The repos that have it; all of them when it's in every one.</param>
/// <param name="Default">It's the default branch of the repos that have it.</param>
public sealed record BranchChoice(string Name, string? Remote, IReadOnlyList<string> Repos, bool Default)
{
    /// <summary>As git names it: <c>feat/x</c>, or <c>origin/feat/x</c>.</summary>
    public string Ref => Remote is null ? Name : $"{Remote}/{Name}";

    /// <summary>What to know before picking it, beside its name: that it's the default, or which repos have it.</summary>
    public string Note => string.Join(" · ", new[]
    {
        Default ? "default" : null,
        Repos.Count > 0 ? "only in " + string.Join(", ", Repos) : null,
    }.OfType<string>());
}

/// <summary>A slot that can't take a branch as it stands: where its worktrees go, there are folders that aren't
/// worktrees of their repos (left from a slot that was removed, or made by hand).</summary>
/// <param name="Next">The lowest slot number that's fine to use instead.</param>
/// <param name="Holds">What the one folder in the way holds, in words, when there's one; null when not looked at.</param>
public sealed record BrokenSlot(string Slot, string SlotName, IReadOnlyList<string> InTheWay, string Next, string NextName, string? Holds = null)
{
    public string Why => InTheWay.Count == 1
        ? $"{SlotName} is broken: {InTheWay[0]} is there{(Holds is null ? "" : $", holding {Holds}")}, but isn't a worktree sprig can use"
        : $"{SlotName} is broken: {string.Join(", ", InTheWay)} are there, but aren't worktrees sprig can use";
}

/// <param name="Repo">The repo's name.</param>
/// <param name="Worktree">A worktree sprig made, rather than the repo run in place.</param>
/// <param name="Unpushed">Commits on the workspace's branch that no other branch or remote has.</param>
public sealed record WorktreeStatus(string Repo, string Dir, bool Worktree, bool Exists, bool Dirty, int Unpushed);

/// <param name="Repo">The repo folder it's in; null when it's outside every repo.</param>
public sealed record WrittenFileStatus(string Path, string Kind, string? Repo, bool Tracked, bool Ignored);

/// <summary>
/// Brings workspaces up, down and away. <c>up</c> is one step: worktrees (or the repos in place), leased ports, env files,
/// a compose project per infra source, started and healthy. Each workspace's record says exactly what it did, so
/// <c>down</c> and <c>rm</c> undo that and nothing else.
/// </summary>
/// <param name="loader">Reads the repos' definitions again from a workspace's worktrees, so a branch that changes its
/// sprig.yaml is planned as the branch has it. Without one, definitions come only from the repos' own folders.</param>
public sealed partial class WorkspaceService(
    WorkspaceStore store,
    PortLeases leases,
    ISettingsStore settings,
    GitClient git,
    DockerCompose docker,
    Func<IReadOnlySet<int>>? listening = null,
    Hosting.HostClient? host = null,
    OrchestrationLoader? loader = null)
{
    /// <summary>A workspace's apps go before its infra and worktrees, which they depend on and hold open.</summary>
    void StopApps(WorkspaceRecord record, Steps step, CancellationToken cancellation)
    {
        if (host?.TrySend(new Hosting.HostRequest("list"))?.Runners?.Any(r => r.Workspace == record.Key) != true) return;
        step.Start("Stop apps");
        var stopped = host.StopAndWait(record.Key, cancellation: cancellation);
        var forced = stopped.Count(r => r.Error is not null);
        step.Done(forced == 0 ? $"{stopped.Count} stopped" : $"{stopped.Count} stopped, {forced} had to be ended");
    }

    readonly Func<IReadOnlySet<int>> _listening = listening ?? PortAllocator.Listening;

    public static string? CheckName(string workspace)
        => NameRule().IsMatch(workspace) ? null
            : $"'{workspace}' can't name a workspace: use letters, digits, '.', '_' and '-', starting with a letter or digit";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,62}$")]
    private static partial Regex NameRule();

    /// <summary>A workspace name from a branch name: <c>feat/x</c> becomes <c>feat-x</c>.</summary>
    public static string NameFromBranch(string branch) => Regex.Replace(branch, "[^A-Za-z0-9._-]+", "-").Trim('-', '.', '_');

    public IReadOnlyList<WorkspaceRecord> List() => store.List();

    public WorkspaceRecord? Get(string orchestration, string workspace) => store.Get(orchestration, workspace);

    /// <summary>The workspace of <paramref name="orchestration"/> that <paramref name="word"/> names: its slot number, its
    /// branch, its folder's name (<c>storefront--2</c>), or the name of one that runs in place.</summary>
    public WorkspaceRecord? Find(string orchestration, string word)
        => store.Get(orchestration, word) is { } byName ? byName
            : store.List().FirstOrDefault(r => r.Orchestration == orchestration && Names(r, word));

    /// <summary>Whether <paramref name="word"/> is one of the ways to name <paramref name="record"/>.</summary>
    public static bool Names(WorkspaceRecord record, string word)
        => record.Name == word || record.Branch == word || string.Equals(record.SlotName, word, StringComparison.OrdinalIgnoreCase);

    public Plan? LastPlan(string orchestration, string workspace) => store.GetPlan(orchestration, workspace);

    /// <summary>
    /// What <paramref name="record"/> would run, for one that has never come up and so has no <see cref="LastPlan"/>:
    /// <paramref name="loaded"/> planned as it stands, and the record with the repos it would run them from (a main
    /// that has never come up has none yet). Ports aren't held until it comes up, so the plan has none to show and no
    /// quick links, which would go to them; its values hold ports picked only so they can be filled in.
    /// </summary>
    public (Plan Plan, WorkspaceRecord Record) Preview(LoadedOrchestration loaded, WorkspaceRecord record)
    {
        var orchestration = loaded.Orchestration ?? throw new WorkspaceException($"{Path.GetFileName(loaded.OrchestrationPath)} isn't ready to run", loaded.Problems);
        var problems = new List<PlanProblem>();
        var plan = Planner.Build(new PlanInput(orchestration, loaded.Repos, record.Name, settings.Get(), new HashSet<int>(),
            ComposeFiles: loaded.ComposeFiles(problems), Values: record.Values, Main: record.IsMain));
        var repos = record.Repos.Count > 0 ? record.Repos
            : orchestration.Repos.Where(r => loaded.Repos.ContainsKey(r.Path) && loaded.RepoDirs.ContainsKey(r.Path))
                .Select(r => new WorkspaceRepo(r.Path, loaded.Repos[r.Path].Name, loaded.RepoDirs[r.Path], loaded.RepoDirs[r.Path], Worktree: false, BranchCreated: false))
                .ToList();
        static PlannedMapping[] Unheld(IEnumerable<PlannedMapping> mappings) => [.. mappings.Select(m => m with { Host = null })];
        plan = plan with
        {
            Ports = [],
            Links = null,
            Apps = [.. plan.Apps.Select(a => a with { Links = null })],
            Containers = [.. plan.Containers.Select(c => c with { Mappings = Unheld(c.Mappings), Ports = [.. Unheld(c.Mappings).Select(m => m.ToString())] })],
            Compose = [.. plan.Compose.Select(c => c with { Services = [.. c.Services.Select(s => s with { Mappings = Unheld(s.Mappings) })] })],
            Problems = [.. problems, .. plan.Problems],
        };
        return (plan, record with { Repos = repos });
    }

    /// <summary>The worktree of each repo of the orchestration at <paramref name="orchestrationPath"/>'s workspace, keyed by repo path,
    /// when it has worktrees that are all still there.</summary>
    public IReadOnlyDictionary<string, string>? Worktrees(string orchestrationPath, string workspace)
        => store.List().FirstOrDefault(r => r.Name == workspace && SamePath(r.OrchestrationPath, orchestrationPath)) is { } record ? Worktrees(record) : null;

    static IReadOnlyDictionary<string, string>? Worktrees(WorkspaceRecord record)
        => record.Repos.Count > 0 && record.Repos.All(r => r.Worktree && Directory.Exists(r.Dir))
            ? record.Repos.ToDictionary(r => r.RepoPath, r => r.Dir, StringComparer.Ordinal)
            : null;

    /// <summary>
    /// Where approval for a definition file is kept. A file in a workspace's worktree counts as the same file in the repo
    /// it came from, so each version of a repo's sprig.yaml is approved once, whichever worktree it's in.
    /// </summary>
    public string TrustPathFor(string file)
    {
        foreach (var repo in store.List().SelectMany(r => r.Repos).Where(r => r.Worktree))
            if (IsUnder(file, repo.Dir) && !SamePath(file, repo.Dir))
                return Path.Combine(repo.Source, Path.GetRelativePath(repo.Dir, file));
        return file;
    }

    /// <param name="branch">The branch for the workspace's worktrees; defaults to the workspace's name. Must be null when it
    /// runs its repos in place.</param>
    /// <param name="options">How a new workspace runs; null for the defaults. An existing workspace keeps what it was made
    /// with, and options that differ from that are refused.</param>
    /// <param name="beforeInfra">Called once the workspace's files are written and before its infra starts, with the
    /// record so far and the plan; a caller that will start the apps can have them building meanwhile.</param>
    /// <param name="values">Setting values to keep on the workspace, added to (or replacing) what it has.</param>
    /// <param name="takeOver">When another definition's workspace is up in the same checkout (see <see cref="HolderOf"/>),
    /// take that one down first, rather than refuse.</param>
    /// <param name="starting">The apps about to be started (a group, say): only the infra they use is started (see
    /// <see cref="InfraFor"/>). Null for all of it.</param>
    public WorkspaceRecord Up(LoadedOrchestration loaded, string workspace, string? branch, WorkspaceOptions? options = null,
        IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default, Action<WorkspaceRecord, Plan>? beforeInfra = null,
        IReadOnlyDictionary<string, string>? values = null, bool takeOver = false, Starting? starting = null)
    {
        if (!loaded.Complete || loaded.Orchestration is not { } orchestration)
            throw new WorkspaceException($"{Path.GetFileName(loaded.OrchestrationPath)} isn't ready to run", loaded.Problems);
        if (CheckName(workspace) is { } bad) throw new WorkspaceException(bad);
        if (workspace == WorkspaceRecord.MainName)
        {
            if (branch is not null)
                throw new WorkspaceException("main runs the repos where they're checked out, so it can't have a branch of its own; check the branch out in a slot instead");
            if (values is { Count: > 0 })
                throw new WorkspaceException("main runs the checkout as it is and takes no values; check a branch out in a slot to set your own values");
            options = (options ?? new WorkspaceOptions()) with { InPlace = true };
        }

        var id = loaded.Id;
        var existing = store.Get(id, workspace);
        if (existing is not null && !SamePath(existing.OrchestrationPath, loaded.OrchestrationPath))
            throw new WorkspaceException($"workspace '{workspace}' already belongs to the orchestration at {existing.OrchestrationPath}");
        if (existing is not null && options is not null && Differs(existing, options) is { } changed)
            throw new WorkspaceException($"workspace '{workspace}' {changed}; remove it and make a new one to change that");
        if (existing is { IsFree: true } && branch is null)
            throw new WorkspaceException($"{existing.SlotName} is free; check a branch out in it");
        // The branch is the label, so a switch made in the worktree with git itself is what the slot is now on.
        if (existing is { IsFree: false, InPlace: false } && LiveBranch(existing) is { } live && live != existing.Branch)
            existing = existing with { Branch = live };
        var chosen = existing is null ? options ?? new WorkspaceOptions() : null;

        var inPlace = existing?.InPlace ?? chosen!.InPlace;
        branch = inPlace ? null : branch ?? existing?.Branch ?? workspace;
        if (inPlace && branch is not null) throw new WorkspaceException($"workspace '{workspace}' runs its repos in place, so it can't have its own branch");
        if (branch is not null && existing?.Branch is { } had && had != branch)
            throw new WorkspaceException($"{existing.SlotName} has '{had}' checked out; free it first to check out another branch");
        if (branch is not null && !git.IsValidBranchName(branch)) throw new WorkspaceException($"'{branch}' isn't a valid branch name");

        var settingsNow = settings.Get();
        var step = new Steps(progress);

        // A dry run first, so a broken orchestration fails before anything is created.
        step.Start("Plan");
        // A free slot's worktrees are still on the last branch's commit, so its new branch is read once it's checked out.
        if (existing is { IsFree: false } && Worktrees(existing) is { } worktrees)
        {
            try { loaded = FromWorktrees(loaded, worktrees); }
            catch (WorkspaceException e) { throw step.Fail(e); }
        }
        var problems = new List<PlanProblem>();
        var allValues = MergeValues(existing?.Values, values);
        var main = workspace == WorkspaceRecord.MainName;
        var listening = _listening();
        // Main always wants the same ports, so while it's up it's what's listening on them.
        if (main && existing is { State: not (WorkspaceState.Down or WorkspaceState.Released) })
            listening = listening.Except(leases.Get(existing.Key).Values).ToHashSet();
        var dryPlan = Planner.Build(new PlanInput(orchestration, loaded.Repos, workspace, settingsNow, listening,
            ComposeFiles: loaded.ComposeFiles(problems), Values: allValues, Main: main));
        problems.AddRange(dryPlan.Problems);
        if (!dryPlan.HasErrors) problems.AddRange(CheckFiles(dryPlan, loaded.ReadFrom));
        if (!inPlace) problems.AddRange(CheckCopies(loaded, loaded.ReadFrom));
        var filesCheckedFor = loaded;
        if (problems.Any(p => p.Severity == ProblemSeverity.Error))
            throw step.Fail(new WorkspaceException("the plan has errors", problems));
        if (dryPlan.ToAsk.Count > 0) throw step.Fail(WorkspaceException.NeedsValues(dryPlan.ToAsk));
        // Main's infra is the user's, so sprig only needs docker to stop what it started for main before.
        var needsDocker = main ? existing?.Compose.Count > 0 : dryPlan.Containers.Count > 0 || dryPlan.Compose.Count > 0;
        if (needsDocker && docker.Unavailable() is { } why) throw step.Fail(new WorkspaceException(why));
        // Before anything is made, so a folder in the way doesn't leave half a slot behind.
        if (!inPlace && SlotBlocker(loaded, existing?.Folder ?? WorkspaceFolder(loaded, id, workspace, settingsNow), branch!) is { } blocked)
            throw step.Fail(new WorkspaceException(blocked));
        step.Done($"{dryPlan.Apps.Count} apps, {dryPlan.Containers.Count} containers, {dryPlan.Compose.Count} compose files");

        if (inPlace && HolderOf(id, workspace, loaded.RepoDirs.Values) is { } holder)
        {
            if (!takeOver) throw step.Fail(new WorkspaceException(HeldMessage(holder, id)) { HeldBy = holder });
            step.Start($"Take over from {holder.SlotName}");
            step.Done("taking it down; its data is kept");
            Down(holder.Orchestration, holder.Name, progress, cancellation);
        }

        var now = DateTimeOffset.UtcNow;
        var record = existing ?? new WorkspaceRecord(id, loaded.OrchestrationPath, workspace, branch, chosen!.PortPolicy,
            [], [], [], WorkspaceState.Starting, now, now);
        record = record with
        {
            State = WorkspaceState.Starting,
            Branch = branch,
            UpdatedAt = now,
            Error = null,
            Folder = inPlace ? null : record.Folder ?? WorkspaceFolder(loaded, id, workspace, settingsNow),
            Values = allValues,
            CheckoutBranch = inPlace ? CheckoutBranchOf(loaded) : null,
        };

        try
        {
            record = record with { Repos = Repos(loaded, record, options?.From, options?.Track, step, cancellation) };
            store.Save(record);

            var dirs = record.Repos.ToDictionary(r => r.RepoPath, r => r.Dir, StringComparer.Ordinal);
            if (!inPlace && loader is not null && !SameDirs(loaded.ReadFrom, dirs))
            {
                step.Start("Definitions");
                loaded = FromWorktrees(loaded, dirs);
                var fresh = loaded.Repos;
                record = record with { Repos = record.Repos.Select(r => r with { Name = fresh[r.RepoPath].Name }).ToList() };
                store.Save(record);
                step.Done("read from the worktrees");
            }

            if (!inPlace && Copies(loaded).Any())
            {
                step.Start("Copy into worktrees");
                // Checked again in the worktrees, since a branch can have its own .gitignore.
                if (CheckCopies(loaded, dirs) is { Count: > 0 } copyProblems)
                    throw new WorkspaceException("sprig won't copy these into the worktrees", copyProblems);
                step.Done(string.Join("; ", CopyIntoWorktrees(loaded, record)));
            }

            step.Start("Ports");
            var plan = LeasePorts(loaded, record, existing, dirs, settingsNow);
            step.Done(string.Join(", ", plan.Ports.Select(p => $"{p.Resource}.{p.Endpoint} {p.Port}")));

            step.Start("Files");
            var alreadyChecked = ReferenceEquals(loaded, filesCheckedFor) && SameDirs(loaded.ReadFrom, dirs);
            record = record with { Files = WriteFiles(plan, record, check: !alreadyChecked) };
            store.Save(record);
            store.SavePlan(plan);
            step.Done(record.IsMain ? "none: main runs the checkout as it is" : $"{record.Files.Count} files");

            beforeInfra?.Invoke(record, plan);
            record = record with
            {
                Compose = record.IsMain ? CheckOwnInfra(plan, record, step, cancellation, InfraFor(plan, starting?.AppsIn(plan)))
                    : StartInfra(plan, record, step, cancellation, InfraFor(plan, starting?.AppsIn(plan))),
            };
            var upAt = DateTimeOffset.UtcNow;
            record = record with { State = WorkspaceState.Up, UpdatedAt = upAt, LastUpAt = upAt };
            store.Save(record);
            return record;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            step.FailCurrent(e.Message);
            store.Save(record with { State = WorkspaceState.Failed, Error = e.Message, UpdatedAt = DateTimeOffset.UtcNow });
            throw e as WorkspaceException ?? new WorkspaceException(e.Message);
        }
    }

    /// <summary>Stop the workspace's containers, keeping their volumes. Under the random port policy its ports go back
    /// too.</summary>
    public WorkspaceRecord Down(string orchestration, string workspace, IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default)
    {
        var record = store.Get(orchestration, workspace) ?? throw new WorkspaceException($"{orchestration} has no workspace named '{workspace}'");
        if (record.IsFree) return record;
        var step = new Steps(progress);
        StopApps(record, step, cancellation);
        foreach (var project in record.Compose)
        {
            step.Start($"Stop {project.Name}");
            docker.Down(project, volumes: false, line => step.Output(line), cancellation);
            step.Done();
        }
        if (record.PortPolicy == "random") leases.Release(record.Key);
        record = record with { State = WorkspaceState.Down, UpdatedAt = DateTimeOffset.UtcNow };
        store.Save(record);
        return record;
    }

    /// <summary>
    /// Delete the workspace's infra data: its apps stop and its containers go, volumes and all, leaving it down with
    /// everything else kept. Bringing it back up, and starting the apps that were running, is the caller's, since that
    /// can need approval or a take-over. A main's infra is the user's own, so its data is theirs to empty.
    /// </summary>
    public WorkspaceRecord EmptyData(string orchestration, string workspace, IProgress<WorkspaceEvent>? progress = null,
        CancellationToken cancellation = default)
    {
        var record = store.Get(orchestration, workspace)
                     ?? throw new WorkspaceException($"{orchestration} has no workspace '{workspace}' yet, so it has no data to empty");
        if (record.IsMain)
            throw new WorkspaceException("the main checkout runs with your own infra, so sprig has no data of its to empty; empty it the way you run it (docker compose down -v, say)");
        var step = new Steps(progress);
        StopApps(record, step, cancellation);
        if (record.Compose.Count == 0)
        {
            step.Start("Data");
            step.Done("nothing to empty: it has no infra");
        }
        WipeData(record, step, cancellation);
        if (!record.IsFree) record = record with { State = WorkspaceState.Down, UpdatedAt = DateTimeOffset.UtcNow };
        store.Save(record);
        return record;
    }

    /// <summary>
    /// Remove the workspace: its containers and volumes, the values it wrote, its worktrees (refusing ones with changes
    /// unless forced) and, when asked, its branches (refusing ones with unpushed commits unless forced).
    /// </summary>
    public void Remove(string orchestration, string workspace, RemoveOptions options, IProgress<WorkspaceEvent>? progress = null,
        CancellationToken cancellation = default)
    {
        var record = store.Get(orchestration, workspace) ?? throw new WorkspaceException($"{orchestration} has no workspace named '{workspace}'");
        if (record.IsMain) throw new WorkspaceException($"{record.SlotName}'s main is always there, so it can't be removed; take it down instead");
        Remove(record, options, progress, cancellation);
    }

    /// <summary>
    /// Remove the main of <paramref name="orchestration"/> as that repo or orchestration goes, or takes a new ID: it's
    /// only always there while they are. Its containers and their data go, and the values it wrote into the checkout
    /// are put back. Nothing happens when it has never come up.
    /// </summary>
    public void RemoveMain(string orchestration, IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default)
    {
        if (store.Get(orchestration, WorkspaceRecord.MainName) is { IsMain: true } record)
            Remove(record, new RemoveOptions(), progress, cancellation);
    }

    void Remove(WorkspaceRecord record, RemoveOptions options, IProgress<WorkspaceEvent>? progress, CancellationToken cancellation)
    {
        var step = new Steps(progress);

        if (!options.Force && RemovalBlockers(record, options.DeleteBranch) is [var first, ..]) throw new WorkspaceException(first);

        StopApps(record, step, cancellation);
        store.DeleteLogs(record.Key);
        foreach (var project in record.Compose)
        {
            step.Start($"Remove {project.Name}");
            docker.Down(project, volumes: true, line => step.Output(line), cancellation);
            step.Done();
        }

        step.Start("Files");
        var wrote = Wrote(record);
        foreach (var file in record.Files.Where(f => !InWorktree(record, f.Path)))
            EnvFiles.Remove(file, wrote.GetValueOrDefault(Path.GetFullPath(file.Path)));
        step.Done();

        foreach (var repo in record.Repos.Where(r => r.Worktree))
        {
            var known = git.IsRepo(repo.Source);
            step.Start($"Worktree {repo.Name}");
            step.Done(RemoveWorktree(repo, known));

            step.Start($"Clean up {repo.Dir}");
            step.Done(DeleteLeftovers(repo.Dir, step));
            if (known) git.PruneWorktrees(repo.Source);

            if (options.DeleteBranch && record.Branch is { } branch && known && git.BranchExists(repo.Source, branch))
            {
                step.Start($"Branch {branch} in {repo.Name}");
                git.DeleteBranch(repo.Source, branch, force: true);
                step.Done("deleted");
            }
        }
        if (record.Folder is { } folder && !record.Repos.Any(r => SamePath(r.Dir, folder)))
        {
            step.Start($"Slot folder {folder}");
            DeleteIfEmpty(folder);
            step.Done(Directory.Exists(folder) ? $"kept, since it holds more than the worktrees: {Contents(folder)}" : "deleted");
        }

        leases.Release(record.Key);
        store.Delete(record.Orchestration, record.Name);
    }

    const int DeleteAttempts = 5;

    /// <summary>Has git remove the worktree, saying how that went. git deletes the folder first and forgets the worktree
    /// even when it can't delete all of it, so what it leaves is cleaned up after (<see cref="DeleteLeftovers"/>).</summary>
    string RemoveWorktree(WorkspaceRepo repo, bool known)
    {
        if (!Directory.Exists(repo.Dir)) return "already gone";
        if (!known || !git.Worktrees(repo.Source).Any(w => SamePath(w, repo.Dir))) return "git had forgotten it already";
        try
        {
            git.RemoveWorktree(repo.Source, repo.Dir, force: true);
            return "removed by git";
        }
        catch (Processes.ProcessException e)
        {
            return $"git stopped partway: {e.Result.StdErr.Trim().Split('\n')[0]}";
        }
    }

    /// <summary>
    /// Delete what git left of a worktree's folder, saying what that was. Two things leave it: links (pnpm's junctions in
    /// node_modules), which git on Windows can't remove once what they point at is gone, though it still says it
    /// succeeded; and a file something still has open (an app that has only just stopped, an IDE, a build server). Links
    /// are removed rather than followed, and a held file is tried again a few times while it's let go. Failing that, it
    /// names what's held, the slot is kept, and removing again once that's closed finishes the job.
    /// </summary>
    string DeleteLeftovers(string dir, Steps step)
    {
        if (!Directory.Exists(dir)) return "nothing left";
        var left = Contents(dir);
        for (var attempt = 1; Directory.Exists(dir); attempt++)
        {
            try { DeleteTree(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == DeleteAttempts)
                    throw step.Fail(new WorkspaceException(
                        $"{dir} couldn't be deleted ({e.Message.TrimEnd('.')}). It still holds {Contents(dir)}. Close whatever has it open, such as an IDE, a terminal or a build server, then remove it again."));
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
        return $"git left {left}; deleted";
    }

    /// <summary>What a folder holds, counted without following links, for saying why it's still there.</summary>
    internal static string Contents(string dir)
    {
        const int Most = 200_000;
        int files = 0, folders = 0, links = 0;
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(dir)]);
        try
        {
            while (pending.Count > 0 && files + folders + links < Most)
                foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) links++;
                    else if (entry is DirectoryInfo sub)
                    {
                        folders++;
                        pending.Push(sub);
                    }
                    else files++;
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* counted as far as it could be read */ }
        static string Count(int n, string one) => n == 1 ? $"1 {one}" : $"{n:N0} {one}s";
        var parts = new List<string>();
        if (files > 0) parts.Add(Count(files, "file"));
        if (folders > 0) parts.Add(Count(folders, files == 0 ? "empty folder" : "folder"));
        if (links > 0) parts.Add(Count(links, "link"));
        if (parts.Count == 0) return "nothing (it's empty)";
        var what = parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " and " + parts[^1];
        return files == 0 && links > 0 ? $"no files, just {what}: what's left when git can't remove links such as pnpm's" : what;
    }

    // ---- slots ----

    /// <summary>A slot's name: a whole number from 1, as written in its folder name.</summary>
    public static bool IsSlot(string name)
        => int.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
           && n > 0 && n.ToString(System.Globalization.CultureInfo.InvariantCulture) == name;

    /// <summary>
    /// The slot of <paramref name="id"/> a check-out of <paramref name="branch"/> would use: the one it's already in, the
    /// one asked for, the lowest free one, or else the next number. Its record, when the slot exists already.
    /// </summary>
    public (string Slot, WorkspaceRecord? Record) SlotFor(string id, string branch, int? slot = null)
    {
        var mine = store.List().Where(r => r.Orchestration == id && IsSlot(r.Name)).ToArray();
        if (mine.FirstOrDefault(r => r.Branch == branch) is { } current)
        {
            if (slot is { } other && current.Name != Number(other))
                throw new WorkspaceException($"'{branch}' is already checked out in {current.SlotName}");
            return (current.Name, current);
        }
        if (slot is { } asked)
        {
            if (asked < 1) throw new WorkspaceException("slots are numbered from 1");
            var at = mine.FirstOrDefault(r => r.Name == Number(asked));
            if (at is { IsFree: false })
                throw new WorkspaceException($"{at.SlotName} has '{at.Branch}' checked out; free it first, or leave the slot out to use a free one");
            return (Number(asked), at);
        }
        if (mine.Where(r => r.IsFree).OrderBy(r => int.Parse(r.Name, System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault() is { } free)
            return (free.Name, free);
        var used = mine.Select(r => int.Parse(r.Name, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();
        var next = 1;
        while (used.Contains(next)) next++;
        return (Number(next), null);
    }

    static string Number(int slot) => slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Check <paramref name="branch"/> out in a slot (see <see cref="SlotFor"/>) and bring it up. A free slot keeps its
    /// folders, ports, values and, unless <see cref="CheckOutOptions.EmptyData"/>, its infra's data.
    /// </summary>
    /// <param name="values">Values to set, over any the slot kept.</param>
    /// <param name="unset">Values the slot kept to clear, so their defaults apply again.</param>
    /// <param name="starting">The apps about to be started; only their infra starts (see <see cref="Up"/>).</param>
    public WorkspaceRecord CheckOut(LoadedOrchestration loaded, string branch, CheckOutOptions? options = null,
        IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default, Action<WorkspaceRecord, Plan>? beforeInfra = null,
        IReadOnlyDictionary<string, string>? values = null, IReadOnlyCollection<string>? unset = null, Starting? starting = null)
    {
        options ??= new CheckOutOptions();
        if (!git.IsValidBranchName(branch)) throw new WorkspaceException($"'{branch}' isn't a valid branch name");
        if (options.NewBranch && BranchTaken(loaded, branch) is { } taken) throw new WorkspaceException(taken);
        var (slot, record) = SlotFor(loaded.Id, branch, options.Slot);

        if (BrokenSlotFor(loaded, branch, options.Slot) is { } broken)
        {
            if (!options.FixSlot)
                throw new WorkspaceException($"{broken.Why}. Fix it (deleting what's in the way), or use {broken.NextName} instead");
            FixSlot(loaded, broken, new Steps(progress));
        }
        // A new branch in the slot starts with no logs; the last one's would only mislead.
        if (record is null or { IsFree: true }) store.DeleteLogs(WorkspaceRecord.KeyOf(loaded.Id, slot));
        if (record is { IsFree: true })
        {
            if (options.EmptyData) WipeData(record, new Steps(progress), cancellation);
            if (unset is { Count: > 0 }) SetValues(record.Orchestration, record.Name, new Dictionary<string, string>(), unset);
        }
        return Up(loaded, slot, branch, new WorkspaceOptions(From: options.From, Track: options.Track), progress, cancellation, beforeInfra, values,
            starting: starting);
    }

    /// <summary>Why <paramref name="branch"/> can't be made new: it's there already, locally or on a remote, in one of the
    /// repos. Null when it can.</summary>
    public string? BranchTaken(LoadedOrchestration loaded, string branch)
    {
        var where = GitRepos(loaded)
            .SelectMany(r => git.Branches(r.Dir).Where(b => b.Name == branch).Select(b => b.Remote is null ? r.Name : $"{b.Remote} ({r.Name})"))
            .Distinct().ToArray();
        return where.Length == 0 ? null
            : $"'{branch}' already exists in {string.Join(", ", where)}; choose Existing branch to check it out, or give the new one another name";
    }

    /// <summary>
    /// The branches of <paramref name="loaded"/>'s repos, for picking one: local branches first, then each remote's, with
    /// the repos that have each (an orchestration's repos needn't all have the same branches) and which is the default.
    /// </summary>
    public IReadOnlyList<BranchChoice> BranchChoices(LoadedOrchestration loaded)
    {
        var repos = GitRepos(loaded);
        var defaults = repos.Select(r => git.DefaultBranch(r.Dir)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return repos
            .SelectMany(r => git.Branches(r.Dir).Select(b => (Branch: b, Repo: r.Name)))
            .GroupBy(x => x.Branch)
            .Select(g => new BranchChoice(g.Key.Name, g.Key.Remote,
                g.Select(x => x.Repo).Distinct().Count() == repos.Length ? [] : g.Select(x => x.Repo).Distinct().ToArray(),
                defaults.Contains(g.Key.Name)))
            .OrderBy(c => c.Remote is null ? 0 : c.Remote == "origin" ? 1 : 2)
            .ThenBy(c => c.Remote, StringComparer.Ordinal)
            .ThenByDescending(c => c.Default)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToArray();
    }

    (string Name, string Dir)[] GitRepos(LoadedOrchestration loaded)
        => loaded.RepoDirs.Where(r => git.IsRepo(r.Value))
            .Select(r => (loaded.Repos.TryGetValue(r.Key, out var repo) ? repo.Name : Path.GetFileName(r.Value.TrimEnd(Path.DirectorySeparatorChar)), r.Value))
            .ToArray();

    /// <summary>
    /// A new slot with no branch: its worktrees detached at <paramref name="at"/> (each repo's default branch when null),
    /// recorded as free, so a branch can be checked out in it later without making the worktrees then. Nothing is started.
    /// </summary>
    /// <param name="slot">The slot number to make; null for the lowest one not in use.</param>
    public WorkspaceRecord MakeFreeSlot(LoadedOrchestration loaded, string? at = null, int? slot = null, bool fixSlot = false,
        IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default)
    {
        if (loaded.Orchestration is null) throw new WorkspaceException($"{Path.GetFileName(loaded.OrchestrationPath)} isn't ready to run", loaded.Problems);
        var name = FreeSlotName(loaded.Id, slot);
        var settingsNow = settings.Get();
        var folder = WorkspaceFolder(loaded, loaded.Id, name, settingsNow);
        var step = new Steps(progress);
        var inTheWay = InTheWay(loaded, folder);
        if (inTheWay.Count > 0)
        {
            if (!fixSlot)
                throw new WorkspaceException($"{loaded.Id}--{name} is broken: {string.Join(", ", inTheWay)} {(inTheWay.Count == 1 ? "is" : "are")} there, but not worktrees sprig can use");
            FixSlot(loaded, new BrokenSlot(name, $"{loaded.Id}--{name}", inTheWay, name, $"{loaded.Id}--{name}"), step);
        }
        store.DeleteLogs(WorkspaceRecord.KeyOf(loaded.Id, name));

        var now = DateTimeOffset.UtcNow;
        var record = new WorkspaceRecord(loaded.Id, loaded.OrchestrationPath, name, null, new WorkspaceOptions().PortPolicy,
            [], [], [], WorkspaceState.Released, now, now) { Folder = folder };
        var repos = new List<WorkspaceRepo>();
        try
        {
            foreach (var (reference, source, dir) in SlotDirs(loaded, folder))
            {
                cancellation.ThrowIfCancellationRequested();
                var repoName = loaded.Repos[reference.Path].Name;
                step.Start($"Worktree {repoName}");
                if (!git.IsRepo(source)) throw new WorkspaceException($"{source} isn't a git repo, so it can't have a worktree");
                if (git.Worktrees(source).Any(w => SamePath(w, dir))) git.PruneWorktrees(source);
                var start = at ?? git.DefaultBranch(source)
                    ?? throw new WorkspaceException($"{source} has no default branch to start the slot at; choose one");
                Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                git.AddDetachedWorktree(source, dir, start);
                repos.Add(new WorkspaceRepo(reference.Path, repoName, source, dir, Worktree: true, BranchCreated: false));
                step.Done($"{dir}, detached at {start}");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            step.FailCurrent(e.Message);
            // What was made so far is kept as the free slot, so it can be removed or used rather than left in the way.
            if (repos.Count > 0) store.Save(record with { Repos = repos });
            throw e as WorkspaceException ?? new WorkspaceException(e.Message);
        }
        record = record with { Repos = repos };
        store.Save(record);
        return record;
    }

    /// <summary>The slot <see cref="MakeFreeSlot"/> makes: the one asked for, which mustn't exist, else the lowest number
    /// not in use.</summary>
    public string FreeSlotName(string id, int? slot = null)
    {
        var used = store.List().Where(r => r.Orchestration == id && IsSlot(r.Name)).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        if (slot is { } asked)
        {
            if (asked < 1) throw new WorkspaceException("slots are numbered from 1");
            if (used.Contains(Number(asked))) throw new WorkspaceException($"{id}--{asked} exists already");
            return Number(asked);
        }
        var next = 1;
        while (used.Contains(Number(next))) next++;
        return Number(next);
    }

    /// <summary>
    /// The slot a check-out of <paramref name="branch"/> would use (see <see cref="SlotFor"/>) when it's broken, with the
    /// slot to use instead; null when it's fine. Only a new or free slot counts: one the branch is already in is its own.
    /// </summary>
    public BrokenSlot? BrokenSlotFor(LoadedOrchestration loaded, string branch, int? slot = null)
    {
        if (loaded.Orchestration is null) return null;
        var (name, record) = SlotFor(loaded.Id, branch, slot);
        if (record is { IsFree: false }) return null;
        var settingsNow = settings.Get();
        var inTheWay = InTheWay(loaded, record?.Folder ?? WorkspaceFolder(loaded, loaded.Id, name, settingsNow));
        if (inTheWay.Count == 0) return null;
        var next = NextUsableSlot(loaded, name, settingsNow);
        return new BrokenSlot(name, $"{loaded.Id}--{name}", inTheWay, next, $"{loaded.Id}--{next}", inTheWay is [var only] ? Contents(only) : null);
    }

    /// <summary>The folders where the slot at <paramref name="root"/> puts its worktrees that are there but aren't
    /// worktrees of their repos.</summary>
    IReadOnlyList<string> InTheWay(LoadedOrchestration loaded, string root)
        => SlotDirs(loaded, root)
            .Where(d => Directory.Exists(d.Dir) && git.IsRepo(d.Source) && !git.Worktrees(d.Source).Any(w => SamePath(w, d.Dir)))
            .Select(d => d.Dir)
            .ToArray();

    /// <summary>The lowest free slot, else the lowest unused number, that isn't <paramref name="skip"/> and has nothing in
    /// its way.</summary>
    string NextUsableSlot(LoadedOrchestration loaded, string skip, SprigSettings settingsNow)
    {
        var mine = store.List().Where(r => r.Orchestration == loaded.Id && IsSlot(r.Name)).ToArray();
        foreach (var free in mine.Where(r => r.IsFree && r.Name != skip).OrderBy(r => int.Parse(r.Name, System.Globalization.CultureInfo.InvariantCulture)))
            if (InTheWay(loaded, free.Folder ?? WorkspaceFolder(loaded, loaded.Id, free.Name, settingsNow)).Count == 0) return free.Name;
        var used = mine.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var name = Number(n);
            if (name == skip || used.Contains(name)) continue;
            if (InTheWay(loaded, WorkspaceFolder(loaded, loaded.Id, name, settingsNow)).Count == 0) return name;
        }
    }

    void FixSlot(LoadedOrchestration loaded, BrokenSlot broken, Steps step)
    {
        foreach (var dir in broken.InTheWay)
        {
            step.Start($"Clear {dir}");
            try { DeleteTree(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw step.Fail(new WorkspaceException($"{dir} couldn't be deleted ({e.Message}); close whatever has it open, or delete it by hand"));
            }
            step.Done("deleted");
        }
        // A worktree whose folder went with it is still listed by git until it's pruned, and git won't add it again.
        foreach (var source in loaded.RepoDirs.Values.Where(git.IsRepo)) git.PruneWorktrees(source);
    }

    /// <summary>Deletes the folder and everything in it, read-only files included (git makes its objects read-only), but
    /// never what a link inside it points at: a link (a junction pnpm made, say) is removed, not followed.</summary>
    static void DeleteTree(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            info.Delete();
            return;
        }
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo sub) DeleteTree(sub.FullName);
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }
        info.Attributes = FileAttributes.Directory;
        info.Delete();
    }

    /// <summary>
    /// Free the slot for another branch: stop its apps and infra, and leave each worktree on a detached HEAD at the commit
    /// it was on. The branch, its commits, the slot's folders, ports, values and data all stay. Refused while a worktree
    /// has changes that aren't committed.
    /// </summary>
    public WorkspaceRecord Free(string orchestration, string workspace, IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default)
    {
        var record = store.Get(orchestration, workspace) ?? throw new WorkspaceException($"{orchestration} has no workspace '{workspace}'");
        if (record.InPlace)
            throw new WorkspaceException($"{orchestration} / {workspace} runs the repos where they're checked out, so there's no slot to free; take it down instead");
        if (record.IsFree) return record;
        var dirty = record.Repos.Where(r => Directory.Exists(r.Dir) && git.HasUncommittedChanges(r.Dir)).ToArray();
        if (dirty.Length > 0)
            throw new WorkspaceException("these worktrees have changes that aren't committed; commit or stash them first:\n  "
                + string.Join("\n  ", dirty.Select(r => r.Dir)));

        var step = new Steps(progress);
        StopApps(record, step, cancellation);
        store.DeleteLogs(record.Key);
        foreach (var project in record.Compose)
        {
            step.Start($"Stop {project.Name}");
            docker.Down(project, volumes: false, line => step.Output(line), cancellation);
            step.Done("data kept");
        }
        foreach (var repo in record.Repos.Where(r => Directory.Exists(r.Dir)))
        {
            step.Start($"Detach {repo.Name}");
            git.Detach(repo.Dir);
            step.Done($"was on {record.Branch}");
        }
        record = record with
        {
            Branch = null,
            State = WorkspaceState.Released,
            Error = null,
            UpdatedAt = DateTimeOffset.UtcNow,
            Repos = record.Repos.Select(r => r with { BranchCreated = false, From = null }).ToList(),
        };
        store.Save(record);
        return record;
    }

    /// <summary>Remove the workspace's containers with their volumes, so its infra next starts with empty data.</summary>
    void WipeData(WorkspaceRecord record, Steps step, CancellationToken cancellation)
    {
        foreach (var project in record.Compose)
        {
            step.Start($"Empty {project.Name}");
            docker.Down(project, volumes: true, line => step.Output(line), cancellation);
            step.Done("volumes removed");
        }
    }

    /// <summary>The branch the slot's first worktree has checked out, read from git; null when it can't be read.</summary>
    string? LiveBranch(WorkspaceRecord record)
        => record.Repos.FirstOrDefault(r => r.Worktree && Directory.Exists(r.Dir)) is { } first ? git.CurrentBranch(first.Dir) : null;

    /// <summary>
    /// Why removing <paramref name="record"/> would lose work, in the order <see cref="Remove"/> checks: worktrees with
    /// changes that aren't committed, then (when its branch would go too) commits no other branch or remote has. Empty
    /// when it can go. Asked before removing so a warning can come first; <see cref="Remove"/> asks again itself.
    /// </summary>
    public IReadOnlyList<string> RemovalBlockers(WorkspaceRecord record, bool deleteBranch)
    {
        var blockers = new List<string>();
        var dirty = record.Repos.Where(r => r.Worktree && Directory.Exists(r.Dir) && git.HasUncommittedChanges(r.Dir)).ToArray();
        if (dirty.Length > 0)
            blockers.Add("these worktrees have changes that aren't committed; commit or discard them, or force it:\n  "
                + string.Join("\n  ", dirty.Select(r => r.Dir)));
        if (deleteBranch && record.Branch is { } b)
        {
            var unpushed = record.Repos.Where(r => r.Worktree && git.BranchExists(r.Source, b) && git.CommitsOnlyOn(r.Source, b) > 0).ToArray();
            if (unpushed.Length > 0)
                blockers.Add($"'{b}' has commits no other branch or remote has, in " + string.Join(", ", unpushed.Select(r => r.Name))
                    + "; push them, keep the branch, or force it");
        }
        return blockers;
    }

    /// <summary>Each of the workspace's repos as it stands: whether its folder is there, has changes that aren't committed,
    /// and how many commits its branch has that no other branch or remote has.</summary>
    public IReadOnlyList<WorktreeStatus> WorktreeStatus(WorkspaceRecord record) => record.Repos.Select(r =>
    {
        var exists = Directory.Exists(r.Dir);
        var unpushed = r.Worktree && record.Branch is { } b && git.BranchExists(r.Source, b) ? git.CommitsOnlyOn(r.Source, b) : 0;
        return new WorktreeStatus(r.Name, r.Dir, r.Worktree, exists, exists && git.HasUncommittedChanges(r.Dir), unpushed);
    }).ToArray();

    /// <summary>The files sprig wrote for the workspace, each with whether its repo ignores it (so it can't be committed by
    /// mistake) or tracks it.</summary>
    public IReadOnlyList<WrittenFileStatus> FileStatus(WorkspaceRecord record)
    {
        var result = new List<WrittenFileStatus>();
        foreach (var group in record.Files.GroupBy(f => record.Repos.FirstOrDefault(r => InRepo(r.Dir, f.Path))?.Dir))
        {
            var status = group.Key is { } dir ? git.Status(dir, group.Select(f => f.Path).ToArray()) : null;
            foreach (var file in group)
            {
                var (tracked, ignored) = status?.GetValueOrDefault(file.Path) ?? (false, false);
                result.Add(new WrittenFileStatus(file.Path, file.Kind, group.Key, tracked, ignored));
            }
        }
        return result;
    }

    static bool InRepo(string dir, string file)
        => Path.GetFullPath(file).StartsWith(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ---- up, step by step ----

    /// <summary>What about <paramref name="record"/> differs from <paramref name="options"/>, in words, or null.
    /// Where worktrees start from only matters when they're made, so it never differs.</summary>
    static string? Differs(WorkspaceRecord record, WorkspaceOptions options)
    {
        if (record.InPlace != options.InPlace)
            return record.InPlace ? "runs its repos in place" : "has worktrees of its own";
        if (record.PortPolicy != options.PortPolicy)
            return record.PortPolicy == "random" ? "takes fresh ports each time" : "keeps the ports it was given";
        return null;
    }

    /// <param name="from">The branch new worktree branches start from; null for each repo's default branch.</param>
    /// <param name="track">The remote an existing branch is taken from; null for whichever has it.</param>
    IReadOnlyList<WorkspaceRepo> Repos(LoadedOrchestration loaded, WorkspaceRecord record, string? from, string? track, Steps step,
        CancellationToken cancellation)
    {
        var orchestration = loaded.Orchestration!;
        var repos = new List<WorkspaceRepo>();
        if (record.Branch is not { } branch)
        {
            foreach (var reference in orchestration.Repos)
                repos.Add(new WorkspaceRepo(reference.Path, loaded.Repos[reference.Path].Name, loaded.RepoDirs[reference.Path],
                    loaded.RepoDirs[reference.Path], Worktree: false, BranchCreated: false));
            return repos;
        }

        var root = record.Folder ?? throw new InvalidOperationException("a worktree workspace has a folder");
        foreach (var (reference, source, dir) in SlotDirs(loaded, root))
        {
            cancellation.ThrowIfCancellationRequested();
            var name = loaded.Repos[reference.Path].Name;
            step.Start($"Worktree {name}");

            var before = record.Repos.FirstOrDefault(r => r.RepoPath == reference.Path);
            if (WorktreeBlocker(name, source, dir, branch) is { } blocked) throw new WorkspaceException(blocked);
            if (Directory.Exists(dir))
            {
                if (git.CurrentBranch(dir) == branch)
                {
                    repos.Add(new WorkspaceRepo(reference.Path, name, source, dir, Worktree: true, before?.BranchCreated ?? false, before?.From));
                    step.Done($"{dir} (already there)");
                    continue;
                }
                // A free slot: its folder, build output and installed packages carry over to the new branch.
                var startAt = from ?? git.DefaultBranch(source)
                    ?? throw new WorkspaceException($"{source} has no default branch to start '{branch}' from; choose one to start from");
                var made = git.SwitchTo(dir, branch, startAt, track);
                repos.Add(new WorkspaceRepo(reference.Path, name, source, dir, Worktree: true, made, made ? startAt : null));
                step.Done($"{dir} on {branch}{(made ? $" (new, from {startAt})" : "")}, reusing the slot");
                continue;
            }
            if (!git.IsRepo(source)) throw new WorkspaceException($"{source} isn't a git repo, so it can't have a worktree");
            // A worktree folder deleted by hand stays listed by git, which then won't add one there again.
            if (git.Worktrees(source).Any(w => SamePath(w, dir))) git.PruneWorktrees(source);
            var start = from ?? git.DefaultBranch(source)
                ?? throw new WorkspaceException($"{source} has no default branch to start '{branch}' from; choose one to start from");
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
            var created = git.AddWorktree(source, dir, branch, start, track);
            repos.Add(new WorkspaceRepo(reference.Path, name, source, dir, Worktree: true, created || (before?.BranchCreated ?? false),
                created ? start : before?.From));
            step.Done($"{dir} on {branch}{(created ? $" (new, from {start})" : "")}");
        }
        return repos;
    }

    /// <summary>Where each repo's worktree goes in the slot at <paramref name="root"/>: a repo on its own is the slot's
    /// folder itself; an orchestration's slot holds its repos as they're laid out.</summary>
    static IEnumerable<(RepoRefModel Reference, string Source, string Dir)> SlotDirs(LoadedOrchestration loaded, string root)
    {
        var common = CommonParent(loaded.RepoDirs.Values);
        foreach (var reference in loaded.Orchestration!.Repos)
        {
            var source = loaded.RepoDirs[reference.Path];
            yield return (reference, source, loaded.Alone ? LongPath.Of(root) : LongPath.Of(Path.Combine(root, Path.GetRelativePath(common, source))));
        }
    }

    /// <summary>Why <paramref name="branch"/> can't have a worktree at <paramref name="dir"/>, or null when it can.</summary>
    /// <param name="clearing">The folder is about to be deleted (see <see cref="CheckOutOptions.FixSlot"/>), so what's
    /// there doesn't count.</param>
    string? WorktreeBlocker(string name, string source, string dir, string branch, bool clearing = false)
    {
        if (git.IsRepo(source) && git.WorktreeOn(source, branch) is { } elsewhere && !SamePath(elsewhere, dir))
            return SamePath(elsewhere, source)
                ? $"'{branch}' is checked out in {name}'s own folder, {elsewhere}; switch that to another branch first"
                : $"'{branch}' is already checked out at {elsewhere}; free that slot first";
        if (!clearing && Directory.Exists(dir) && !git.Worktrees(source).Any(w => SamePath(w, dir)))
            return $"{dir} already exists and isn't a worktree of {source}. Move or delete it, then try again.";
        return null;
    }

    /// <summary>
    /// What would stop <paramref name="branch"/> being checked out in a slot of <paramref name="loaded"/> (see
    /// <see cref="SlotFor"/>), found without creating anything: a folder in the way, or the branch checked out somewhere
    /// else. Null when nothing would. Quick enough to ask before handing over to a progress window.
    /// </summary>
    /// <param name="fixSlot">The check-out will fix a broken slot (see <see cref="CheckOutOptions.FixSlot"/>), so folders
    /// in its way don't count.</param>
    public string? CheckOutBlocker(LoadedOrchestration loaded, string branch, int? slot = null, bool fixSlot = false)
    {
        if (loaded.Orchestration is null) return null;
        if (!git.IsValidBranchName(branch)) return $"'{branch}' isn't a valid branch name";
        try
        {
            var (name, record) = SlotFor(loaded.Id, branch, slot);
            return SlotBlocker(loaded, record?.Folder ?? WorkspaceFolder(loaded, loaded.Id, name, settings.Get()), branch, fixSlot);
        }
        catch (WorkspaceException e)
        {
            return e.Message;
        }
    }

    string? SlotBlocker(LoadedOrchestration loaded, string root, string branch, bool clearing = false)
        => SlotDirs(loaded, root)
            .Select(d => WorktreeBlocker(loaded.Repos.TryGetValue(d.Reference.Path, out var repo) ? repo.Name : d.Reference.Path, d.Source, d.Dir, branch, clearing))
            .FirstOrDefault(b => b is not null);

    Plan LeasePorts(LoadedOrchestration loaded, WorkspaceRecord record, WorkspaceRecord? existing, IReadOnlyDictionary<string, string> dirs, SprigSettings settingsNow)
    {
        var problems = new List<PlanProblem>();
        var compose = loaded.ComposeFiles(problems, dirs);
        if (problems.Count > 0) throw new WorkspaceException("the workspace's compose files couldn't be read", problems);

        // Anything but a clean down may have left its containers listening on the leased ports.
        var wasUp = existing is not null && existing.State is not (WorkspaceState.Down or WorkspaceState.Released);
        var keep = record.PortPolicy != "random" || wasUp;
        var listening = _listening();

        var plan = leases.Update(record.Key, (mine, others) =>
        {
            // A running workspace listens on its own ports; they mustn't count against it.
            var inUse = listening.Where(p => !(wasUp && mine.Values.Contains(p))).ToHashSet();
            var built = Planner.Build(new PlanInput(loaded.Orchestration!, loaded.Repos, record.Name, settingsNow, inUse,
                Leased: keep ? mine : null, ComposeFiles: compose, Random: record.PortPolicy == "random" ? Random.Shared : null,
                PortPolicy: record.PortPolicy, WorktreesFrom: WorktreesFrom(record), Values: record.Values, Held: Holders(others), Main: record.IsMain));
            if (built.HasErrors || built.ToAsk.Count > 0) return (built, null);
            return (built, built.Ports.Where(p => p.Port is not null).ToDictionary(p => p.Key, p => p.Port!.Value));
        });
        if (plan.HasErrors) throw new WorkspaceException("the plan has errors", plan.Problems);
        // A branch's definitions, read from its worktrees, can ask for more than the main checkouts' did.
        if (plan.ToAsk.Count > 0) throw WorkspaceException.NeedsValues(plan.ToAsk);
        // Saved and looked up under the workspace's own key, not the name the orchestration gives itself in code.
        return plan with { Orchestration = record.Orchestration };
    }

    /// <summary>Who holds each port other workspaces lease, as people know them. Where two hold one port (a pin naming
    /// it alone, taken while the other was down), the one that's up is the one that counts.</summary>
    Dictionary<int, PortHolder> Holders(IReadOnlyDictionary<string, Dictionary<string, int>> others)
    {
        var records = store.List().ToDictionary(r => r.Key, StringComparer.Ordinal);
        var holders = new Dictionary<int, PortHolder>();
        foreach (var (key, ports) in others)
        {
            var record = records.GetValueOrDefault(key);
            // A lease with no record behind it, or anything but a clean down, may have something listening there still.
            var holder = new PortHolder(record?.SlotName ?? key, record is null || record.State is not (WorkspaceState.Down or WorkspaceState.Released));
            foreach (var port in ports.Values)
                if (!holders.TryGetValue(port, out var had) || (holder.Up && !had.Up)) holders[port] = holder;
        }
        return holders;
    }

    static IReadOnlyDictionary<string, string>? MergeValues(IReadOnlyDictionary<string, string>? kept, IReadOnlyDictionary<string, string>? given)
    {
        if (given is null || given.Count == 0) return kept;
        var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in kept ?? new Dictionary<string, string>()) merged[k] = v;
        foreach (var (k, v) in given) merged[k] = v;
        return merged;
    }

    /// <summary>
    /// Set or clear values kept on the workspace. They take effect the next time it comes up, which rewrites only the
    /// files whose content changed.
    /// </summary>
    public WorkspaceRecord SetValues(string orchestration, string workspace, IReadOnlyDictionary<string, string> set, IReadOnlyCollection<string>? unset = null)
    {
        if (workspace == WorkspaceRecord.MainName)
            throw new WorkspaceException("main runs the checkout as it is and takes no values; check a branch out in a slot to set your own values");
        var record = store.Get(orchestration, workspace) ?? throw new WorkspaceException($"{orchestration} has no workspace named '{workspace}'");
        var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in record.Values ?? new Dictionary<string, string>()) merged[k] = v;
        foreach (var k in unset ?? []) merged.Remove(k);
        foreach (var (k, v) in set) merged[k] = v;
        record = record with { Values = merged.Count > 0 ? merged : null, UpdatedAt = DateTimeOffset.UtcNow };
        store.Save(record);
        return record;
    }

    /// <summary>Where the workspace's worktrees started from, in a few words: the one branch when they share it.</summary>
    static string? WorktreesFrom(WorkspaceRecord record)
    {
        if (record.InPlace) return null;
        var froms = record.Repos.Select(r => r.From).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        return froms.Length == 1 ? froms[0] : froms.Length == 0 ? "existing branches" : "each repo's default branch";
    }

    /// <summary>A file a workspace writes values into, and the definition line that asked for it.</summary>
    sealed record FileTarget(string RepoPath, string RepoDir, string Path, string Kind, string? Profile,
        List<KeyValuePair<string, string>> Values, SourceLocation Location);

    static List<FileTarget> Targets(Plan plan, IReadOnlyDictionary<string, string> repoDirs)
    {
        var targets = new List<FileTarget>();
        foreach (var app in plan.Apps)
        {
            var repoDir = repoDirs[app.RepoPath];
            var appDir = Path.GetFullPath(Path.Combine(repoDir, app.Path));
            foreach (var group in app.Env.Where(e => e.File is not null).GroupBy(e => (e.File!, e.Kind ?? "dotenv", e.Profile)))
                Add(app.RepoPath, repoDir, Path.GetFullPath(Path.Combine(appDir, group.Key.Item1)), group.Key.Item2, group.Key.Profile,
                    group.Select(e => KeyValuePair.Create(e.Key, e.Value.Value ?? "")), group.First().Location);
        }
        return targets;

        void Add(string repoPath, string repoDir, string path, string kind, string? profile, IEnumerable<KeyValuePair<string, string>> values, SourceLocation at)
        {
            var same = targets.FindIndex(t => SamePath(t.Path, path));
            if (same < 0) targets.Add(new FileTarget(repoPath, repoDir, path, kind, profile, values.ToList(), at));
            else if (targets[same].Kind != kind) throw new WorkspaceException($"{path} is written as both {targets[same].Kind} and {kind}");
            else targets[same].Values.AddRange(values);
        }
    }

    /// <summary>
    /// Every file the plan would write values into must be one the repo already keeps out of git: not tracked, and
    /// covered by its ignore rules. That's the repo saying the file is for local values, so sprig never has to hide
    /// anything from git itself. <paramref name="repoDirs"/> are the folders the workspace would use, by repo path.
    /// </summary>
    public IReadOnlyList<PlanProblem> CheckFiles(Plan plan, IReadOnlyDictionary<string, string> repoDirs)
    {
        var problems = new List<PlanProblem>();
        var targets = Targets(plan, repoDirs);
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var statusByRepo = targets.GroupBy(t => t.RepoDir, pathComparer).AsParallel()
            .Select(g => (dir: g.Key, status: git.Status(g.Key, g.Select(t => t.Path).ToList())))
            .ToDictionary(x => x.dir, x => x.status, pathComparer);
        foreach (var target in targets)
        {
            // Without git there's nothing to commit by accident.
            if (statusByRepo[target.RepoDir] is not { } status) continue;
            var (tracked, ignored) = status[target.Path];
            var relative = Path.GetRelativePath(target.RepoDir, target.Path).Replace('\\', '/');
            if (tracked)
                problems.Add(new PlanProblem(ProblemSeverity.Error,
                    $"git tracks {relative}, so sprig won't write a workspace's values into it. Write them into a local file the app reads instead" +
                    (target.Kind == "launchSettings" ? ", such as appsettings.Local.json." : "."), target.Location));
            else if (!ignored)
                problems.Add(new PlanProblem(ProblemSeverity.Error,
                    $"{relative} isn't ignored by git, so a workspace's values could be committed. Add it to the repo's .gitignore" + target.Kind switch
                    {
                        "appSettings" => $", and have the app load it: builder.Configuration.AddJsonFile(\"{Path.GetFileName(target.Path)}\", optional: true).",
                        _ => ".",
                    }, target.Location));
        }
        return problems;
    }

    /// <summary>What each repo copies into its worktrees (<c>repo.CopyIntoWorktrees</c>), by repo path.</summary>
    static IEnumerable<(string RepoPath, WorktreeCopyModel Copy)> Copies(LoadedOrchestration loaded)
        => loaded.Orchestration!.Repos
            .Where(r => loaded.Repos.ContainsKey(r.Path))
            .SelectMany(r => (loaded.Repos[r.Path].WorktreeCopies ?? []).Select(c => (r.Path, c)));

    /// <summary>
    /// What each repo copies into its worktrees must stay inside the repo, and be something git ignores and doesn't
    /// track: tracked, every worktree has it already; not ignored, a worktree's copy could be committed. Checked in
    /// <paramref name="dirs"/>, the folders the workspace would use, by repo path.
    /// </summary>
    IReadOnlyList<PlanProblem> CheckCopies(LoadedOrchestration loaded, IReadOnlyDictionary<string, string> dirs)
    {
        var problems = new List<PlanProblem>();
        foreach (var (repoPath, copy) in Copies(loaded))
        {
            var dir = dirs[repoPath];
            var path = Path.GetFullPath(Path.Combine(dir, copy.Path));
            if (copy.Path.Length == 0 || !InRepo(dir, path))
            {
                problems.Add(new PlanProblem(ProblemSeverity.Error, $"CopyIntoWorktrees(\"{copy.Path}\") names something outside the repo; give a path inside it, such as \"backend/src/.env\".", copy.Location));
                continue;
            }
            if (!git.IsRepo(dir)) continue;
            // A folder's ignore rule is often written "name/", which only matches when git is told it's a folder. A
            // worktree has none of it yet, so the checkout it's copied from says which it is.
            var folder = Directory.Exists(path) || Directory.Exists(Path.Combine(loaded.RepoDirs[repoPath], copy.Path));
            var asked = folder ? path + '/' : path;
            if (git.IsTracked(dir, path))
                problems.Add(new PlanProblem(ProblemSeverity.Error, $"git tracks {copy.Path}, so every worktree has it already; take out CopyIntoWorktrees(\"{copy.Path}\").", copy.Location));
            else if (!git.IsIgnored(dir, asked))
                problems.Add(new PlanProblem(ProblemSeverity.Error, $"{copy.Path} isn't ignored by git, so a worktree's copy could be committed. Add it to the repo's .gitignore.", copy.Location));
        }
        return problems;
    }

    /// <summary>Copy what each repo names into its worktree from its own checkout, keeping what the worktree has
    /// already. Says, per path, what happened.</summary>
    IReadOnlyList<string> CopyIntoWorktrees(LoadedOrchestration loaded, WorkspaceRecord record)
    {
        var said = new List<string>();
        foreach (var (repoPath, copy) in Copies(loaded))
        {
            var repo = record.Repos.Single(r => r.RepoPath == repoPath);
            if (!repo.Worktree) continue;
            var source = Path.Combine(repo.Source, copy.Path);
            var target = Path.Combine(repo.Dir, copy.Path);
            said.Add($"{copy.Path}: {Copy(source, target)}");
        }
        return said;
    }

    static string Copy(string source, string target)
    {
        if (File.Exists(source))
        {
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                return "copied";
            }
            return SameContent(source, target) ? "already there" : "kept the worktree's own, which differs from the checkout's";
        }
        if (Directory.Exists(source))
        {
            int copied = 0, kept = 0;
            var pending = new Stack<string>([source]);
            while (pending.Count > 0)
                foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
                {
                    // A link (a junction pnpm made, say) points somewhere else, which isn't the repo's to copy.
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (entry is DirectoryInfo sub)
                    {
                        pending.Push(sub.FullName);
                        continue;
                    }
                    var to = Path.Combine(target, Path.GetRelativePath(source, entry.FullName));
                    if (File.Exists(to))
                    {
                        kept++;
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(entry.FullName, to);
                    copied++;
                }
            return kept == 0 ? $"copied {copied} file{(copied == 1 ? "" : "s")}"
                : $"copied {copied}, kept {kept} the worktree had already";
        }
        return "not in the checkout, so there was nothing to copy";
    }

    static bool SameContent(string a, string b)
    {
        var (left, right) = (new FileInfo(a), new FileInfo(b));
        return left.Length == right.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <param name="check">False when the plan step already checked these targets in these folders.</param>
    IReadOnlyList<WrittenFile> WriteFiles(Plan plan, WorkspaceRecord record, bool check)
    {
        var dirs = record.Repos.ToDictionary(r => r.RepoPath, r => r.Dir, StringComparer.Ordinal);
        // Checked in the workspace's own folders (a branch can have its own .gitignore), and all before any write, so a
        // refusal leaves nothing half done.
        IReadOnlyList<PlanProblem> problems = check ? CheckFiles(plan, dirs) : [];
        if (problems.Count > 0) throw new WorkspaceException("sprig won't write into these files", problems);

        var written = new List<WrittenFile>();
        // The plan saved by the last up, which this one hasn't replaced yet: what's in the files now, as sprig wrote it.
        var before = Wrote(record);
        foreach (var target in Targets(plan, dirs))
        {
            var repo = record.Repos.Single(r => r.RepoPath == target.RepoPath);
            var path = target.Path;
            var had = record.Files.FirstOrDefault(f => SamePath(f.Path, path));
            var created = had?.Created ?? !File.Exists(path);
            var values = Dedupe(target.Values);
            switch (target.Kind)
            {
                case "dotenv":
                    var seed = repo.Worktree ? Path.Combine(repo.Source, Path.GetRelativePath(repo.Dir, path)) : null;
                    EnvFiles.WriteDotEnv(path, values, seed);
                    written.Add(new WrittenFile(path, target.Kind, created));
                    break;
                case "launchSettings":
                    EnvFiles.WriteLaunchProfile(path, target.Profile ?? "sprig", values);
                    written.Add(new WrittenFile(path, target.Kind, created, target.Profile ?? "sprig"));
                    break;
                case "appSettings":
                    var keys = EnvFiles.WriteAppSettings(path, values, had?.Keys);
                    written.Add(new WrittenFile(path, target.Kind, created, Keys: keys));
                    // Keys an earlier up set here that this one doesn't.
                    if (had?.Keys?.Select(k => string.Join(':', EnvFiles.SettingPath(k.Replace('.', ':')))).Except(keys).ToArray() is { Length: > 0 } dropped)
                        EnvFiles.RemoveAppSettings(new WrittenFile(path, target.Kind, false, Keys: dropped), before.GetValueOrDefault(path));
                    break;
            }
        }

        // Undo what an earlier up wrote that this one no longer does.
        foreach (var gone in record.Files.Where(f => !written.Any(w => SamePath(w.Path, f.Path))))
            EnvFiles.Remove(gone, before.GetValueOrDefault(gone.Path));
        return written;
    }

    /// <summary>What the workspace's last up wrote into each file, by path, from the plan it saved; empty when there's
    /// none to go by. Taking values out again compares against it, so a value changed by hand since is left alone.</summary>
    Dictionary<string, IReadOnlyDictionary<string, string>> Wrote(WorkspaceRecord record)
    {
        var byPath = new Dictionary<string, IReadOnlyDictionary<string, string>>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (store.GetPlan(record.Orchestration, record.Name) is not { } plan) return byPath;
        var dirs = record.Repos.ToDictionary(r => r.RepoPath, r => r.Dir, StringComparer.Ordinal);
        if (plan.Apps.Any(a => !dirs.ContainsKey(a.RepoPath))) return byPath;
        try
        {
            foreach (var target in Targets(plan, dirs))
                byPath[Path.GetFullPath(target.Path)] = Dedupe(target.Values).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        }
        catch (WorkspaceException) { /* a plan that can't be read back only means nothing to compare against */ }
        return byPath;
    }

    /// <summary>
    /// A main checkout's infra is the user's own, so rather than starting it, <c>up</c> checks it's listening and stops
    /// with what isn't. Infra sprig started for main before it worked this way is stopped, its data kept, since it would
    /// clash with theirs. Returns the compose projects main now has: none.
    /// </summary>
    IReadOnlyList<ComposeProject> CheckOwnInfra(Plan plan, WorkspaceRecord record, Steps step, CancellationToken cancellation, IReadOnlySet<string>? needed)
    {
        foreach (var old in record.Compose)
        {
            step.Start($"Stop {old.Name}");
            docker.Down(old, volumes: false, line => step.Output(line), cancellation);
            step.Done("sprig's own copy from before; main uses yours now, and this one's data is kept");
        }
        var infra = OwnInfraOf(plan, needed);
        if (infra.Count == 0) return [];
        step.Start("Your infra");
        if (infra.Where(o => o.Answering == false).ToArray() is { Length: > 0 } missing) throw step.Fail(OwnInfraMissing(missing));
        step.Done(string.Join(", ", infra.Select(o => o.Ports.Count == 0 ? $"{o.Key} (no port to check)" : $"{o.Key} on {string.Join(", ", o.Ports)}")));
        return [];
    }

    /// <summary>One container or compose service as <c>up</c> starts it.</summary>
    /// <param name="Key"><c>owner/name</c>, as the plan names it.</param>
    /// <param name="Service">Its service in the compose project.</param>
    sealed record InfraService(string Key, string Name, string Service, bool Checked);

    /// <param name="needed">Only these, by key (see <see cref="InfraFor"/>); null for every one. Every project's file is
    /// written and kept on the record either way, so the rest can be started later.</param>
    IReadOnlyList<ComposeProject> StartInfra(Plan plan, WorkspaceRecord record, Steps step, CancellationToken cancellation,
        IReadOnlySet<string>? needed = null)
    {
        var folder = store.Folder(record.Orchestration, record.Name);
        var projects = new List<ComposeProject>();
        var services = new Dictionary<string, InfraService[]>();

        if (plan.Containers.Count > 0)
        {
            var file = Path.Combine(folder, "containers.compose.yml");
            File.WriteAllText(file, ComposeYaml.ForContainers(plan.Containers));
            var project = new ComposeProject(ContainersProject(record), file, folder);
            projects.Add(project);
            services[project.Name] = plan.Containers
                .Select(c => new InfraService($"{c.Owner}/{c.Name}", c.Name, ComposeYaml.ServiceName(c.Owner, c.Name), c.Health is not null)).ToArray();
        }
        foreach (var compose in plan.Compose)
        {
            var repo = record.Repos.Single(r => r.RepoPath == compose.RepoPath);
            var source = Path.Combine(repo.Dir, compose.File);
            var file = Path.Combine(folder, $"{ComposeYaml.ProjectName(repo.Name, Path.GetFileNameWithoutExtension(compose.File))}.compose.yml");
            File.WriteAllText(file, ComposeYaml.ForWorkspace(File.ReadAllText(source), compose));
            var project = new ComposeProject(RepoProject(record, repo.Name), file,
                Path.GetDirectoryName(Path.GetFullPath(source))!);
            projects.Add(project);
            services[project.Name] = compose.Services
                .Select(s => new InfraService($"{compose.Owner}/{s.Name}", s.Name, s.Name, s.Health is not null)).ToArray();
        }

        // Projects an earlier up started that this one no longer has.
        foreach (var gone in record.Compose.Where(c => projects.All(p => p.Name != c.Name)))
            docker.Down(gone, volumes: false, line => step.Output(line), cancellation);

        foreach (var project in projects)
        {
            var wanted = services[project.Name].Where(s => needed?.Contains(s.Key) ?? true).ToArray();
            step.Start($"Start {project.Name}");
            if (wanted.Length == 0)
            {
                step.Done("left off: nothing being started uses it");
                continue;
            }
            docker.Up(project, line => step.Output(line), cancellation,
                needed is null ? null : wanted.Select(s => s.Service).ToArray());
            var healthy = wanted.Where(s => s.Checked).Select(s => s.Name).ToArray();
            var only = needed is null || wanted.Length == services[project.Name].Length ? "" : $"{string.Join(", ", wanted.Select(s => s.Name))} only; ";
            step.Done(healthy.Length > 0 ? $"{only}running; healthy: {string.Join(", ", healthy)}" : $"{only}running");
        }
        return projects;
    }

    // ---- helpers ----

    /// <summary>The orchestration with each repo's definition read from the workspace's worktree instead, since the workspace runs
    /// its branch's code. Unchanged when there's no loader or it was already read from there.</summary>
    LoadedOrchestration FromWorktrees(LoadedOrchestration loaded, IReadOnlyDictionary<string, string> dirs)
    {
        if (loader is null || SameDirs(loaded.ReadFrom, dirs)) return loaded;
        var again = loader.Load(loaded.OrchestrationPath, dirs);
        if (again.Untrusted.Count > 0)
            throw new WorkspaceException("the workspace's branch has definitions you haven't approved: "
                + string.Join(", ", again.Untrusted.Select(u => u.Path)), untrusted: again.Untrusted);
        if (!again.Complete) throw new WorkspaceException("the definitions in the workspace's worktrees have problems", again.Problems);
        return again with { Id = loaded.Id };
    }

    static bool SameDirs(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
        => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && SamePath(kv.Value, other));

    /// <summary>
    /// Another workspace that's up where <paramref name="loaded"/>'s repos are checked out, which would have to go down
    /// for <paramref name="workspace"/> to run there in place; null when none is. Two definitions of one repo (its own
    /// sprig.yaml and a copy kept in sprig, or an orchestration that has it) each have a main in the same folder, and only
    /// one can write its files and run its apps there at a time. One that's down holds nothing.
    /// </summary>
    public WorkspaceRecord? HolderOf(LoadedOrchestration loaded, string workspace = WorkspaceRecord.MainName)
        => HolderOf(loaded.Id, workspace, loaded.RepoDirs.Values);

    /// <summary>The same for a workspace that has come up before, from the folders it ran in last time, so it can be
    /// said without reading its definitions.</summary>
    public WorkspaceRecord? HolderOf(WorkspaceRecord record)
        => record.InPlace ? HolderOf(record.Orchestration, record.Name, record.Repos.Select(r => r.Dir)) : null;

    WorkspaceRecord? HolderOf(string orchestration, string workspace, IEnumerable<string> dirs)
    {
        var mine = dirs.ToArray();
        return store.List()
            .Where(r => !(r.Orchestration == orchestration && r.Name == workspace))
            .Where(r => r.State is WorkspaceState.Up or WorkspaceState.Starting)
            .FirstOrDefault(r => r.Repos.Any(repo => !repo.Worktree && mine.Any(d => SamePath(d, repo.Dir))));
    }

    /// <summary>Why <paramref name="id"/> can't run in place while <paramref name="holder"/> is up there.</summary>
    static string HeldMessage(WorkspaceRecord holder, string id)
    {
        var where = holder.Repos.Where(r => !r.Worktree).Select(r => r.Dir.Replace('\\', '/')).FirstOrDefault() ?? "the checkout";
        return $"{holder.SlotName} is up in {where}. Take it over to take {holder.SlotName} down (its data is kept) and start {id} there";
    }

    /// <summary>The branch the first repo's checkout is on, for showing beside a workspace that runs in place.</summary>
    string? CheckoutBranchOf(LoadedOrchestration loaded)
        => loaded.RepoDirs.Values.FirstOrDefault(Directory.Exists) is { } dir ? git.CurrentBranch(dir) : null;

    /// <summary>
    /// The main of <paramref name="id"/>: the stored one, or else one that has never come up, for listing. Every repo
    /// and orchestration has a main, so it's shown before it's made; bringing it up saves it.
    /// </summary>
    public WorkspaceRecord MainOf(string id, string orchestrationPath)
        => store.Get(id, WorkspaceRecord.MainName)
           ?? new WorkspaceRecord(id, orchestrationPath, WorkspaceRecord.MainName, null, new WorkspaceOptions().PortPolicy,
               [], [], [], WorkspaceState.Down, default, default);

    static string WorkspaceFolder(LoadedOrchestration loaded, string orchestration, string workspace, SprigSettings settingsNow)
    {
        var parent = settingsNow.WorkspacesFolder is { Length: > 0 } configured ? configured : CommonParent(loaded.RepoDirs.Values);
        return LongPath.Of(Path.Combine(parent, $"{orchestration}--{workspace}"));
    }

    /// <summary>The deepest folder holding every repo; the workspace's folder mirrors the repos' layout under it.</summary>
    internal static string CommonParent(IEnumerable<string> dirs)
    {
        var parents = dirs.Select(d => Path.GetDirectoryName(Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar))!).ToArray();
        if (parents.Length == 0) throw new WorkspaceException("the orchestration names no repos");
        var common = parents[0];
        while (!parents.All(p => IsUnder(p, common)))
            common = Path.GetDirectoryName(common) ?? throw new WorkspaceException("the orchestration's repos share no folder");
        return common;
    }

    static bool IsUnder(string path, string folder)
        => SamePath(path, folder) || path.StartsWith(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, Comparison);

    static bool InWorktree(WorkspaceRecord record, string path)
        => record.Repos.Any(r => r.Worktree && IsUnder(path, r.Dir));

    static void DeleteIfEmpty(string? dir)
    {
        if (dir is null || !Directory.Exists(dir)) return;
        try
        {
            foreach (var child in Directory.EnumerateDirectories(dir)) DeleteIfEmpty(child);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* something else is in there; leave it */ }
    }

    /// <summary>Last value wins, as it would for a process env var set twice.</summary>
    static List<KeyValuePair<string, string>> Dedupe(IEnumerable<KeyValuePair<string, string>> values)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var kv in values)
        {
            result.RemoveAll(r => r.Key == kv.Key);
            result.Add(kv);
        }
        return result;
    }

    static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), Comparison);

    /// <summary>Reports steps to the caller, remembering the one in progress so a failure can be pinned on it.</summary>
    sealed class Steps(IProgress<WorkspaceEvent>? progress)
    {
        string? _current;

        public void Start(string name)
        {
            _current = name;
            progress?.Report(new WorkspaceEvent(name, StepState.Running));
        }

        public void Done(string? detail = null)
        {
            if (_current is null) return;
            progress?.Report(new WorkspaceEvent(_current, StepState.Done, detail));
            _current = null;
        }

        public void Output(string line) => progress?.Report(new WorkspaceEvent(_current ?? "", StepState.Output, line));

        public void FailCurrent(string message)
        {
            if (_current is null) return;
            progress?.Report(new WorkspaceEvent(_current, StepState.Failed, message));
            _current = null;
        }

        public WorkspaceException Fail(WorkspaceException e)
        {
            FailCurrent(e.Message);
            return e;
        }
    }
}
