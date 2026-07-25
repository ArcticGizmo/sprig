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

    [GeneratedRegex(@"\$\{sprig\.ports\.([^}]+)\}")]
    private static partial Regex PortRefPattern();
}
