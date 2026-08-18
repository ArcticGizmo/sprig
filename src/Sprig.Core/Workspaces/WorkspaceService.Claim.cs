using Sprig.Core.Git;
using Sprig.Core.Setup;
using Sprig.Core.Stacks;
using Sprig.Core.Store;

namespace Sprig.Core.Workspaces;

/// <summary>Per-repo pending work found when a workspace is released — <b>surfaced, never acted on</b>. The
/// branch ref survives release, so <see cref="UnpushedCommits"/> are stranded-but-recoverable, not lost;
/// the user decides what to do before a later fresh checkout resets the workspace.</summary>
public sealed record RepoPending(string Repo, bool Dirty, int UnpushedCommits)
{
    public bool HasAny => Dirty || UnpushedCommits > 0;
}

/// <summary>What a release found across a workspace's repos. Report-only: nothing was changed.</summary>
public sealed record ReleaseReport(IReadOnlyList<RepoPending> Repos)
{
    public bool HasPending => Repos.Any(r => r.HasAny);

    /// <summary>A one-line human summary per repo with pending work, e.g.
    /// <c>api: 3 uncommitted files · web: 2 unpushed commits</c>. Empty when nothing is pending.</summary>
    public string Summary()
    {
        var parts = new List<string>();
        foreach (var r in Repos.Where(r => r.HasAny))
        {
            var bits = new List<string>();
            if (r.Dirty) bits.Add("uncommitted changes");
            if (r.UnpushedCommits > 0) bits.Add($"{r.UnpushedCommits} unpushed commit{(r.UnpushedCommits == 1 ? "" : "s")}");
            parts.Add($"{r.Repo}: {string.Join(" + ", bits)}");
        }
        return string.Join(" · ", parts);
    }
}

/// <summary>Outcome of the claim pre-flight for a branch name across a workspace's repos.
/// <see cref="Blocked"/> repos already have the branch locally (which includes one checked out in another
/// worktree) — a hard stop the user must resolve. <see cref="RemoteWarnings"/> repos have it only on a
/// remote — a heads-up, not a block.</summary>
public sealed record ClaimConflicts(IReadOnlyList<string> Blocked, IReadOnlyList<string> RemoteWarnings)
{
    public bool IsBlocked => Blocked.Count > 0;
}

public sealed partial class WorkspaceService
{
    /// <summary>Check a proposed claim branch name against every repo in the workspace, touching nothing. A
    /// name already present as a local branch (which includes one checked out in another worktree) blocks
    /// the claim; a name present only on a remote is a warning. Callers surface both;
    /// <see cref="Claim"/> enforces the block — we never resolve a conflict for the user (it may be more
    /// tangled than we can safely automate).</summary>
    public ClaimConflicts CheckClaim(InstanceRecord record, string branch)
        => CheckClaimAcross(record.Repos.Select(r => (r.Name, r.SourcePath)), branch);

    /// <summary>The repo-list form of <see cref="CheckClaim"/> — so a not-yet-created workspace can be
    /// pre-flighted against its stack's source repos <b>before</b> any workspace is materialised.</summary>
    public ClaimConflicts CheckClaimAcross(IEnumerable<(string Name, string SourcePath)> repos, string branch)
    {
        var blocked = new List<string>();
        var remote = new List<string>();
        foreach (var (name, sourcePath) in repos)
        {
            if (git.BranchExists(sourcePath, branch)) blocked.Add(name);
            else if (git.RemoteBranchExists(sourcePath, branch)) remote.Add(name);
        }
        return new ClaimConflicts(blocked, remote);
    }

    /// <summary>Throw a uniform "branch already exists" error if the pre-flight is blocked. Shared by the
    /// claim path and the pool's pre-create guard so the message is identical wherever the conflict is caught.</summary>
    internal static void ThrowIfBlocked(ClaimConflicts conflicts, string branch)
    {
        if (conflicts.IsBlocked)
            throw new WorkspaceException(
                $"branch '{branch}' already exists in: {string.Join(", ", conflicts.Blocked)}. " +
                "Delete or rename it there, or choose another name — sprig won't touch an existing branch.");
    }

    /// <summary>Throw if <paramref name="branch"/> isn't a legal git branch name. Public so the pool can
    /// reject a bad name <b>before</b> materialising a workspace (the validity check must precede any side effect).</summary>
    public void EnsureValidBranchName(string branch)
    {
        if (!git.IsValidBranchName(branch))
            throw new WorkspaceException(
                $"'{branch}' is not a valid git branch name (no spaces, leading '-', '..', trailing '.lock', …)");
    }

