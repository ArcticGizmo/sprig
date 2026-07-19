using Sprig.Core.Config;
using Sprig.Core.Substitution;

namespace Sprig.Core.Stacks;

/// <summary>The per-repo variable scopes for a workspace, plus the resolved cross-repo provides.</summary>
public sealed class StackScope(
    IReadOnlyDictionary<string, string> provides,
    IReadOnlyDictionary<string, IVariableSource> perRepo)
{
    /// <summary>Resolved provides, keyed <c>&lt;repo&gt;.&lt;key&gt;</c>.</summary>
    public IReadOnlyDictionary<string, string> Provides { get; } = provides;

    /// <summary>The <see cref="IVariableSource"/> a given repo's env/compose templates resolve against.</summary>
    public IVariableSource For(string repo) => perRepo[repo];
}

/// <summary>
/// Builds per-repo scopes for a multi-repo workspace in two phases:
/// (1) resolve each repo's <c>provides</c> against its own ports → a global provides map;
/// (2) give every repo a scope of <c>workspace</c> + its own local ports + all provides + stack vars.
/// Same-named ports in different repos never collide because each repo only sees its own.
/// </summary>
public static class StackScopeBuilder
{
    public static StackScope Build(
        string workspace,
        IReadOnlyList<(string repo, SprigRepoConfig config)> repos,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> portsByRepo,
        IReadOnlyDictionary<string, string>? stackVars = null)
    {
        // Phase 1 — resolve each repo's provides against its own ports (+ workspace).
        var provides = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (repo, config) in repos)
        {
            var selfScope = SprigScope.ForWorkspace(workspace, portsByRepo[repo]);
            foreach (var (key, template) in config.Provides)
                provides[$"{repo}.{key}"] = SubstitutionEngine.Resolve(template, selfScope);
        }

        // Phase 2 — full scope per repo: own ports + all provides + stack vars.
        var perRepo = new Dictionary<string, IVariableSource>(StringComparer.Ordinal);
        foreach (var (repo, config) in repos)
        {
            _ = config;
            perRepo[repo] = SprigScope.ForWorkspace(workspace, portsByRepo[repo], stackVars, provides);
        }

        return new StackScope(provides, perRepo);
    }
}
