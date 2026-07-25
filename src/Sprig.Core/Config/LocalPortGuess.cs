using System.Text.RegularExpressions;

namespace Sprig.Core.Config;

/// <summary>
/// Guesses a <c>${sprig.*}</c> template for a value that hard-codes a port against a local host — a
/// URL like <c>http://localhost:5000</c> or a connection string like
/// <c>Host=localhost;Port=5432;…</c>. Only fires when the host is clearly local (localhost,
/// 127.0.0.1, the docker host, 0.0.0.0, or [::1]), so a value pointing at a real external service is
/// left alone. The port is rewritten to a declared input — chosen only when it's unambiguous (a
/// single port-named input, or the repo's only input), so a wrong guess isn't offered.
/// </summary>
public static partial class LocalPortGuess
{
    /// <summary>
    /// The value with its local port swapped for a <c>${sprig.&lt;input&gt;}</c> token, or null when
    /// there's no local port to template or no unambiguous input to bind it to.
    /// </summary>
    public static string? Rewrite(string? value, IReadOnlyList<string> inputNames)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var input = ChooseInput(inputNames);
        if (input is null) return null;
        var token = $"${{sprig.{input}}}";

        // host:port — a URL (scheme://localhost:PORT) or a bare localhost:PORT.
        var hp = HostPortPattern().Match(value);
        if (hp.Success)
            return Splice(value, hp.Groups["port"], token);

        // connection string — a local host somewhere in the string plus a Port=NNNN assignment.
        if (LocalHostPattern().IsMatch(value))
        {
            var pa = PortAssignPattern().Match(value);
            if (pa.Success)
                return Splice(value, pa.Groups["port"], token);
        }

        return null;
    }

    static string Splice(string value, Group port, string token) =>
        value[..port.Index] + token + value[(port.Index + port.Length)..];

    /// <summary>The input to bind the port to: a single port-named input, else the sole input, else none.</summary>
    static string? ChooseInput(IReadOnlyList<string> inputNames)
    {
        var inputs = inputNames.Where(n => !string.IsNullOrWhiteSpace(n) && n != "workspace").ToList();
        var portish = inputs.Where(n => n.Contains("port", StringComparison.OrdinalIgnoreCase)).ToList();
        if (portish.Count == 1) return portish[0];
        if (inputs.Count == 1) return inputs[0];
        return null; // ambiguous — better to offer nothing than the wrong input
    }

    [GeneratedRegex(@"(?:localhost|127\.0\.0\.1|host\.docker\.internal|0\.0\.0\.0|\[::1\]):(?<port>\d{2,5})",
        RegexOptions.IgnoreCase)]
    private static partial Regex HostPortPattern();

    [GeneratedRegex(@"localhost|127\.0\.0\.1|host\.docker\.internal|0\.0\.0\.0|\[::1\]", RegexOptions.IgnoreCase)]
    private static partial Regex LocalHostPattern();

    [GeneratedRegex(@"\bport\s*=\s*(?<port>\d{2,5})", RegexOptions.IgnoreCase)]
    private static partial Regex PortAssignPattern();
}