    /// <summary>The ordered checklist a <see cref="Claim"/> will work through, computed up front so a UI can
    /// show every row before work starts. Both modes cut the branch and reapply env/compose per repo; only a
    /// <b>fresh</b> claim adds the dependency-install (setup) rows.</summary>
    public IReadOnlyList<WorkspaceStep> PlanClaim(InstanceRecord record, bool fresh,
        IReadOnlyList<ResolvedRepo>? resolvedRepos = null)
    {
        var steps = new List<WorkspaceStep>();
        foreach (var repo in record.Repos)
        {
            var resolved = new ResolvedRepo(repo.Name, repo.SourcePath, ConfigFor(repo, resolvedRepos));
            steps.Add(new(RefreshStepIds.Claim(repo.Name), $"Create branch — {repo.Name}"));
            steps.Add(new(RefreshStepIds.Env(repo.Name), $"Apply environment — {repo.Name}"));
            if (resolved.Config.EffectiveModules.Any(m => m.Compose.Count > 0))
                steps.Add(new(RefreshStepIds.Compose(repo.Name), $"Generate compose — {repo.Name}"));
            if (fresh && HasSetup(resolved))
            {
                steps.Add(new(RefreshStepIds.Setup(repo.Name), $"Install dependencies — {repo.Name}"));
                foreach (var cmd in SetupCommands(resolved))
                    steps.Add(new(RefreshStepIds.SetupCommand(repo.Name, cmd.Index), cmd.Command) { SubStep = true });
            }
        }
        steps.Add(new(RefreshStepIds.Infra, fresh ? "Restart infrastructure" : "Start infrastructure"));
        return steps;
    }

