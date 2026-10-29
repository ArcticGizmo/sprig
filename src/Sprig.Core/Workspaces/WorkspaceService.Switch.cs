using Sprig.Core.Store;

namespace Sprig.Core.Workspaces;

/// <summary>Where <see cref="WorkspaceService.Switch"/> moves a workspace's worktrees.</summary>
public sealed record SwitchTarget
{
    public enum Kind { NewBranch, ExistingBranch, Detach }

    public Kind Mode { get; private init; }
    /// <summary>The branch to cut or check out; null when detaching.</summary>
    public string? Branch { get; private init; }
    /// <summary>The start point for a new branch, or the ref to detach at; null means each repo's base.</summary>
    public string? Ref { get; private init; }

    public static SwitchTarget NewBranch(string branch, string? from = null) => new() { Mode = Kind.NewBranch, Branch = branch, Ref = from };
    public static SwitchTarget ExistingBranch(string branch) => new() { Mode = Kind.ExistingBranch, Branch = branch };
    public static SwitchTarget Detach(string? reference = null) => new() { Mode = Kind.Detach, Ref = reference };
}

/// <summary>A finished switch: the updated record, and the unpushed commits left on each repo's previous
/// branch — reported so nothing is forgotten, but never a reason to refuse (the branch keeps them).</summary>
public sealed record SwitchResult(InstanceRecord Record, ReleaseReport Previous);

/// <summary>
/// Moving a workspace's git state in place — the in-place sibling of <see cref="RefreshToBase"/>. Sprig can't
/// tell whether a PR has merged or is only queued, so moving a worktree on and tidying up the old branch are
/// separate jobs: <c>switch</c> moves the worktree straight away and never deletes, resets or pushes the branch
/// it leaves. The slot, ports, label, note and tags are untouched.
/// </summary>
public sealed partial class WorkspaceService
{
    /// <summary>The ordered checklist <see cref="Switch"/> works through. Infra only appears for a fresh
    /// switch; otherwise it is left running.</summary>
    public IReadOnlyList<WorkspaceStep> PlanSwitch(InstanceRecord record, SwitchTarget target,
        IReadOnlyList<string>? onlyRepos = null, bool fresh = false, IReadOnlyList<ResolvedRepo>? resolvedRepos = null)
    {
        var steps = new List<WorkspaceStep>();
        foreach (var repo in SelectRefreshRepos(record, onlyRepos))
        {
            var resolved = new ResolvedRepo(repo.Name, repo.SourcePath, ConfigFor(repo, resolvedRepos));
            steps.Add(new(RefreshStepIds.Claim(repo.Name), target.Mode switch
            {
                SwitchTarget.Kind.NewBranch => $"Create branch {target.Branch} — {repo.Name}",
                SwitchTarget.Kind.ExistingBranch => $"Check out {target.Branch} — {repo.Name}",
                _ => $"Detach at {target.Ref ?? "base"} — {repo.Name}",
            }));
            steps.Add(new(RefreshStepIds.Env(repo.Name), $"Apply environment — {repo.Name}"));
            if (resolved.Config.EffectiveModules.Any(m => m.Compose.Count > 0))
                steps.Add(new(RefreshStepIds.Compose(repo.Name), $"Generate compose — {repo.Name}"));
            if (HasSetup(resolved))
            {
                steps.Add(new(RefreshStepIds.Setup(repo.Name), $"Install dependencies — {repo.Name}"));
                foreach (var cmd in SetupCommands(resolved))
                    steps.Add(new(RefreshStepIds.SetupCommand(repo.Name, cmd.Index), cmd.Command) { SubStep = true });
            }
        }
        if (fresh) steps.Add(new(RefreshStepIds.Infra, "Recreate infrastructure (volumes wiped)"));
        return steps;
    }

