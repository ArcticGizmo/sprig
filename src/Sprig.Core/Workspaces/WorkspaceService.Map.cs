using Sprig.Core.Maps;
using Sprig.Core.Store;

namespace Sprig.Core.Workspaces;

/// <summary>
/// The map-model create path (the Graph Turn) — the one create path now that stacks are retired. It resolves
/// the selection with <see cref="CapabilityResolver"/> — per-module capability scopes from the repos' own
/// provides/needs — then materialises worktree, env, compose and setup.
/// </summary>
public sealed partial class WorkspaceService
{
    /// <summary>Virtual repos let one checkout be registered under several configs, but a workspace's worktree
    /// path comes from the checkout's folder, so two of them in one workspace would land on the same folder.</summary>
    static void RejectSharedCheckouts(IReadOnlyList<ResolvedRepo> repos)
    {
        var shared = repos
            .GroupBy(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r.Root)), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (shared is not null)
            throw new WorkspaceException(
                $"{string.Join(" and ", shared.Select(r => $"'{r.Name}'"))} are the same checkout ({shared.Key}) — " +
                "a workspace can hold only one config of a repo");
    }

    /// <summary>The ordered checklist <see cref="CreateFromMap"/> works through, computed up front so a UI/CLI
    /// can show every row before execution starts. Env + compose share one "Apply environment" step per repo
    /// (there is no separate compose row). Runs the same cheap pre-flight (name + duplicate) so a bad name
    /// fails here, not mid-run.</summary>
    public IReadOnlyList<WorkspaceStep> PlanCreateFromMap(IReadOnlyList<ResolvedRepo> repos, string workspace)
    {
        ValidateName(workspace);
        if (repos.Count == 0)
            throw new WorkspaceException("nothing to create: no repos selected");
        if (instances.TryLoad(workspace) is not null)
            throw new WorkspaceException($"workspace '{workspace}' already exists");
        RejectSharedCheckouts(repos);

        var steps = new List<WorkspaceStep> { new(CreateStepIds.Ports, "Allocate ports") };
        foreach (var repo in repos)
        {
            steps.Add(new(CreateStepIds.Worktree(repo.Name), $"Create worktree — {repo.Name}"));
            steps.Add(new(CreateStepIds.Env(repo.Name), $"Apply environment — {repo.Name}"));
            if (HasSetup(repo))
            {
                steps.Add(new(CreateStepIds.Setup(repo.Name), $"Install dependencies — {repo.Name}"));
                foreach (var cmd in SetupCommands(repo))
                    steps.Add(new(CreateStepIds.SetupCommand(repo.Name, cmd.Index), cmd.Command) { SubStep = true });
            }
        }
        steps.Add(new(CreateStepIds.Record, "Save workspace record"));
        return steps;
    }

    /// <summary>
    /// Create an isolated workspace from a map and a selected repo set. Rolls back on failure. An unmet
    /// need (no provider in the selection, no inline literal, no map default) is a hard failure with the
    /// gap list — <b>materialise nothing</b> rather than a half-wired workspace.
    /// </summary>
    /// <param name="map">The map (for wiring/defaults); null resolves purely from the repos' provides/needs.</param>
    /// <param name="repos">The selected repos, already resolved to path + config (the caller applies the map's selection).</param>
    /// <param name="inlineLiterals">Optional per-checkout fallbacks (<c>[repo][capability][output] = literal</c>).</param>
    public InstanceRecord CreateFromMap(
        string workspace,
        MapDefinition? map,
        IReadOnlyList<ResolvedRepo> repos,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>>? inlineLiterals = null,
        IProgress<WorkspaceStepProgress>? progress = null,
        string? startPoint = null)
    {
        ValidateName(workspace);
        if (repos.Count == 0)
            throw new WorkspaceException("nothing to create: no repos selected");
        if (instances.TryLoad(workspace) is not null)
            throw new WorkspaceException($"workspace '{workspace}' already exists");
        RejectSharedCheckouts(repos);

        // Pre-compute each repo's worktree (and link) path and guard against collisions before anything is made.
        var layout = Layout;
        var plans = new List<RepoPlan>();
        foreach (var repo in repos)
        {
            var (worktree, link) = WorktreePaths.For(repo.Root, workspace, layout);
            if (DirectoryLink.Occupied(worktree))
                throw new WorkspaceException($"worktree path already exists: {worktree}");
            if (link is not null && DirectoryLink.Occupied(link))
                throw new WorkspaceException($"path for the worktree's shortcut already exists: {link}");
            plans.Add(new RepoPlan(repo, worktree, link));
        }

        var portsAcquired = false;
        var addedWorktrees = new List<(string root, string worktree, string? link)>();
        var current = CreateStepIds.Ports;
        try
        {
            progress?.Report(new(CreateStepIds.Ports, WorkspaceStepState.Running));
            var allPorts = ports.Acquire(workspace, CapabilityResolver.PortRequests(repos));
            portsAcquired = true;

            var resolved = CapabilityResolver.Resolve(workspace, map, repos, allPorts, inlineLiterals);
            if (resolved.Unsatisfied.Count > 0)
                throw new WorkspaceException(
                    "unmet needs — add the provider to your selection or supply a value:\n  " +
                    string.Join("\n  ", resolved.Unsatisfied.Select(u => $"{u.Repo}.{u.Module} needs '{u.Capability}'")));
            progress?.Report(new(CreateStepIds.Ports, WorkspaceStepState.Done));

            var modulesByRepo = resolved.Modules.ToLookup(m => m.Repo, StringComparer.Ordinal);

            var repoRecords = new List<InstanceRepo>();
            foreach (var plan in plans)
            {
                var repo = plan.Repo;

                // Park detached at the start point (same as the stack path — identity attaches at claim).
                current = CreateStepIds.Worktree(repo.Name);
                progress?.Report(new(current, WorkspaceStepState.Running));
                TryQuiet(() => git.Fetch(repo.Root));
                var start = startPoint is not null && git.RefExists(repo.Root, startPoint)
                    ? startPoint
                    : git.ResolveDefaultBase(repo.Root);
                git.AddWorktreeDetached(repo.Root, plan.Worktree, start);
                addedWorktrees.Add((repo.Root, plan.Worktree, plan.Link));
                var linkProblem = plan.Link is null ? null : TryLinkWorktree(repo.Root, plan.Worktree, plan.Link);
                var linkPath = linkProblem is null ? plan.Link : null;
                progress?.Report(linkProblem is null
                    ? new(current, WorkspaceStepState.Done)
                    : new(current, WorkspaceStepState.Warning, linkProblem));

                // Env + compose per module, each against its own capability scope. The scope's concrete
                // values are recorded (InstanceModule) so claim/refresh can rebuild them without the map.
                current = CreateStepIds.Env(repo.Name);
                progress?.Report(new(current, WorkspaceStepState.Running));
                var composePaths = new List<string>();
                var moduleScopes = new List<InstanceModule>();
                foreach (var rm in modulesByRepo[repo.Name])
                {
                    env.ApplyModule(rm.Declaration, repo.Root, plan.Worktree, rm.Scope);
                    foreach (var composeCfg in rm.Declaration.Compose)
                    {
                        var dest = Path.Combine(paths.InstanceDir(workspace),
                            $"docker-compose.{repo.Name}.{rm.Module}.{ComposeSlug(composeCfg.File)}.sprig.yml");
                        compose.GenerateToFile(
                            Path.Combine(repo.Root, rm.Path, composeCfg.File), composeCfg, rm.Scope, dest);
                        composePaths.Add(dest);
                    }
                    moduleScopes.Add(new InstanceModule { Name = rm.Module, Path = rm.Path, Values = rm.Values });
                }
                progress?.Report(new(current, WorkspaceStepState.Done));

                // Setup is scope-independent (literal commands per module dir) — reuse the stack path's runner.
                IReadOnlyList<Setup.SetupOutcome> setupOutcomes = [];
                if (HasSetup(repo))
                {
                    var setupStep = CreateStepIds.Setup(repo.Name);
                    progress?.Report(new(setupStep, WorkspaceStepState.Running));
                    setupOutcomes = RunSetup(repo, plan.Worktree, progress);
                    var failed = setupOutcomes.FirstOrDefault(o => !o.Success);
                    progress?.Report(failed is null
                        ? new(setupStep, WorkspaceStepState.Done)
                        : new(setupStep, WorkspaceStepState.Warning, $"'{failed.Command}' exited {failed.ExitCode} — worktree kept"));
                }

                repoRecords.Add(new InstanceRepo
                {
                    Name = repo.Name,
                    SourcePath = repo.Root,
                    WorktreePath = plan.Worktree,
                    VirtualConfigPath = repo.VirtualConfigPath,
                    LinkPath = linkPath,
                    Branch = null,
                    GeneratedComposePaths = composePaths,
                    Modules = moduleScopes,
                    Setup = setupOutcomes,
                });
            }

            current = CreateStepIds.Record;
            progress?.Report(new(current, WorkspaceStepState.Running));
            var record = new InstanceRecord
            {
                Workspace = workspace,
                Map = map?.Name,
                SelectedRepos = repos.Select(r => r.Name).ToList(),
                Repos = repoRecords,
                Ports = new Dictionary<string, int>(allPorts),
                LastStatus = "created",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            instances.Save(record);
            progress?.Report(new(current, WorkspaceStepState.Done));
            return record;
        }
        catch (Exception ex)
        {
            progress?.Report(new(current, WorkspaceStepState.Error, ex.Message));
            foreach (var (root, worktree, link) in addedWorktrees)
            {
                if (link is not null) TryQuiet(() => DirectoryLink.Remove(link));
                TryQuiet(() => git.RemoveWorktree(root, worktree));
                WorktreeInspector.TryDeleteDirectory(worktree);
            }
            if (portsAcquired) TryQuiet(() => ports.Release(workspace));
            TryQuiet(() => instances.Delete(workspace));
            throw;
        }
    }
}
