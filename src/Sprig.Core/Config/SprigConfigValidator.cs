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

        // A config may be in the legacy flat shape (a schema-≤2 file before migration, or the editor's
        // pre-modules Build output) or the new module shape. Validate whichever is present. Compose files
        // must be unique by their *effective* path (module path + file) across the whole repo, so the same
        // filename may appear in two modules at different paths but never collide.
        var seenComposePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateEnv(config.Env, "env", issues);
        ValidateCompose(config.Compose, "compose", "", seenComposePaths, issues);
        ValidateSetup(config.Setup, "setup", issues);
        ValidateModules(config, seenComposePaths, issues);

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

    static void ValidateModules(SprigRepoConfig config, HashSet<string> seenComposePaths, List<ValidationIssue> issues)
    {
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var m = 0; m < config.Modules.Count; m++)
        {
            var module = config.Modules[m];
            var at = $"modules[{m}]";

            if (string.IsNullOrWhiteSpace(module.Name))
                issues.Add(new($"{at}.name", "must be a non-empty module name"));
            else if (!IsIdentifier(module.Name))
                issues.Add(new($"{at}.name", $"'{module.Name}' must contain only letters, digits, '-' or '_'"));
            else if (!seenNames.Add(module.Name))
                issues.Add(new($"{at}.name", $"duplicate module name '{module.Name}'"));

            if (!string.IsNullOrEmpty(module.Path) && !IsSafeRelativePath(module.Path))
                issues.Add(new($"{at}.path",
                    "must be a relative path inside the repo (no drive letter, leading '/' or '..' segments)"));

            ValidateEnv(module.Env, $"{at}.env", issues);
            ValidateCompose(module.Compose, $"{at}.compose", module.Path, seenComposePaths, issues);
            ValidateSetup(module.Setup, $"{at}.setup", issues);
        }
    }

    static void ValidateEnv(IReadOnlyList<EnvOverride> env, string prefix, List<ValidationIssue> issues)
    {
        for (var i = 0; i < env.Count; i++)
        {
            var e = env[i];
            var at = $"{prefix}[{i}]";
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

    static void ValidateCompose(
        IReadOnlyList<ComposeConfig> compose, string prefix, string basePath,
        HashSet<string> seenEffectivePaths, List<ValidationIssue> issues)
    {
        for (var c = 0; c < compose.Count; c++)
        {
            var cfg = compose[c];
            var atFile = $"{prefix}[{c}].file";
            if (string.IsNullOrWhiteSpace(cfg.File))
                issues.Add(new(atFile, "must name the repo's compose file"));
            else if (!seenEffectivePaths.Add(EffectivePath(basePath, cfg.File)))
                issues.Add(new(atFile, $"duplicate compose file '{cfg.File.Trim()}'"));

            for (var i = 0; i < cfg.Overrides.Count; i++)
            {
                var o = cfg.Overrides[i];
                var at = $"{prefix}[{c}].overrides[{i}]";
                if (o.Path.Count == 0)
                    issues.Add(new($"{at}.path", "must have at least one path segment"));
                else if (o.Path.Any(string.IsNullOrWhiteSpace))
                    issues.Add(new($"{at}.path", "path segments must be non-empty"));
                if (string.IsNullOrWhiteSpace(o.Template))
                    issues.Add(new($"{at}.template", "must be a non-empty template"));
            }
        }
    }

    static void ValidateSetup(IReadOnlyList<string> setup, string prefix, List<ValidationIssue> issues)
    {
        for (var i = 0; i < setup.Count; i++)
            if (string.IsNullOrWhiteSpace(setup[i]))
                issues.Add(new($"{prefix}[{i}]", "must be a non-empty command"));
    }

    /// <summary>Combines a module's base path and a file into a normalised, case-folding dedup key.</summary>
    static string EffectivePath(string basePath, string file)
    {
        var combined = string.IsNullOrEmpty(basePath) ? file.Trim() : $"{basePath.Trim()}/{file.Trim()}";
        return combined.Replace('\\', '/').TrimStart('/');
    }

    static bool IsIdentifier(string s)
    {
        foreach (var c in s)
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        return true;
    }

    /// <summary>A module path must stay inside the repo: relative, no drive letter, no <c>..</c> escape.</summary>
    static bool IsSafeRelativePath(string path)
    {
        var p = path.Replace('\\', '/').Trim();
        if (p.StartsWith('/')) return false;                    // rooted
        if (p.Length >= 2 && p[1] == ':') return false;         // drive letter, e.g. "C:"
        foreach (var seg in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (seg == "..") return false;                       // escapes the repo
        return true;
    }
}
