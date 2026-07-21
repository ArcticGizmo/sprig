using Sprig.Core.Config;
using Sprig.Core.Git;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Stacks;

/// <summary>Turns a named stack into a <see cref="ResolvedStack"/> by resolving each repo via the registry + git.</summary>
public sealed class StackResolver(RepoRegistryStore registry, StackStore stacks, IGitService git)
{
    public ResolvedStack Resolve(string stackName)
    {
        var stack = stacks.Get(stackName)
            ?? throw new StackException($"unknown stack '{stackName}'");

        var repos = new List<ResolvedRepo>();
        foreach (var name in stack.Repos)
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

            repos.Add(new ResolvedRepo(config.Name, root, config));
        }

        return new ResolvedStack(stackName, repos, stack.Ports, stack.Bindings);
    }
}