    /// <summary>
    /// Move the workspace's worktrees (all, or <paramref name="onlyRepos"/>) to <paramref name="target"/>, then
    /// reapply env and compose and rerun setup. Everything that could block is checked across every repo
    /// before any worktree moves: uncommitted changes, a branch name that's taken (new) or missing (existing),
    /// a branch another worktree already holds, and a ref that doesn't exist. <paramref name="fresh"/> also
    /// wipes the data volumes and recreates infra; otherwise infra is left running.
    /// </summary>
    public SwitchResult Switch(string workspace, SwitchTarget target, IReadOnlyList<string>? onlyRepos = null,
        bool fresh = false, IProgress<WorkspaceStepProgress>? progress = null, IReadOnlyList<ResolvedRepo>? resolvedRepos = null)
    {
        var record = instances.TryLoad(workspace) ?? throw new WorkspaceException($"unknown workspace '{workspace}'");
        var targets = SelectRefreshRepos(record, onlyRepos);
        if (target.Branch is { } name) EnsureValidBranchName(name);

        var dirty = targets.Where(r => git.HasUncommittedChanges(r.WorktreePath)).Select(r => r.Name).ToList();
        if (dirty.Count > 0)
            throw new WorkspaceException(
                $"refusing to switch — uncommitted changes in: {string.Join(", ", dirty)}. " +
                "Commit or stash them first (see 'git status' in each).");

        foreach (var repo in targets) TryQuiet(() => git.Fetch(repo.WorktreePath));
        var previous = new ReleaseReport(targets.Select(r => new RepoPending(r.Name, false, git.CountUnpushedCommits(r.WorktreePath))).ToList());
        var refs = PreflightSwitch(record, targets, target);

        var targetNames = targets.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var updatedRepos = new List<InstanceRepo>();
        foreach (var repo in record.Repos)
        {
            if (!targetNames.Contains(repo.Name)) { updatedRepos.Add(repo); continue; }
            var config = ConfigFor(repo, resolvedRepos);
            var resolved = new ResolvedRepo(repo.Name, repo.SourcePath, config);

            progress?.Report(new(RefreshStepIds.Claim(repo.Name), WorkspaceStepState.Running));
            switch (target.Mode)
            {
                case SwitchTarget.Kind.NewBranch: git.SwitchNewBranch(repo.WorktreePath, target.Branch!, refs[repo.Name]); break;
                case SwitchTarget.Kind.ExistingBranch: git.SwitchToBranch(repo.WorktreePath, target.Branch!); break;
                default: git.DetachTo(repo.WorktreePath, refs[repo.Name]); break;
            }
            progress?.Report(new(RefreshStepIds.Claim(repo.Name), WorkspaceStepState.Done));

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

            var setupOutcomes = repo.Setup;
            if (HasSetup(resolved))
            {
                progress?.Report(new(RefreshStepIds.Setup(repo.Name), WorkspaceStepState.Running));
                setupOutcomes = RunSetup(resolved, repo.WorktreePath, progress);
                var failed = setupOutcomes.FirstOrDefault(o => !o.Success);
                progress?.Report(failed is null
                    ? new(RefreshStepIds.Setup(repo.Name), WorkspaceStepState.Done)
                    : new(RefreshStepIds.Setup(repo.Name), WorkspaceStepState.Warning, $"'{failed.Command}' exited {failed.ExitCode}"));
            }

            updatedRepos.Add(repo with { Branch = target.Branch, GeneratedComposePaths = composePaths, Setup = setupOutcomes });
        }

        // The claim branch follows the switch, so release reports unpushed work on the branch now in use.
        var switched = record with { Repos = updatedRepos, Branch = target.Branch };
        instances.Save(switched);

        if (fresh)
        {
            progress?.Report(new(RefreshStepIds.Infra, WorkspaceStepState.Running));
            TryStopInfra(workspace, removeVolumes: true);
            progress?.Report(InfraStartReport(TryStartInfra(workspace)));
        }

        return new SwitchResult(instances.TryLoad(workspace) ?? switched, previous);
    }

    // Check every repo can make the move before any of them does, and resolve the ref each one starts from or
    // detaches at (repo name → ref). Throws one error naming every repo that can't.
    Dictionary<string, string> PreflightSwitch(InstanceRecord record, IReadOnlyList<InstanceRepo> targets, SwitchTarget target)
    {
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var repo in targets)
        {
            switch (target.Mode)
            {
                case SwitchTarget.Kind.NewBranch:
                    if (git.BranchExists(repo.WorktreePath, target.Branch!))
                        problems.Add($"{repo.Name}: branch '{target.Branch}' already exists — use --branch to check it out");
                    break;
                case SwitchTarget.Kind.ExistingBranch:
                    if (!git.BranchExists(repo.WorktreePath, target.Branch!) && !git.RemoteBranchExists(repo.WorktreePath, target.Branch!))
                        problems.Add($"{repo.Name}: no branch '{target.Branch}' locally or on any remote");
                    else if (HolderOf(record, repo, target.Branch!) is { } holder)
                        problems.Add($"{repo.Name}: '{target.Branch}' is already checked out in {holder}");
                    break;
            }

            if (target.Mode == SwitchTarget.Kind.ExistingBranch) continue;
            var reference = target.Ref ?? git.ResolveDefaultBase(repo.WorktreePath);
            if (target.Ref is not null && !git.RefExists(repo.WorktreePath, reference))
                problems.Add($"{repo.Name}: no ref '{reference}'");
            refs[repo.Name] = reference;
        }

        if (problems.Count > 0)
            throw new WorkspaceException("refusing to switch — nothing was changed:\n  " + string.Join("\n  ", problems));
        return refs;
    }

    // The other worktree (named by its workspace when Sprig knows it, else by path) that has `branch` checked
    // out — git allows a branch in only one worktree at a time. Null when none does, or when it is this one.
    string? HolderOf(InstanceRecord record, InstanceRepo repo, string branch)
    {
        var self = Normalise(repo.WorktreePath);
        var holder = git.ListWorktrees(repo.SourcePath)
            .FirstOrDefault(w => w.Branch == branch && !string.Equals(Normalise(w.Path), self, PathComparison));
        if (holder is null) return null;

        var path = Normalise(holder.Path);
        var owner = instances.LoadAll().FirstOrDefault(i =>
            i.Workspace != record.Workspace && i.Repos.Any(r => string.Equals(Normalise(r.WorktreePath), path, PathComparison)));
        return owner is not null ? $"workspace '{owner.Workspace}'" : holder.Path;
    }
}
