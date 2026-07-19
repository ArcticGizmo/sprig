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

    /// <summary>Apply every env override; returns the worktree file paths written.</summary>
    public IReadOnlyList<string> Apply(SprigRepoConfig config, string sourceRepo, string worktree, IVariableSource scope)
    {
        var written = new List<string>();
        foreach (var over in config.Env)
        {
            var resolved = Resolve(over, scope);
            var block = RenderBlock(resolved);

            var sourceFile = Path.Combine(sourceRepo, over.File);
            var seed = File.Exists(sourceFile) ? StripBlocks(File.ReadAllText(sourceFile)) : "";

            var target = Path.Combine(worktree, over.File);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, Wrap(seed, block));
            written.Add(target);
        }
        return written;
    }

    /// <summary>Remove sprig blocks from the targeted files in the worktree (best-effort).</summary>
    public void Strip(SprigRepoConfig config, string worktree)
    {
        foreach (var over in config.Env)
        {
            var target = Path.Combine(worktree, over.File);
            if (File.Exists(target))
                File.WriteAllText(target, StripBlocks(File.ReadAllText(target)));
        }
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
