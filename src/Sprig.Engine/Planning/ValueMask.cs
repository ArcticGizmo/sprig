using System.Text.RegularExpressions;

namespace Sprig.Engine.Planning;

/// <summary>Hides passwords in connection strings and URLs before a value is shown on screen. Plans keep the real
/// values, since sprig has to write them.</summary>
public static partial class ValueMask
{
    public static string Apply(string value) => UrlCredentials().Replace(Password().Replace(value, "$1***"), "$1$2:***@");

    [GeneratedRegex(@"((?:^|;)\s*Password\s*=\s*)[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex Password();

    [GeneratedRegex(@"(\w+://)([^:/@]+):[^@/]*@")]
    private static partial Regex UrlCredentials();
}
