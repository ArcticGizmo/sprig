using System.Text.RegularExpressions;

namespace Sprig.Core.Env;

/// <summary>One example value for an env variable, and the repo-relative file it was read from.
/// Used to show what a key looks like in the real/example files an override targets.</summary>
public sealed record EnvExample(string Source, string Value);

/// <summary>
/// Reads the variables declared in <c>.env</c>-style files, to drive key autosuggest and example
/// values when someone overrides an env file. Keys/values come from the target file itself plus the
/// conventional "template" companions that outline the available variables (<c>.env.template</c>,
/// <c>.env.example</c>, …) — so they still show up when the real file is gitignored, empty, or
/// absent. Read-only.
/// </summary>
public static partial class EnvKeyReader
{
    // KEY=value, optional leading `export`, KEY is an identifier; group 2 captures the rest of the line.
    [GeneratedRegex(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$")]
    private static partial Regex KeyLine();

    /// <summary>The <c>KEY=value</c> pairs declared in <c>.env</c> <paramref name="content"/>, in
    /// first-seen order (a repeated key keeps its first value). Values are trimmed and unquoted.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string content)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(content))
            return pairs;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimStart();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var m = KeyLine().Match(line);
            if (m.Success && seen.Add(m.Groups[1].Value))
                pairs.Add(new(m.Groups[1].Value, CleanValue(m.Groups[2].Value)));
        }
        return pairs;
    }

    /// <summary>The variable names declared in <c>.env</c> <paramref name="content"/>, in first-seen order.</summary>
    public static IReadOnlyList<string> Keys(string content)
        => Parse(content).Select(p => p.Key).ToList();

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
                foreach (var p in Parse(File.ReadAllText(abs)))
                    if (seen.Add(p.Key)) keys.Add(p.Key);
            }
            catch { /* skip an unreadable candidate */ }
        }
        return keys;
    }

    /// <summary>
    /// Example values for each variable available to <paramref name="file"/>: <c>KEY</c> → the
    /// (source file, value) pairs found across the file and its template companions, in scan order
    /// (at most one example per source file). Lets the editor show what a key looks like in the
    /// real/example files an override targets. Best-effort — unreadable/missing candidates skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<EnvExample>> ExamplesForFile(string repoRoot, string file)
    {
        var map = new Dictionary<string, List<EnvExample>>(StringComparer.Ordinal);
        foreach (var candidate in Candidates(file))
        {
            try
            {
                var abs = Path.Combine(repoRoot, candidate);
                if (!File.Exists(abs))
                    continue;
                foreach (var (key, value) in Parse(File.ReadAllText(abs)))
                {
                    if (value.Length == 0)
                        continue;   // a bare KEY= is no help as an example
                    if (!map.TryGetValue(key, out var list))
                        map[key] = list = [];
                    if (!list.Any(e => e.Source == candidate))
                        list.Add(new EnvExample(candidate, value));
                }
            }
            catch { /* skip an unreadable candidate */ }
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EnvExample>)kv.Value, StringComparer.Ordinal);
    }

    // Trim surrounding whitespace (incl. a trailing \r on CRLF files) and one layer of matching quotes.
    static string CleanValue(string raw)
    {
        var v = raw.Trim();
        if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')))
            v = v[1..^1];
        return v;
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
