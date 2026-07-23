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

        ValidateInputs(config, issues);
        ValidateEnv(config, issues);
        ValidateCompose(config, issues);

        foreach (var reference in ConfigReferences.UndeclaredReferences(config))
            issues.Add(new("template",
                $"references '${{sprig.{reference}}}' which is not a declared input (add it to \"inputs\")"));

        return new ValidationResult(issues);
    }

    static void ValidateInputs(SprigRepoConfig config, List<ValidationIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < config.Inputs.Count; i++)
        {
            var input = config.Inputs[i];
            var at = $"inputs[{i}]";
            if (string.IsNullOrWhiteSpace(input.Name))
                issues.Add(new($"{at}.name", "must be a non-empty input name"));
            else if (!IsIdentifier(input.Name))
                issues.Add(new($"{at}.name", $"'{input.Name}' must contain only letters, digits, '-' or '_'"));
            else if (!seen.Add(input.Name))
                issues.Add(new($"{at}.name", $"duplicate input name '{input.Name}'"));

            if (!string.IsNullOrWhiteSpace(input.AllowedPorts)
                && !Ports.PortSetSpec.TryParse(input.AllowedPorts, out _, out var portErr))
                issues.Add(new($"{at}.allowedPorts", portErr!));
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
            if (e.Templates is { } templates)
                for (var t = 0; t < templates.Count; t++)
                    if (string.IsNullOrWhiteSpace(templates[t]))
                        issues.Add(new($"{at}.templates[{t}]", "template path must be non-empty"));
            if (e.Set.Count == 0)
                issues.Add(new($"{at}.set", "must set at least one key"));
            foreach (var k in e.Set.Keys)
                if (string.IsNullOrWhiteSpace(k))
                    issues.Add(new($"{at}.set", "env keys must be non-empty"));
        }
    }

    static void ValidateCompose(SprigRepoConfig config, List<ValidationIssue> issues)
    {
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var c = 0; c < config.Compose.Count; c++)
        {
            var compose = config.Compose[c];
            var atFile = $"compose[{c}].file";
            if (string.IsNullOrWhiteSpace(compose.File))
                issues.Add(new(atFile, "must name the repo's compose file"));
            else if (!seenFiles.Add(compose.File.Trim()))
                issues.Add(new(atFile, $"duplicate compose file '{compose.File.Trim()}'"));

            for (var i = 0; i < compose.Overrides.Count; i++)
            {
                var o = compose.Overrides[i];
                var at = $"compose[{c}].overrides[{i}]";
                if (o.Path.Count == 0)
                    issues.Add(new($"{at}.path", "must have at least one path segment"));
                else if (o.Path.Any(string.IsNullOrWhiteSpace))
                    issues.Add(new($"{at}.path", "path segments must be non-empty"));
                if (string.IsNullOrWhiteSpace(o.Template))
                    issues.Add(new($"{at}.template", "must be a non-empty template"));
            }
        }
    }

    static bool IsIdentifier(string s)
    {
        foreach (var c in s)
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        return true;
    }
}
