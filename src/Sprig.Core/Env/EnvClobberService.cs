using System.Text;
using Sprig.Core.Config;
using Sprig.Core.Substitution;

namespace Sprig.Core.Env;

/// <summary>
/// Writes sprig's env overrides into a worktree's <c>.env.*</c> files. For each targeted file
/// it seeds from the source repo's copy, then wraps that content in an identical marker block
/// at the <b>top and bottom</b> so the override wins under both first-wins and last-wins loaders
/// (see docs/spike-findings.md S1). Only files named in config are touched; the source repo is
/// never written.
/// </summary>
public sealed class EnvClobberService
{
    public const string BeginMarker = "# >>> sprig >>>";
    public const string EndMarker = "# <<< sprig <<<";

    /// <summary>Apply every module's env overrides against one shared <paramref name="scope"/> (the stack
    /// model, where inputs are repo-level). Returns the worktree file paths written.</summary>
    public IReadOnlyList<string> Apply(SprigRepoConfig config, string sourceRepo, string worktree, IVariableSource scope)
    {
        var written = new List<string>();
        foreach (var module in config.EffectiveModules)
            written.AddRange(ApplyModule(module, sourceRepo, worktree, scope));
        return written;
    }

    /// <summary>Apply a single module's env overrides against <paramref name="scope"/> — the map model, where
    /// each module resolves against its own capability scope. Files and template seeds resolve under the
    /// module's path.</summary>
    public IReadOnlyList<string> ApplyModule(ModuleDeclaration module, string sourceRepo, string worktree, IVariableSource scope)
    {
        var written = new List<string>();
        foreach (var over in module.Env)
        {
            var resolved = Resolve(over, scope);
            var block = RenderBlock(resolved);

            var seed = SeedFor(over, sourceRepo, module.Path);

            var target = Path.Combine(worktree, module.Path, over.File);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, Wrap(seed, block));
            written.Add(target);
        }
        return written;
    }

    /// <summary>Remove sprig blocks from the targeted files in the worktree (best-effort).</summary>
    public void Strip(SprigRepoConfig config, string worktree)
    {
        foreach (var module in config.EffectiveModules)
            foreach (var over in module.Env)
            {
                var target = Path.Combine(worktree, module.Path, over.File);
                if (File.Exists(target))
                    File.WriteAllText(target, StripBlocks(File.ReadAllText(target)));
            }
    }

    /// <summary>
    /// The seed content for an override's target file: the real target file merged with the declared
    /// <see cref="EnvOverride.Templates"/>, in <b>precedence order</b> — the target file first (on a
    /// working machine its gitignored copy holds the actual values you want carried into the worktree),
    /// then each template in turn. Every source is block-stripped; a key is taken from the first source
    /// that defines it, so a lower-precedence source only contributes keys not already present — a
    /// template never overrides a value the target file (or an earlier template) already gave. Missing
    /// sources are skipped; non-assignment lines (comments/blanks) are kept in order. Empty when nothing
    /// is available. (The sprig override block still wraps the result, so anything sprig itself sets in
    /// <see cref="EnvOverride.Set"/> wins over every seeded value regardless.)
    /// </summary>
    static string SeedFor(EnvOverride over, string sourceRepo, string basePath)
    {
        // Highest precedence first: the real target file, then each declared template in order.
        var sources = new List<string> { over.File };
        if (over.Templates is { Count: > 0 } templates)
            sources.AddRange(templates.Where(t => !string.IsNullOrWhiteSpace(t)));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();
        foreach (var rel in sources)
        {
            var abs = Path.Combine(sourceRepo, basePath, rel);
            if (!File.Exists(abs)) continue;
            foreach (var line in StripBlocks(File.ReadAllText(abs)).Replace("\r\n", "\n").Split('\n'))
            {
                // An assignment whose key a higher-precedence source already provided is dropped; unique
                // keys, comments and blanks are kept in order.
                if (EnvKey(line) is { } key && !seen.Add(key)) continue;
                kept.Add(line);
            }
        }
        return string.Join('\n', kept);
    }

    /// <summary>The key of an env assignment line (leading/trailing whitespace ignored), or null when the
    /// line is a comment or blank / carries no <c>KEY=</c>. Mirrors the tolerant parse the rest of the
    /// codebase uses, so dedup keys line up with how these files are read elsewhere.</summary>
    internal static string? EnvKey(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return null;
        var eq = trimmed.IndexOf('=');
        return eq <= 0 ? null : trimmed[..eq].Trim();
    }

    static SortedDictionary<string, string> Resolve(EnvOverride over, IVariableSource scope)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, template) in over.Set)
            result[key] = SubstitutionEngine.Resolve(template, scope);
        return result;
    }

    internal static string RenderBlock(IReadOnlyDictionary<string, string> resolved)
    {
        var sb = new StringBuilder();
        sb.Append(BeginMarker).Append('\n');
        foreach (var (key, value) in resolved)
            sb.Append(key).Append('=').Append(value).Append('\n');
        sb.Append(EndMarker).Append('\n');
        return sb.ToString();
    }

    internal static string Wrap(string seed, string block)
    {
        var sb = new StringBuilder();
        sb.Append(block);
        if (seed.Length > 0)
        {
            sb.Append(seed);
            if (!seed.EndsWith('\n')) sb.Append('\n');
        }
        sb.Append(block);
        return sb.ToString();
    }

    /// <summary>Remove every <c>&gt;&gt;&gt; sprig &gt;&gt;&gt;</c> … <c>&lt;&lt;&lt; sprig &lt;&lt;&lt;</c> region (inclusive).</summary>
    internal static string StripBlocks(string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        var inside = false;
        foreach (var line in lines)
        {
            if (!inside && line.TrimEnd() == BeginMarker) { inside = true; continue; }
            if (inside)
            {
                if (line.TrimEnd() == EndMarker) inside = false;
                continue;
            }
            kept.Add(line);
        }
        // Join reproduces the original line structure (incl. a trailing newline) for the kept lines.
        return string.Join('\n', kept);
    }
}
