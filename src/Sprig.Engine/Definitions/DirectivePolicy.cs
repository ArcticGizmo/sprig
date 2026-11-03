namespace Sprig.Engine.Definitions;

/// <summary>
/// The trust hash covers only the sprig.cs text, so anything a <c>#:</c> directive pulls in from elsewhere would
/// run unseen: another package (whose build targets also run during restore), a local project, or an MSBuild
/// property that changes how it builds. Only the SDK package is allowed, plus a local SDK project in dev builds.
/// </summary>
public static class DirectivePolicy
{
    public sealed record Violation(int Line, string Directive, string Reason);

    public static IReadOnlyList<Violation> Check(string text, bool allowLocalSdkProject)
    {
        var violations = new List<Violation>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#:", StringComparison.Ordinal)) continue;

            var parts = line[2..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var kind = parts.Length > 0 ? parts[0] : "";
            var value = parts.Length > 1 ? parts[1] : "";

            var reason = kind switch
            {
                "package" when IsSdkPackage(value) => null,
                "package" => "only the Sprig.Sdk package may be referenced",
                "project" when allowLocalSdkProject && IsSdkProject(value) => null,
                "project" => "a definition can't reference a local project",
                _ => $"the #:{kind} directive isn't allowed in a definition",
            };
            if (reason is not null) violations.Add(new Violation(i + 1, line, reason));
        }
        return violations;
    }

    static bool IsSdkPackage(string value)
    {
        var id = value.Split('@', 2)[0].Trim();
        return string.Equals(id, "Sprig.Sdk", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsSdkProject(string value)
        => string.Equals(Path.GetFileName(value.Trim().Trim('"')), "Sprig.Sdk.csproj", StringComparison.OrdinalIgnoreCase);
}