    /// <summary>
    /// Claim a parked/reused workspace by cutting <paramref name="branch"/> across every repo — the
    /// load-bearing identity of the workspace, one branch name spanning the stack.
    /// <para>
    /// Pre-flight is atomic: the name is checked against every repo first (via <see cref="CheckClaim"/>) and
    /// <b>no</b> branch is created until all are clear, so a conflict never leaves a half-claim. A blocked
    /// name throws listing the repos; the caller decides what to do about it.
    /// </para>
    /// <para>
    /// Both modes cut the branch at <paramref name="startPoint"/> (default: the repo's base — <c>origin/main</c>)
    /// and reset the tracked tree to it, so the code is a clean, predictable checkout; gitignored artifacts
    /// (node_modules, docker volumes, real .env) always survive, and the workspace's previous branch is left as a
    /// ref (no commits lost). They differ only in the warm state: <b>keep</b> (<paramref name="fresh"/> =
    /// false) leaves volumes and installed deps as they are — the fast path; <b>fresh</b> reinstalls deps
    /// (setup) and wipes volumes for clean runtime data.
    /// </para>
    /// <para>
    /// TODO (circle back): <paramref name="startPoint"/> is a single ref applied to every repo. Decide
    /// whether to expose an <i>advanced</i> per-repo start point (a different branch per repo, e.g. from a
    /// remote), and how to resolve a ref that exists in some repos but not others. Not wired to CLI/UI yet —
    /// the picker (default main, else a chosen remote branch) is the immediate follow-up.
    /// </para>
    /// </summary>
    public InstanceRecord Claim(string workspace, string branch, bool fresh, bool force = false,
        IProgress<WorkspaceStepProgress>? progress = null, IReadOnlyList<ResolvedRepo>? resolvedRepos = null,
        string? startPoint = null)
    {
        _ = force; // reserved: no claim path currently discards commits (previous branch is retained)
        var record = instances.TryLoad(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");

        EnsureValidBranchName(branch);
        // Atomic pre-flight across the whole stack — create nothing until every repo is clear.
        ThrowIfBlocked(CheckClaim(record, branch), branch);

        var updatedRepos = new List<InstanceRepo>();
        foreach (var repo in record.Repos)
        {
            var config = ConfigFor(repo, resolvedRepos);
            var resolved = new ResolvedRepo(repo.Name, repo.SourcePath, config);

            // Cut the branch at the chosen start point and reset tracked files to it (clean, predictable
            // checkout). Cut from current HEAD first (always safe), then hard-reset — gitignored artifacts
            // are untouched, so the warm environment survives regardless of mode. A chosen start point that
            // doesn't exist in this repo falls back to the repo's base (noted on the row) — the single-ref
            // form: see the per-repo TODO above.
            progress?.Report(new(RefreshStepIds.Claim(repo.Name), WorkspaceStepState.Running));
            TryQuiet(() => git.Fetch(repo.WorktreePath));
            var chosen = startPoint is not null && git.RefExists(repo.WorktreePath, startPoint);
            var start = chosen ? startPoint! : git.ResolveDefaultBase(repo.WorktreePath);
            git.SwitchNewBranch(repo.WorktreePath, branch);
            git.ResetHard(repo.WorktreePath, start);
            progress?.Report(startPoint is not null && !chosen
                ? new(RefreshStepIds.Claim(repo.Name), WorkspaceStepState.Done, $"'{startPoint}' not in {repo.Name} — used {start}")
                : new(RefreshStepIds.Claim(repo.Name), WorkspaceStepState.Done));

            // Env + compose always reapply (cheap, and keep them aligned with the freshly-reset tree).
            progress?.Report(new(RefreshStepIds.Env(repo.Name), WorkspaceStepState.Running));
            ApplyEnvFor(repo, resolved, workspace);
            progress?.Report(new(RefreshStepIds.Env(repo.Name), WorkspaceStepState.Done));

            var composePaths = repo.ComposePaths;
            if (config.EffectiveModules.Any(m => m.Compose.Count > 0))
            {
                progress?.Report(new(RefreshStepIds.Compose(repo.Name), WorkspaceStepState.Running));
                composePaths = GenerateComposeFor(repo, resolved, workspace);
                progress?.Report(new(RefreshStepIds.Compose(repo.Name), WorkspaceStepState.Done));
            }

            // Setup (dependency install) runs only on fresh; keep trusts the warm node_modules already on
            // disk. On keep we carry the prior setup outcomes so a previously-degraded workspace stays flagged.
            var setupOutcomes = repo.Setup;
            if (fresh && HasSetup(resolved))
            {
                progress?.Report(new(RefreshStepIds.Setup(repo.Name), WorkspaceStepState.Running));
                setupOutcomes = RunSetup(resolved, repo.WorktreePath, progress);
                var failed = setupOutcomes.FirstOrDefault(o => !o.Success);
                progress?.Report(failed is null
                    ? new(RefreshStepIds.Setup(repo.Name), WorkspaceStepState.Done)
                    : new(RefreshStepIds.Setup(repo.Name), WorkspaceStepState.Warning, $"'{failed.Command}' exited {failed.ExitCode}"));
            }

            updatedRepos.Add(repo with { Branch = branch, GeneratedComposePaths = composePaths, Setup = setupOutcomes });
        }

        var claimed = record with { Repos = updatedRepos, Branch = branch, LastStatus = "claimed" };
        instances.Save(claimed);

        // Infra: fresh wipes volumes for clean runtime data; keep just starts what's there. Both tolerant —
        // a stopped Docker is a Warning on the infra row, not a crash.
        progress?.Report(new(RefreshStepIds.Infra, WorkspaceStepState.Running));
        if (fresh) TryStopInfra(workspace, removeVolumes: true);
        progress?.Report(InfraStartReport(TryStartInfra(workspace)));

        return instances.TryLoad(workspace) ?? claimed;
    }

    /// <summary>Cut the claim <paramref name="branch"/> on a <b>freshly-created</b> workspace and start its infra —
    /// the minimal claim for the new-workspace path. The workspace was just materialised by <see cref="Create"/>
    /// at base with env/compose/setup already done, so this only creates the branch (from the current base
    /// HEAD) and starts infra; it deliberately skips the reset/env/compose/setup that <see cref="Claim"/>
    /// does for a reused workspace, to avoid redoing work Create just finished. The branch pre-flight is the
    /// pool's responsibility here (it runs before Create so a bad name never materialises a workspace).</summary>
    public InstanceRecord CutBranchAndStart(string workspace, string branch,
        IProgress<WorkspaceStepProgress>? progress = null)
    {
        var record = instances.TryLoad(workspace)
            ?? throw new WorkspaceException($"unknown workspace '{workspace}'");

        var updatedRepos = new List<InstanceRepo>();
        foreach (var repo in record.Repos)
        {
            progress?.Report(new(RefreshStepIds.Claim(repo.Name), WorkspaceStepState.Running));
            git.SwitchNewBranch(repo.WorktreePath, branch); // workspace is parked at base — branch starts there
            progress?.Report(new(RefreshStepIds.Claim(repo.Name), WorkspaceStepState.Done));
            updatedRepos.Add(repo with { Branch = branch });
        }

        var claimed = record with { Repos = updatedRepos, Branch = branch, LastStatus = "claimed" };
        instances.Save(claimed);

        progress?.Report(new(RefreshStepIds.Infra, WorkspaceStepState.Running));
        progress?.Report(InfraStartReport(TryStartInfra(workspace)));
        return instances.TryLoad(workspace) ?? claimed;
    }

    /// <summary>Collect (do not act on) pending work across a workspace's repos — the report a release
    /// surfaces so nothing is silently stranded. Two categories per repo: an uncommitted working tree and
    /// commits not yet on any remote.</summary>
    public ReleaseReport CollectPending(InstanceRecord record)
        => new(record.Repos.Select(r => new RepoPending(
                r.Name,
                git.HasUncommittedChanges(r.WorktreePath),
                git.CountUnpushedCommits(r.WorktreePath)))
            .ToList());

    /// <summary>Fetch the given repos and gather the "start from" picker options: the resolved
    /// <see cref="StartPointOptions.Default"/> (what a null start point resolves to — the upstream-preferring
    /// base) and the union of every repo's candidate refs, ranked upstream → origin → other remotes → local.
    /// Best-effort per repo, so one repo with no remotes doesn't sink the list.</summary>
    public StartPointOptions StartPoints(IReadOnlyList<string> repoPaths, bool fetch)
    {
        var latest = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        var order = new List<string>();
        var currentBranches = new HashSet<string>(StringComparer.Ordinal);
        string? resolvedDefault = null;
        foreach (var path in repoPaths)
        {
            // The slow bit — skipped for the instant local read; the caller does a background fetch pass to
            // refresh. (Correctness is unaffected: Create/Claim fetch again before actually cutting the branch.)
            if (fetch) TryQuiet(() => git.Fetch(path));
            if (resolvedDefault is null)
                try { resolvedDefault = git.ResolveDefaultBase(path); } catch { /* no base here; try the next repo */ }
            if (git.CurrentBranch(path) is { } cur) currentBranches.Add(cur);
            foreach (var b in git.ListStartPointCandidates(path))
            {
                if (!latest.TryGetValue(b.Name, out var date)) { order.Add(b.Name); latest[b.Name] = b.LastCommit; }
                else if (b.LastCommit > date) latest[b.Name] = b.LastCommit; // newest tip across repos
            }
        }

        // Order so the likely picks lead: where you are now, then the default-ish main/master, then most
        // recently active. A picker showing "recent" with no search text gets the useful few from the top.
        var choices = order
            .Select(name => new StartPointChoice(name, latest[name], IsDefaultBranchName(name), currentBranches.Contains(name)))
            .OrderByDescending(c => c.IsCurrent)
            .ThenByDescending(c => c.IsDefaultBranch)
            .ThenByDescending(c => c.LastCommit ?? DateTimeOffset.MinValue)
            .ToList();
        return new StartPointOptions(resolvedDefault, choices);
    }

    /// <summary>Raw input for the branch-graph view of one repo: its recent commits (newest first, capped at
    /// <paramref name="limit"/>) and the currently checked-out branch (null when detached, for the "current"
    /// highlight). The caller lays it out (<see cref="Graph.CommitGraphLayout"/>).</summary>
    public (IReadOnlyList<GraphCommit> Commits, string? CurrentBranch) CommitGraphData(string repoPath, int limit)
        => (git.ListCommitGraph(repoPath, limit), git.CurrentBranch(repoPath));

    // A main/master branch on any remote (or local) — the "most likely what you want" chip.
    static bool IsDefaultBranchName(string reference)
    {
        var tail = reference.Contains('/') ? reference[(reference.LastIndexOf('/') + 1)..] : reference;
        return tail is "main" or "master";
    }
}

/// <summary>One start-point option for the picker: the ref, its tip-commit date (for recency), and whether
/// it's a likely default (main/master) or the repo's current branch — the two chips the picker highlights.</summary>
public sealed record StartPointChoice(string Ref, DateTimeOffset? LastCommit, bool IsDefaultBranch, bool IsCurrent);

/// <summary>Options for the "start from" picker: the default ref a null start point resolves to (may be null
/// if no base could be found), and the ranked candidate refs to branch from.</summary>
public sealed record StartPointOptions(string? Default, IReadOnlyList<StartPointChoice> Candidates);

/// <summary>Pure filter behind the picker's list. With no search text it shows the most-relevant few (the
/// pre-ordered current → default → recent leaders), capped at <paramref name="recentLimit"/>; with search
/// text it returns every candidate whose ref contains it (case-insensitive), uncapped — so a specific branch
/// is always findable even when it's not "recent".</summary>
public static class StartPointFilter
{
    public static IReadOnlyList<StartPointChoice> Apply(IReadOnlyList<StartPointChoice> all, string? search, int recentLimit)
    {
        if (string.IsNullOrWhiteSpace(search))
            return recentLimit > 0 && all.Count > recentLimit ? all.Take(recentLimit).ToList() : all;
        var q = search.Trim();
        return all.Where(c => c.Ref.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
