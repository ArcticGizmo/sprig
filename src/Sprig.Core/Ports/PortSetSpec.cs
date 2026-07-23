using System.Globalization;
using Sprig.Core.Settings;

namespace Sprig.Core.Ports;

/// <summary>
/// Parses (and renders) a compact port-set spec: a comma-separated list of single ports and
/// inclusive ranges, e.g. <c>"8100-8103"</c>, <c>"8100,8101,8200"</c>, or <c>"8100-8103,8200"</c>.
/// Whitespace around items and around a range's <c>-</c> is ignored. Every port must be a valid
/// port number (<see cref="SprigSettings.MinPort"/>–<see cref="SprigSettings.MaxPort"/>).
/// </summary>
public static class PortSetSpec
{
    /// <summary>Parse <paramref name="spec"/> into a sorted set of ports.</summary>
    /// <exception cref="FormatException">The spec is empty or malformed.</exception>
    public static IReadOnlySet<int> Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new FormatException("port set is empty");

        var result = new SortedSet<int>();
        foreach (var item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = item.IndexOf('-');
            if (dash < 0)
            {
                result.Add(ParsePort(item));
                continue;
            }

            var lo = ParsePort(item[..dash].Trim());
            var hi = ParsePort(item[(dash + 1)..].Trim());
            if (hi < lo)
                throw new FormatException($"range '{item}' has its end before its start");
            for (var p = lo; p <= hi; p++)
                result.Add(p);
        }

        if (result.Count == 0)
            throw new FormatException("port set is empty");
        return result;
    }

    /// <summary>Parse without throwing; returns false and an explanation on a malformed spec.</summary>
    public static bool TryParse(string spec, out IReadOnlySet<int> ports, out string? error)
    {
        try { ports = Parse(spec); error = null; return true; }
        catch (FormatException ex) { ports = new HashSet<int>(); error = ex.Message; return false; }
    }

    /// <summary>Render a set back to the compact spec form, collapsing runs into ranges.</summary>
    public static string Describe(IReadOnlySet<int> ports)
    {
        var sorted = ports.OrderBy(p => p).ToList();
        if (sorted.Count == 0) return "(none)";

        var parts = new List<string>();
        var start = sorted[0];
        var prev = sorted[0];
        for (var i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && sorted[i] == prev + 1) { prev = sorted[i]; continue; }
            parts.Add(start == prev ? start.ToString(CultureInfo.InvariantCulture)
                : $"{start}-{prev}");
            if (i < sorted.Count) { start = sorted[i]; prev = sorted[i]; }
        }
        return string.Join(",", parts);
    }

    static int ParsePort(string s)
    {
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p))
            throw new FormatException($"'{s}' is not a valid port number");
        if (p < SprigSettings.MinPort || p > SprigSettings.MaxPort)
            throw new FormatException($"port {p} is out of range (must be {SprigSettings.MinPort}-{SprigSettings.MaxPort})");
        return p;
    }
}
