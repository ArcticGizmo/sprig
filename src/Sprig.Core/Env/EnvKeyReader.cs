using System.Text.RegularExpressions;

namespace Sprig.Core.Env;

/// <summary>
/// Reads the variable <em>names</em> declared in <c>.env</c>-style files, to drive key autosuggest
/// when someone overrides an env file. Keys come from the target file itself plus the conventional
/// "template" companions that outline the available variables (<c>.env.template</c>, <c>.env.example</c>,
/// …) — so the names still show up when the real file is gitignored, empty, or absent. Read-only.
/// </summary>
public static partial class EnvKeyReader
{
    // KEY=..., optional leading `export`, KEY is an identifier. Value (if any) is ignored.
    [GeneratedRegex(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=")]
    private static partial Regex KeyLine();

    /// <summary>The variable names declared in <c>.env</c> <paramref name="content"/>, in first-seen order.</summary>
    public static IReadOnlyList<string> Keys(string content)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(content))
            return keys;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var m = KeyLine().Match(line);
            if (m.Success && seen.Add(m.Groups[1].Value))
                keys.Add(m.Groups[1].Value);
        }
        return keys;
    }

    /// <summary>
    /// Variable names available for <paramref name="file"/> (repo-relative), gathered from the file
    /// plus its conventional template companions. Best-effort — unreadable/missing candidates are
    /// skipped, never thrown.
    /// </summary>
    public static IReadOnlyList<string> KeysForFile(string repoRoot, string file)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in Candidates(file))
        {
            try
            {
                var abs = Path.Combine(repoRoot, candidate);
                if (!File.Exists(abs))
                    continue;
                foreach (var k in Keys(File.ReadAllText(abs)))
                    if (seen.Add(k)) keys.Add(k);
            }
            catch { /* skip an unreadable candidate */ }
        }
        return keys;
    }

    // The target file, then the template companions that outline available vars — both a per-file
    // template (e.g. .env.local.template) and the directory's generic .env.template family.
    static IEnumerable<string> Candidates(string file)
    {
        var f = (file ?? "").Replace('\\', '/').Trim().TrimStart('/');
        if (f.Length == 0)
            yield break;

        var dir = f.Contains('/') ? f[..(f.LastIndexOf('/') + 1)] : "";
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in new[]
                 {
                     f, f + ".template", f + ".example",
                     dir + ".env.template", dir + ".env.example", dir + ".env.sample", dir + ".env.dist",
                 })
            if (emitted.Add(c))
                yield return c;
    }
}
