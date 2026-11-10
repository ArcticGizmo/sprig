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

    public static WorkspaceException NeedsValues(IReadOnlyList<PlannedAsk> asks)
        => new($"{(asks.Count == 1 ? "a setting needs" : "settings need")} a value first: " +
               string.Join(", ", asks.Select(a => $"{a.Name} ({a.Type}, for {a.Owner})")), asks: asks);
}

public sealed record RemoveOptions(bool DeleteBranch = false, bool Force = false);

/// <summary>How one workspace runs, chosen when it's made and kept for its life.</summary>
/// <param name="InPlace">Run the repos where they're checked out, rather than in worktrees of the workspace's own.</param>
/// <param name="From">The branch new worktree branches start from; null for each repo's own default branch.</param>
/// <param name="RandomPorts">Fresh ports each time it comes up, rather than keeping the ones it was given.</param>
public sealed record WorkspaceOptions(bool InPlace = false, string? From = null, bool RandomPorts = false)
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
public sealed record CheckOutOptions(int? Slot = null, string? From = null, bool EmptyData = false, bool FixSlot = false);

/// <summary>A slot that can't take a branch as it stands: where its worktrees go, there are folders that aren't
/// worktrees of their repos (left from a slot that was removed, or made by hand).</summary>
/// <param name="Next">The lowest slot number that's fine to use instead.</param>
public sealed record BrokenSlot(string Slot, string SlotName, IReadOnlyList<string> InTheWay, string Next, string NextName)
{
    public string Why => InTheWay.Count == 1
        ? $"{SlotName} is broken: {InTheWay[0]} is there, but isn't a worktree sprig can use"
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
/// sprig.cs is planned as the branch has it. Without one, definitions come only from the repos' own folders.</param>
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
    /// it came from, so each version of a repo's sprig.cs is approved once, whichever worktree it's in.
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
    public WorkspaceRecord Up(LoadedOrchestration loaded, string workspace, string? branch, WorkspaceOptions? options = null,
        IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default, Action<WorkspaceRecord, Plan>? beforeInfra = null,
        IReadOnlyDictionary<string, string>? values = null)
    {
        if (!loaded.Complete || loaded.Orchestration is not { } orchestration)
            throw new WorkspaceException($"{Path.GetFileName(loaded.OrchestrationPath)} isn't ready to run", loaded.Problems);
        if (CheckName(workspace) is { } bad) throw new WorkspaceException(bad);

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
            throw new WorkspaceException($"{existing.SlotName} has '{had}' checked out; release it first to check out another branch");
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
        var dryPlan = Planner.Build(new PlanInput(orchestration, loaded.Repos, workspace, settingsNow, _listening(),
            ComposeFiles: loaded.ComposeFiles(problems), Values: allValues));
        problems.AddRange(dryPlan.Problems);
        if (!dryPlan.HasErrors) problems.AddRange(CheckFiles(dryPlan, loaded.ReadFrom));
        var filesCheckedFor = loaded;
        if (problems.Any(p => p.Severity == ProblemSeverity.Error))
            throw step.Fail(new WorkspaceException("the plan has errors", problems));
        if (dryPlan.ToAsk.Count > 0) throw step.Fail(WorkspaceException.NeedsValues(dryPlan.ToAsk));
        var needsDocker = dryPlan.Containers.Count > 0 || dryPlan.Compose.Count > 0;
        if (needsDocker && docker.Unavailable() is { } why) throw step.Fail(new WorkspaceException(why));
        // Before anything is made, so a folder in the way doesn't leave half a slot behind.
        if (!inPlace && SlotBlocker(loaded, existing?.Folder ?? WorkspaceFolder(loaded, id, workspace, settingsNow), branch!) is { } blocked)
            throw step.Fail(new WorkspaceException(blocked));
        step.Done($"{dryPlan.Apps.Count} apps, {dryPlan.Containers.Count} containers, {dryPlan.Compose.Count} compose files");

        if (inPlace) CheckNoOtherInPlace(id, workspace, loaded.RepoDirs.Values);

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
        };

