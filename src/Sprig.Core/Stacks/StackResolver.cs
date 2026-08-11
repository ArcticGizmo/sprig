using Sprig.Core.Config;
using Sprig.Core.Git;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Stacks;

/// <summary>Turns a named stack into a <see cref="ResolvedStack"/> by resolving each repo via the registry + git.</summary>
public sealed class StackResolver(RepoRegistryStore registry, StackStore stacks, IGitService git)
{
    /// <summary>
    /// Resolve a stack, optionally as a <i>partial</i> workspace: pass the subset of repo names to
    /// keep in <paramref name="selectedRepos"/> (<c>null</c> means all of them; an empty selection is
    /// refused rather than treated as "all", so a mis-built subset can't silently create everything).
    /// Deselected
    /// repos are dropped before anything is read from disk, and any stack port left orphaned by that
    /// choice — referenced only by the repos being dropped — is removed from the ports to provision.
    /// </summary>
    public ResolvedStack Resolve(string stackName, IEnumerable<string>? selectedRepos = null)
    {
        var stack = stacks.Get(stackName)
            ?? throw new StackException($"unknown stack '{stackName}'");

        var included = StackSelection.Include(stack, selectedRepos);
        var keep = new HashSet<string>(included, StringComparer.Ordinal);
        var excluded = stack.Repos.Where(r => !keep.Contains(r)).ToList();
        var skippedPorts = StackSelection.OrphanedPorts(stack, included);

        var repos = new List<ResolvedRepo>();
        foreach (var name in included)
        {
            var reg = registry.Get(name)
                ?? throw new StackException($"stack '{stackName}' references unregistered repo '{name}'");
            if (!git.IsGitRepo(reg.Path))
                throw new StackException($"repo '{name}' at {reg.Path} is not a git repository");

            var root = git.ResolveRepoRoot(reg.Path);
            var config = SprigConfigLoader.LoadFromFile(Path.Combine(root, ".sprig.json"));
            var validation = SprigConfigValidator.Validate(config);
            if (!validation.IsValid)
                throw new StackException(
                    $"invalid .sprig.json for '{name}':\n  " + string.Join("\n  ", validation.Issues));

            repos.Add(new ResolvedRepo(config.Name, root, FoldStackSetup(config, stack, name)));
        }

        // Hand create only what it should materialise: the kept repos, their bindings, and the ports
        // that still have a consumer.
        var skipped = new HashSet<string>(skippedPorts, StringComparer.Ordinal);
        var ports = skipped.Count == 0 ? stack.Ports : stack.Ports.Where(p => !skipped.Contains(p)).ToList();
        var bindings = excluded.Count == 0
            ? stack.Bindings
            : stack.Bindings.Where(b => keep.Contains(b.Key))
                .ToDictionary(b => b.Key, b => b.Value, StringComparer.Ordinal);

        return new ResolvedStack(stackName, repos, ports, bindings)
        {
            ExcludedRepos = excluded,
            SkippedPorts = skippedPorts,
        };
    }

    /// <summary>
    /// Fold any stack-supplied setup for this repo into its config as an extra module (named
    /// <c>stack</c>) that runs after the repo's own. Makes the stack a complete block: a repo with a
    /// name-only <c>.sprig.json</c> gets its whole stand-up from the stack. Returns the config unchanged
    /// when the stack supplies no setup for it.
    /// </summary>
    static SprigRepoConfig FoldStackSetup(SprigRepoConfig config, StackDefinition stack, string repoName)
    {
        if (!stack.Setup.TryGetValue(repoName, out var stackSetup) || stackSetup.Count == 0)
            return config;

        // Materialise the repo's own modules, append the stack's, and clear the legacy flat fields so
        // EffectiveModules returns exactly this list (mirrors how module-narrowing rebuilds a config).
        var modules = config.EffectiveModules.ToList();
        modules.Add(new ModuleDeclaration { Name = "stack", Setup = stackSetup });
        return config with { Modules = modules, Env = null, Compose = null, Setup = null };
    }
}
