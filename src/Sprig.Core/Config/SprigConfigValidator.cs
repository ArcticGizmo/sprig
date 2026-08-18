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

        // Map-model surface: provides/needs on the repo (single-app sugar) and per module.
        // Capability names are unique across the whole repo (a duplicate is a local ambiguity nearest-wins
        // can't resolve).
        var seenCaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateProvides(config.Provides, "provides", seenCaps, issues);
        ValidateNeeds(config.Needs, "needs", issues);

        // A config may be in the single-app flat shape (top-level env/compose/setup) or the module shape.
        // Validate whichever is present. Compose files
        // must be unique by their *effective* path (module path + file) across the whole repo, so the same
        // filename may appear in two modules at different paths but never collide.
        var seenComposePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ValidateEnv(config.Env ?? [], "env", issues);
        ValidateCompose(config.Compose ?? [], "compose", "", seenComposePaths, issues);
        ValidateSetup(config.Setup ?? [], "setup", issues);
        ValidateModules(config, seenComposePaths, seenCaps, issues);

        foreach (var reference in ConfigReferences.UndeclaredReferences(config))
            issues.Add(new("template",
                $"references '${{sprig.{reference}}}' which is not a provided/needed capability output "
                + "or 'workspace'"));

        return new ValidationResult(issues);
    }

    static void ValidateModules(
        SprigRepoConfig config, HashSet<string> seenComposePaths, HashSet<string> seenCaps, List<ValidationIssue> issues)
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

            ValidateProvides(module.Provides, $"{at}.provides", seenCaps, issues);
            ValidateNeeds(module.Needs, $"{at}.needs", issues);
            ValidateEnv(module.Env, $"{at}.env", issues);
            ValidateCompose(module.Compose, $"{at}.compose", module.Path, seenComposePaths, issues);
            ValidateSetup(module.Setup, $"{at}.setup", issues);
        }
    }

    static void ValidateProvides(
        IReadOnlyList<ProvidedCapability> provides, string prefix, HashSet<string> seenCaps, List<ValidationIssue> issues)
    {
        for (var i = 0; i < provides.Count; i++)
        {
            var p = provides[i];
            var at = $"{prefix}[{i}]";
            if (string.IsNullOrWhiteSpace(p.Capability))
                issues.Add(new($"{at}.capability", "must be a non-empty capability name"));
            else if (!IsIdentifier(p.Capability))
                issues.Add(new($"{at}.capability", $"'{p.Capability}' must contain only letters, digits, '-' or '_'"));
            else if (!seenCaps.Add(p.Capability))
                issues.Add(new($"{at}.capability", $"duplicate provided capability '{p.Capability}' in this repo"));

            if (p.Ports.Count == 0 && p.Shapes.Count == 0)
                issues.Add(new($"{at}", "a capability must declare at least one port or shape"));

            // Ports and shapes share one output namespace — a name is one or the other, never both.
            var seenOutputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, spec) in p.Ports)
            {
                if (string.IsNullOrWhiteSpace(name) || !IsIdentifier(name))
                    issues.Add(new($"{at}.ports", $"port name '{name}' must contain only letters, digits, '-' or '_'"));
                else if (!seenOutputs.Add(name))
                    issues.Add(new($"{at}.ports.{name}", $"duplicate output name '{name}' in this capability"));
                if (!string.IsNullOrWhiteSpace(spec.Allowed)
                    && !Ports.PortSetSpec.TryParse(spec.Allowed, out _, out var portErr))
                    issues.Add(new($"{at}.ports.{name}.allowed", portErr!));
            }
            foreach (var (name, template) in p.Shapes)
            {
                if (string.IsNullOrWhiteSpace(name) || !IsIdentifier(name))
                    issues.Add(new($"{at}.shapes", $"shape name '{name}' must contain only letters, digits, '-' or '_'"));
                else if (!seenOutputs.Add(name))
                    issues.Add(new($"{at}.shapes.{name}", $"duplicate output name '{name}' in this capability"));
                if (string.IsNullOrWhiteSpace(template))
                    issues.Add(new($"{at}.shapes.{name}", "a derived shape must be a non-empty template"));
            }

            ValidateShapeReferences(p, at, issues);
        }
    }

    /// <summary>
    /// A derived shape is part of the provider's own contract — resolved in isolation, before any wiring — so
    /// it may reference ONLY this capability's own outputs (its <c>port</c> and sibling shapes) or
    /// <c>${sprig.workspace}</c>. Referencing another capability or a need is rejected (that belongs in
    /// env/compose). A shape referencing itself, or a cycle between shapes, can never resolve, so both are
    /// flagged here rather than blowing up at checkout.
    /// </summary>
    static void ValidateShapeReferences(ProvidedCapability p, string at, List<ValidationIssue> issues)
    {
        if (p.Shapes.Count == 0) return;
        var outputs = new HashSet<string>(p.OutputNames, StringComparer.Ordinal);

        // Edges: a shape -> the sibling shapes it references (port refs can't form a cycle).
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (shapeName, template) in p.Shapes)
        {
            var edges = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in ConfigReferences.ReferencedNames(template))
            {
                if (reference == "workspace") continue;
                var dot = reference.IndexOf('.');
                var head = dot > 0 ? reference[..dot] : reference;
                var tail = dot > 0 ? reference[(dot + 1)..] : "";
                if (dot <= 0 || head != p.Capability)
                {
                    issues.Add(new($"{at}.shapes.{shapeName}",
                        $"a derived shape may only reference this capability's own outputs "
                        + $"(${{sprig.{p.Capability}.<output>}}) or ${{sprig.workspace}}, not '${{sprig.{reference}}}'"));
                    continue;
                }
                if (!outputs.Contains(tail))
                {
                    issues.Add(new($"{at}.shapes.{shapeName}",
                        $"references '${{sprig.{reference}}}', which is not one of '{p.Capability}'s outputs"));
                    continue;
                }
                if (tail == shapeName)
                    issues.Add(new($"{at}.shapes.{shapeName}", "a derived shape cannot reference itself"));
                else if (p.Shapes.ContainsKey(tail))
                    edges.Add(tail);
            }
            deps[shapeName] = edges;
        }

        if (FindShapeCycle(deps) is { } cycle)
            issues.Add(new($"{at}.shapes",
                $"circular dependency between derived shapes: {string.Join(" -> ", cycle)}"));
    }

    /// <summary>Depth-first cycle hunt over the shape dependency graph; returns the offending loop (closed on
    /// its first node) or null when the graph is acyclic.</summary>
    static IReadOnlyList<string>? FindShapeCycle(Dictionary<string, HashSet<string>> deps)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);   // 0/absent = unseen, 1 = on stack, 2 = done
        var stack = new List<string>();
        IReadOnlyList<string>? found = null;

        bool Visit(string node)
        {
            state[node] = 1;
            stack.Add(node);
            var neighbours = deps.TryGetValue(node, out var e) ? e : new HashSet<string>();
            foreach (var next in neighbours)
            {
                if (state.GetValueOrDefault(next) == 1)
                {
                    var from = stack.IndexOf(next);
                    found = stack.Skip(from).Append(next).ToList();   // e.g. a -> b -> a
                    return true;
                }
                if (state.GetValueOrDefault(next) == 0 && Visit(next)) return true;
            }
            state[node] = 2;
            stack.RemoveAt(stack.Count - 1);
            return false;
        }

        foreach (var node in deps.Keys)
            if (state.GetValueOrDefault(node) == 0 && Visit(node))
                break;
        return found;
    }

    static void ValidateNeeds(IReadOnlyList<Need> needs, string prefix, List<ValidationIssue> issues)
    {
        for (var i = 0; i < needs.Count; i++)
        {
            var n = needs[i];
            var at = $"{prefix}[{i}]";
            if (string.IsNullOrWhiteSpace(n.Capability))
                issues.Add(new($"{at}.capability", "must be a non-empty capability name"));
            else if (!IsIdentifier(n.Capability))
                issues.Add(new($"{at}.capability", $"'{n.Capability}' must contain only letters, digits, '-' or '_'"));
            if (!string.IsNullOrWhiteSpace(n.As) && !IsIdentifier(n.As!))
                issues.Add(new($"{at}.as", $"'{n.As}' must contain only letters, digits, '-' or '_'"));
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
