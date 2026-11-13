using System.Text.RegularExpressions;

namespace Sprig.Engine.Authoring;

/// <summary>Writing YAML by hand, for the text sprig adds to a definition, so what it writes reads like what a person
/// would.</summary>
public static partial class YamlText
{
    /// <summary><paramref name="value"/> as a YAML scalar: plain when it reads back unchanged, else single-quoted.</summary>
    public static string Scalar(string value)
        => Plain().IsMatch(value) && value is not ("null" or "Null" or "NULL") ? value : "'" + value.Replace("'", "''") + "'";

    // Letters, digits and the punctuation paths and names use (a dot starts .env.local), never starting with one YAML
    // gives a meaning to.
    [GeneratedRegex(@"^[A-Za-z0-9_.][A-Za-z0-9_./\-]*$")]
    private static partial Regex Plain();
}
