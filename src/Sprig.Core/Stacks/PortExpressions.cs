using System.Text.RegularExpressions;

namespace Sprig.Core.Stacks;

/// <summary>
/// Reads the <c>${sprig.ports.&lt;name&gt;}</c> references out of a binding expression. A binding is
/// either a literal or a template over those port tokens (and <c>${sprig.workspace}</c>); this
/// pulls just the stack-port names it names, in first-seen order. Shared by the shared-port
/// migration, the store's consistency check, the auto-wire proposer, and the wiring canvas.
/// </summary>
public static partial class PortExpressions
{
    /// <summary>The distinct stack-port names <paramref name="expr"/> references, in first-seen order.</summary>
    public static IReadOnlyList<string> ReferencedPorts(string? expr)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(expr)) return names;
        foreach (Match m in PortRefPattern().Matches(expr))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length > 0 && !names.Contains(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>
    /// Whether <paramref name="expr"/> references the built-in <c>${sprig.workspace}</c> source. The
    /// workspace is a first-class producer (a fixed, named string that fans out to many inputs), not a
    /// port — the wiring canvas draws it as its own source on the rail.
    /// </summary>
    public static bool ReferencesWorkspace(string? expr) =>
        !string.IsNullOrEmpty(expr) && WorkspaceRefPattern().IsMatch(expr);

    /// <summary>True when <paramref name="expr"/> is exactly a single source token and nothing else —
    /// a bare <c>${sprig.ports.x}</c> or <c>${sprig.workspace}</c>. Such a pass-through needs no
    /// transform node on the canvas; anything else (wrapping text, multiple sources) does.</summary>
    public static bool IsBareSourceReference(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;
        var trimmed = expr.Trim();
        if (trimmed == "${sprig.workspace}") return true;
        var ports = ReferencedPorts(trimmed);
        return ports.Count == 1 && !ReferencesWorkspace(trimmed) && trimmed == $"${{sprig.ports.{ports[0]}}}";
    }

    [GeneratedRegex(@"\$\{sprig\.ports\.([^}]+)\}")]
    private static partial Regex PortRefPattern();

    [GeneratedRegex(@"\$\{sprig\.workspace\}")]
    private static partial Regex WorkspaceRefPattern();
}
