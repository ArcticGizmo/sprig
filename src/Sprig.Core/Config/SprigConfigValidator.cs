namespace Sprig.Core.Config;

/// <summary>A single validation problem, located by a dotted config path.</summary>
public sealed record ValidationIssue(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

/// <summary>The outcome of validating a config; collects every issue rather than failing fast.</summary>
public sealed record ValidationResult(IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
    public static ValidationResult Ok { get; } = new([]);
}

/// <summary>
/// Validates a parsed <see cref="SprigRepoConfig"/> for internal consistency. Does NOT resolve
/// templates (that is the substitution engine's job) — it only checks structure.
/// </summary>
public static class SprigConfigValidator
{
    public static ValidationResult Validate(SprigRepoConfig config)
    {
        var issues = new List<ValidationIssue>();

        if (config.Schema != SprigConfigLoader.SupportedSchema)
            issues.Add(new("schema",
                $"unsupported schema {config.Schema}; this build understands {SprigConfigLoader.SupportedSchema}"));

        if (string.IsNullOrWhiteSpace(config.Name))
            issues.Add(new("name", "must be a non-empty repo name"));

        foreach (var key in config.Unknown.Keys)
            issues.Add(new(key, "unknown top-level key"));

        ValidatePorts(config, issues);
        ValidateEnv(config, issues);
        ValidateCompose(config, issues);
        ValidateProvides(config, issues);

        return new ValidationResult(issues);
    }

    static void ValidatePorts(SprigRepoConfig config, List<ValidationIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < config.Ports.Count; i++)
        {
            var p = config.Ports[i];
            var at = $"ports[{i}]";
            if (string.IsNullOrWhiteSpace(p.Name))
                issues.Add(new($"{at}.name", "must be a non-empty port name"));
            else if (!IsIdentifier(p.Name))
                issues.Add(new($"{at}.name", $"'{p.Name}' must contain only letters, digits, '-' or '_'"));
            else if (!seen.Add(p.Name))
                issues.Add(new($"{at}.name", $"duplicate port name '{p.Name}'"));
        }
    }

    static void ValidateEnv(SprigRepoConfig config, List<ValidationIssue> issues)
    {
        for (var i = 0; i < config.Env.Count; i++)
        {
            var e = config.Env[i];
            var at = $"env[{i}]";
            if (string.IsNullOrWhiteSpace(e.File))
                issues.Add(new($"{at}.file", "must name a .env file to clobber"));
            if (e.Set.Count == 0)
                issues.Add(new($"{at}.set", "must set at least one key"));
            foreach (var k in e.Set.Keys)
                if (string.IsNullOrWhiteSpace(k))
                    issues.Add(new($"{at}.set", "env keys must be non-empty"));
        }
    }

    static void ValidateCompose(SprigRepoConfig config, List<ValidationIssue> issues)
    {
        if (config.Compose is not { } compose) return;
        if (string.IsNullOrWhiteSpace(compose.File))
            issues.Add(new("compose.file", "must name the repo's compose file"));
        for (var i = 0; i < compose.Overrides.Count; i++)
        {
            var o = compose.Overrides[i];
            var at = $"compose.overrides[{i}]";
            if (o.Path.Count == 0)
                issues.Add(new($"{at}.path", "must have at least one path segment"));
            else if (o.Path.Any(string.IsNullOrWhiteSpace))
                issues.Add(new($"{at}.path", "path segments must be non-empty"));
            if (string.IsNullOrWhiteSpace(o.Template))
                issues.Add(new($"{at}.template", "must be a non-empty template"));
        }
    }

    static void ValidateProvides(SprigRepoConfig config, List<ValidationIssue> issues)
    {
        foreach (var k in config.Provides.Keys)
            if (string.IsNullOrWhiteSpace(k))
                issues.Add(new("provides", "provided keys must be non-empty"));
    }

    static bool IsIdentifier(string s)
    {
        foreach (var c in s)
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        return true;
    }
}