        try
        {
            record = record with { Repos = Repos(loaded, record, options?.From, step, cancellation) };
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

            step.Start("Ports");
            var plan = LeasePorts(loaded, record, existing, dirs, settingsNow);
            step.Done(string.Join(", ", plan.Ports.Select(p => $"{p.Resource}.{p.Endpoint} {p.Port}")));

            step.Start("Files");
            var alreadyChecked = ReferenceEquals(loaded, filesCheckedFor) && SameDirs(loaded.ReadFrom, dirs);
            record = record with { Files = WriteFiles(plan, record, check: !alreadyChecked) };
            store.Save(record);
            store.SavePlan(plan);
            step.Done($"{record.Files.Count} files");

            beforeInfra?.Invoke(record, plan);
            record = record with { Compose = StartInfra(plan, record, step, cancellation) };
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
    /// Remove the workspace: its containers and volumes, the values it wrote, its worktrees (refusing ones with changes
    /// unless forced) and, when asked, its branches (refusing ones with unpushed commits unless forced).
    /// </summary>
    public void Remove(string orchestration, string workspace, RemoveOptions options, IProgress<WorkspaceEvent>? progress = null,
        CancellationToken cancellation = default)
    {
        var record = store.Get(orchestration, workspace) ?? throw new WorkspaceException($"{orchestration} has no workspace named '{workspace}'");
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
        foreach (var file in record.Files.Where(f => !InWorktree(record, f.Path)))
            EnvFiles.Remove(file);
        step.Done();

        foreach (var repo in record.Repos.Where(r => r.Worktree))
        {
            step.Start($"Worktree {repo.Name}");
            RemoveWorktree(repo, step);
            if (options.DeleteBranch && record.Branch is { } branch && git.BranchExists(repo.Source, branch))
                git.DeleteBranch(repo.Source, branch, force: true);
            step.Done(options.DeleteBranch ? "removed, with its branch" : "removed");
        }
        DeleteIfEmpty(record.Folder);

        leases.Release(record.Key);
        store.Delete(orchestration, workspace);
    }

    const int DeleteAttempts = 5;

    /// <summary>
    /// git forgets a worktree once it has started deleting its folder, so a file something still has open (an app that
    /// has only just stopped, an IDE, a build server) leaves the folder half deleted and no longer a worktree: asking git
    /// again fails, and the folder is in the slot's way next time. Whatever git leaves is deleted here instead, tried a few
    /// times while the last app lets go, and git's list is pruned. Failing that, it says what's held, and removing again
    /// once that's closed finishes the job.
    /// </summary>
    void RemoveWorktree(WorkspaceRepo repo, Steps step)
    {
        var known = git.IsRepo(repo.Source);
        if (known && Directory.Exists(repo.Dir) && git.Worktrees(repo.Source).Any(w => SamePath(w, repo.Dir)))
        {
            try { git.RemoveWorktree(repo.Source, repo.Dir, force: true); }
            catch (Processes.ProcessException) { /* what it left is deleted below */ }
        }
        for (var attempt = 1; Directory.Exists(repo.Dir); attempt++)
        {
            try { DeleteTree(repo.Dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == DeleteAttempts)
                    throw step.Fail(new WorkspaceException(
                        $"{repo.Dir} couldn't be deleted ({e.Message.TrimEnd('.')}). Close whatever has it open, such as an IDE, a terminal or a build server, then remove it again."));
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }
        if (known) git.PruneWorktrees(repo.Source);
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
                throw new WorkspaceException($"{at.SlotName} has '{at.Branch}' checked out; release it first, or leave the slot out to use a free one");
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
    public WorkspaceRecord CheckOut(LoadedOrchestration loaded, string branch, CheckOutOptions? options = null,
        IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default, Action<WorkspaceRecord, Plan>? beforeInfra = null,
        IReadOnlyDictionary<string, string>? values = null, IReadOnlyCollection<string>? unset = null)
    {
        options ??= new CheckOutOptions();
        if (!git.IsValidBranchName(branch)) throw new WorkspaceException($"'{branch}' isn't a valid branch name");
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
        return Up(loaded, slot, branch, new WorkspaceOptions(From: options.From), progress, cancellation, beforeInfra, values);
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
        return new BrokenSlot(name, $"{loaded.Id}--{name}", inTheWay, next, $"{loaded.Id}--{next}");
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
    public WorkspaceRecord Release(string orchestration, string workspace, IProgress<WorkspaceEvent>? progress = null, CancellationToken cancellation = default)
    {
        var record = store.Get(orchestration, workspace) ?? throw new WorkspaceException($"{orchestration} has no workspace '{workspace}'");
        if (record.InPlace)
            throw new WorkspaceException($"{orchestration} / {workspace} runs the repos where they're checked out, so there's no slot to release; take it down instead");
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

    /// <summary>Remove a free slot's infra volumes, so the next branch in it starts with empty data.</summary>
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
    IReadOnlyList<WorkspaceRepo> Repos(LoadedOrchestration loaded, WorkspaceRecord record, string? from, Steps step, CancellationToken cancellation)
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
                var made = git.SwitchTo(dir, branch, startAt);
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
            var created = git.AddWorktree(source, dir, branch, start);
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
                : $"'{branch}' is already checked out at {elsewhere}; release it there first";
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
            var inUse = listening.Where(p => !(wasUp && mine.Values.Contains(p))).Concat(others).ToHashSet();
            var built = Planner.Build(new PlanInput(loaded.Orchestration!, loaded.Repos, record.Name, settingsNow, inUse,
                Leased: keep ? mine : null, ComposeFiles: compose, Random: record.PortPolicy == "random" ? Random.Shared : null,
                PortPolicy: record.PortPolicy, WorktreesFrom: WorktreesFrom(record), Values: record.Values));
            if (built.HasErrors || built.ToAsk.Count > 0) return (built, null);
            return (built, built.Ports.Where(p => p.Port is not null).ToDictionary(p => p.Key, p => p.Port!.Value));
        });
        if (plan.HasErrors) throw new WorkspaceException("the plan has errors", plan.Problems);
        // A branch's definitions, read from its worktrees, can ask for more than the main checkouts' did.
        if (plan.ToAsk.Count > 0) throw WorkspaceException.NeedsValues(plan.ToAsk);
        // Saved and looked up under the workspace's own key, not the name the orchestration gives itself in code.
        return plan with { Orchestration = record.Orchestration };
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
                    (target.Kind == "launchSettings" ? ", e.g. .AppSettings(\"appsettings.Local.json\", …)." : "."), target.Location));
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

    /// <param name="check">False when the plan step already checked these targets in these folders.</param>
    IReadOnlyList<WrittenFile> WriteFiles(Plan plan, WorkspaceRecord record, bool check)
    {
        var dirs = record.Repos.ToDictionary(r => r.RepoPath, r => r.Dir, StringComparer.Ordinal);
        // Checked in the workspace's own folders (a branch can have its own .gitignore), and all before any write, so a
        // refusal leaves nothing half done.
        IReadOnlyList<PlanProblem> problems = check ? CheckFiles(plan, dirs) : [];
        if (problems.Count > 0) throw new WorkspaceException("sprig won't write into these files", problems);

        var written = new List<WrittenFile>();
        foreach (var target in Targets(plan, dirs))
        {
            var repo = record.Repos.Single(r => r.RepoPath == target.RepoPath);
            var path = target.Path;
            var created = record.Files.FirstOrDefault(f => SamePath(f.Path, path))?.Created ?? !File.Exists(path);
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
                    written.Add(new WrittenFile(path, target.Kind, created, Keys: EnvFiles.WriteAppSettings(path, values)));
                    break;
            }
        }

        // Undo what an earlier up wrote that this one no longer does.
        foreach (var gone in record.Files.Where(f => !written.Any(w => SamePath(w.Path, f.Path))))
            EnvFiles.Remove(gone);
        return written;
    }

