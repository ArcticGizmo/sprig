namespace Sprig.Cli;

/// <summary>Setting values given on the command line as <c>name=value</c>.</summary>
static class SettingValues
{
    /// <summary>The values by name, later ones winning. Returns why one can't be read, or null.</summary>
    public static string? Parse(IEnumerable<string> pairs, out Dictionary<string, string> values)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) return $"'{pair}' isn't name=value";
            values[pair[..eq].Trim()] = pair[(eq + 1)..];
        }
        return null;
    }

    public static IReadOnlyDictionary<string, string>? Merge(IReadOnlyDictionary<string, string>? kept, IReadOnlyDictionary<string, string> given)
    {
        if (given.Count == 0) return kept;
        var merged = new Dictionary<string, string>(kept ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (k, v) in given) merged[k] = v;
        return merged;
    }
}
