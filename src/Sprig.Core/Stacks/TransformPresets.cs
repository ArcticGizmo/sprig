namespace Sprig.Core.Stacks;

/// <summary>
/// A named way to turn a stack port into a binding value. The common case isn't a bare port — it's
/// "the URL of the thing on that port". A preset is just a template with a single <c>{PORT}</c> slot;
/// picking one generates the expression, and an existing expression can be recognised back to the
/// preset that would produce it (falling back to <see cref="TransformPresets.Custom"/>).
/// </summary>
public sealed record TransformPreset(string Id, string Label, string Pattern);

public static class TransformPresets
{
    /// <summary>The port itself: <c>${sprig.ports.x}</c>.</summary>
    public static readonly TransformPreset Raw = new("raw", "Raw port", "{PORT}");

    /// <summary>A localhost URL: <c>http://localhost:${sprig.ports.x}</c>.</summary>
    public static readonly TransformPreset Url = new("url", "URL — http://localhost:port", "http://localhost:{PORT}");

    /// <summary>A localhost HTTPS URL: <c>https://localhost:${sprig.ports.x}</c>.</summary>
    public static readonly TransformPreset UrlHttps = new("url-https", "URL — https://localhost:port", "https://localhost:{PORT}");

    /// <summary>Host and port: <c>localhost:${sprig.ports.x}</c>.</summary>
    public static readonly TransformPreset HostPort = new("host-port", "localhost:port", "localhost:{PORT}");

    /// <summary>Anything the presets don't cover — edit the expression directly.</summary>
    public static readonly TransformPreset Custom = new("custom", "Custom…", "");

    /// <summary>The presets a user can pick, ending with Custom. A stable, shared instance.</summary>
    public static IReadOnlyList<TransformPreset> All { get; } = [Raw, Url, UrlHttps, HostPort, Custom];

    const string Placeholder = "{PORT}";

    /// <summary>The expression this preset produces for <paramref name="port"/>.</summary>
    public static string Generate(TransformPreset preset, string port) =>
        preset.Pattern.Replace(Placeholder, Token(port));

    /// <summary>
    /// Recognise which preset an expression came from. Only a single-port expression can map to a
    /// preset; zero or several ports (or an unrecognised shape) is <see cref="Custom"/> and reports
    /// the port only when there's exactly one.
    /// </summary>
    public static (TransformPreset Preset, string? Port) Recognize(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return (Custom, null);

        var expr = expression.Trim();
        var ports = PortExpressions.ReferencedPorts(expr);
        if (ports.Count != 1)
            return (Custom, null);

        var port = ports[0];
        foreach (var preset in All)
            if (preset != Custom && Generate(preset, port) == expr)
                return (preset, port);

        return (Custom, port);
    }

    static string Token(string port) => $"${{sprig.ports.{port}}}";
}