    IReadOnlyList<ComposeProject> StartInfra(Plan plan, WorkspaceRecord record, Steps step, CancellationToken cancellation)
    {
        var folder = store.Folder(record.Orchestration, record.Name);
        var projects = new List<ComposeProject>();
        var checkedBy = new Dictionary<string, string[]>();

        if (plan.Containers.Count > 0)
        {
            var file = Path.Combine(folder, "containers.compose.yml");
            File.WriteAllText(file, ComposeYaml.ForContainers(plan.Containers));
            var project = new ComposeProject(ContainersProject(record), file, folder);
            projects.Add(project);
            checkedBy[project.Name] = plan.Containers.Where(c => c.Health is not null).Select(c => c.Name).ToArray();
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
            checkedBy[project.Name] = compose.Services.Where(s => s.Health is not null).Select(s => s.Name).ToArray();
        }

        // Projects an earlier up started that this one no longer has.
        foreach (var gone in record.Compose.Where(c => projects.All(p => p.Name != c.Name)))
            docker.Down(gone, volumes: false, line => step.Output(line), cancellation);

        foreach (var project in projects)
        {
            step.Start($"Start {project.Name}");
            docker.Up(project, line => step.Output(line), cancellation);
            var healthy = checkedBy[project.Name];
            step.Done(healthy.Length > 0 ? $"running; healthy: {string.Join(", ", healthy)}" : "running");
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

    void CheckNoOtherInPlace(string orchestration, string workspace, IEnumerable<string> dirs)
    {
        var mine = dirs.ToArray();
        foreach (var other in store.List().Where(r => !(r.Orchestration == orchestration && r.Name == workspace)))
        {
            var clash = other.Repos.Where(r => !r.Worktree).FirstOrDefault(r => mine.Any(d => SamePath(d, r.Dir)));
            if (clash is not null)
                throw new WorkspaceException($"{clash.Dir} already runs in place for workspace '{other.Name}' of {other.Orchestration}; remove that workspace first");
        }
    }

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
