using System.Globalization;
using Sprig.Core.Config;
using Sprig.Core.Git;
using YamlDotNet.RepresentationModel;

namespace Sprig.Core.Init;

/// <summary>A proposed <c>.sprig.json</c> plus advisory notes for the user to review.</summary>
public sealed record InitProposal(SprigRepoConfig Config, IReadOnlyList<string> Notes);

/// <summary>What detection found in a single compose file: the value <see cref="Overrides"/> to apply,
/// the port <see cref="Inputs"/> those overrides reference (to declare), and any advisory
/// <see cref="Notes"/>. Returned by <see cref="InitInspector.DetectComposeInText"/> so the editor's
/// manual "add compose file" flow can propose the same isolation the initial add does.</summary>
public sealed record ComposeDetection(
    IReadOnlyList<ComposeOverride> Overrides,
    IReadOnlyList<InputDeclaration> Inputs,
    IReadOnlyList<string> Notes);

/// <summary>A module to scaffold: its <see cref="Name"/> (tab label) and the repo-relative
/// <see cref="Path"/> (subdirectory) its detection is scoped to. Empty path = the repo root.</summary>
public sealed record ModuleSpec(string Name, string Path);

/// <summary>
/// Detects a repo's isolation surface and proposes a <c>.sprig.json</c>: it turns port-shaped env
/// keys and compose ports into declared <b>inputs</b> (with example shapes) that the stack will
/// supply, and rewrites the matching env/compose values to reference those inputs. Heuristic and
/// advisory — a starting point the user edits.
/// <para>
/// Env detection is git-aware: only <b>untracked</b> env files become override targets (overriding a
/// tracked file would permanently dirty every worktree). A <b>tracked</b> env file sitting next to an
/// untracked one — the classic committed <c>.env</c> template beside a gitignored <c>.env.local</c> —
/// is offered as a seed <b>template</b> for that target rather than clobbered itself.
/// </para>
/// </summary>
public sealed class InitInspector
{
    static readonly string[] ComposeNames =
        ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    /// <summary>Directories never worth scanning for env files (build output, dependency caches, VCS).</summary>
    static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "dist", "build", "out", "bin", "obj", ".vs", ".vscode", ".idea",
        "target", "coverage", ".next", ".nuxt", ".svelte-kit", "vendor", ".venv", "venv",
        "__pycache__", ".gradle", ".terraform", ".turbo", ".cache",
    };

    /// <summary>How deep below the repo root to look for env files — enough for a monorepo package,
    /// shallow enough not to crawl the whole tree.</summary>
    const int MaxDepth = 5;

    readonly IGitService _git;

    public InitInspector(IGitService git) => _git = git;

    /// <summary>
    /// Scaffold a single default module at the repo root, scanning the whole tree. This is the
    /// "one module" answer to the add-repo structure prompt — and the behaviour when the caller has
    /// no module opinion. An empty module (nothing detected) is dropped, so a bare repo proposes none.
    /// </summary>
    public InitProposal Inspect(string repoRoot)
        => InspectModules(repoRoot, [new ModuleSpec(SprigConfigMigration.DefaultModuleName, "")], dropEmpty: true);

    /// <summary>
    /// Scaffold the given modules, scoping detection to each one's path. Inputs are shared across every
    /// module (deduplicated as a single repo-level list). Every named module is kept even when its path
    /// yields nothing to isolate — the user asked for it, and can fill it in the editor. Falls back to
    /// the single-default <see cref="Inspect(string)"/> when no modules are supplied.
    /// </summary>
    public InitProposal Inspect(string repoRoot, IReadOnlyList<ModuleSpec> modules)
        => modules.Count == 0 ? Inspect(repoRoot) : InspectModules(repoRoot, modules, dropEmpty: false);

    /// <summary>
    /// Map-model counterpart to <see cref="Inspect(string)"/> (the Graph Turn): a detected listen/service
    /// port becomes a <b>provided</b> capability the repo owns (auto-allocated per workspace) rather than a
    /// stack-supplied input, and the matching env/compose value is rewritten to <c>${sprig.&lt;cap&gt;.port}</c>.
    /// <b>needs</b> (external services this repo consumes) can't be inferred reliably — an embedded URL is
    /// surfaced as a note for the author to declare by hand. Heuristic and advisory; a starting point.
    /// </summary>
    public InitProposal InspectMap(string repoRoot)
    {
        var notes = new List<string>();
        var usedCaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var provides = new List<ProvidedCapability>();
        var env = new List<EnvOverride>();

        DetectEnvProvides(repoRoot, provides, usedCaps, env, notes);
        var compose = DetectComposeProvides(repoRoot, provides, usedCaps, notes);

        var module = new ModuleDeclaration
        {
            Name = SprigConfigMigration.DefaultModuleName, Path = "",
            Provides = provides, Env = env, Compose = compose,
        };
        var hasSurface = provides.Count > 0 || env.Count > 0 || compose.Count > 0;
        var config = new SprigRepoConfig
        {
            Schema = SprigConfigLoader.SupportedSchema,
            Name = Path.GetFileName(repoRoot.TrimEnd('\\', '/')),
            Modules = hasSurface ? [module] : [],
        };

        if (!hasSurface)
            notes.Add("no ports or compose detected — declare this repo's provides/needs by hand");
        notes.Add("review the provided capabilities; add any 'needs' (services this repo consumes) by hand");
        return new InitProposal(config, notes);
    }

    /// <summary>Env detection for the map model: each bare-port key becomes its own provided capability with
    /// a single <c>port</c> output, and its value is rewritten to reference it. Same git-aware targeting as
    /// <see cref="DetectEnv"/> — only untracked files are overridden, tracked neighbours seed as templates.</summary>
    void DetectEnvProvides(string repoRoot, List<ProvidedCapability> provides, HashSet<string> usedCaps,
        List<EnvOverride> envOverrides, List<string> notes)
    {
        var scanRoot = ScanRoot(repoRoot, "");
        if (!Directory.Exists(scanRoot)) return;
        var tracked = new HashSet<string>(_git.ListTrackedFiles(repoRoot), StringComparer.OrdinalIgnoreCase);
        var envFiles = EnumerateEnvFiles(repoRoot, scanRoot).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var dir in envFiles.GroupBy(DirOf, StringComparer.OrdinalIgnoreCase))
        {
            var templates = dir.Where(tracked.Contains).ToList();
            foreach (var target in dir.Where(f => !tracked.Contains(f)))
            {
                var set = new Dictionary<string, string>();
                void Scan(string relFile)
                {
                    var abs = Path.Combine(repoRoot, relFile);
                    if (!File.Exists(abs)) return;
                    foreach (var (key, value) in ParseEnv(File.ReadAllText(abs)))
                    {
                        if (IsBarePort(value) && !set.ContainsKey(key))
                        {
                            var cap = UniqueName(Sanitize(key), usedCaps);
                            provides.Add(new ProvidedCapability
                            {
                                Capability = cap,
                                Outputs = new Dictionary<string, OutputSpec> { ["port"] = OutputSpec.Port() },
                            });
                            set[key] = $"${{sprig.{cap}.port}}";
                        }
                        else if (relFile == target && LooksLikeEmbeddedPort(key, value))
                            notes.Add($"'{key}' in {relFile} looks like it consumes another service — declare a need and reference it (e.g. ${{sprig.api.url}})");
                    }
                }
                Scan(target);
                foreach (var t in templates) Scan(t);
                if (set.Count == 0) continue;
                envOverrides.Add(new EnvOverride
                {
                    File = target,
                    Templates = templates.Count > 0 ? templates : null,
                    Set = set,
                });
            }
        }
    }

    /// <summary>Compose detection for the map model: each service with a published port becomes a provided
    /// capability named after the service (single <c>port</c> output), and the port mapping is rewritten to
    /// reference it. container_name is still suffixed with the workspace slug.</summary>
    List<ComposeConfig> DetectComposeProvides(string repoRoot, List<ProvidedCapability> provides,
        HashSet<string> usedCaps, List<string> notes)
    {
        var result = new List<ComposeConfig>();
        var scanRoot = ScanRoot(repoRoot, "");
        if (!Directory.Exists(scanRoot)) return result;

        foreach (var file in EnumerateComposeFiles(repoRoot, scanRoot).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(repoRoot, file)); }
            catch { notes.Add($"could not parse {file} — skipping compose detection"); continue; }

            YamlMappingNode root;
            try
            {
                var stream = new YamlStream();
                using var reader = new StringReader(text);
                stream.Load(reader);
                root = (YamlMappingNode)stream.Documents[0].RootNode;
            }
            catch { notes.Add($"could not parse {file} — skipping compose detection"); continue; }

            var overrides = new List<ComposeOverride>();
            if (TryGetMap(root, "services", out var services))
                foreach (var entry in services.Children)
                {
                    var svc = ((YamlScalarNode)entry.Key).Value!;
                    if (entry.Value is not YamlMappingNode svcNode) continue;

                    if (TryGetScalar(svcNode, "container_name", out var cname))
                        overrides.Add(new ComposeOverride
                        {
                            Path = ["services", svc, "container_name"],
                            Template = $"{cname}--${{sprig.workspace}}",
                        });

                    if (svcNode.Children.TryGetValue(new YamlScalarNode("ports"), out var portsNode)
                        && portsNode is YamlSequenceNode { Children.Count: > 0 } seq
                        && seq.Children[0] is YamlScalarNode { Value: { } mapping })
                    {
                        var (_, container) = SplitPort(mapping);
                        var cap = UniqueName(Sanitize(svc), usedCaps);
                        provides.Add(new ProvidedCapability
                        {
                            Capability = cap, Type = "port",
                            Outputs = new Dictionary<string, OutputSpec> { ["port"] = OutputSpec.Port() },
                        });
                        overrides.Add(new ComposeOverride
                        {
                            Path = ["services", svc, "ports", "0"],
                            Template = $"${{sprig.{cap}.port}}:{container}",
                        });
                    }
                }

            if (overrides.Count > 0)
                result.Add(new ComposeConfig { File = file, Overrides = overrides });
        }
        return result;
    }

    InitProposal InspectModules(string repoRoot, IReadOnlyList<ModuleSpec> specs, bool dropEmpty)
    {
        var notes = new List<string>();
        var inputs = new List<InputDeclaration>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Each module scans only under its own path; inputs/used/notes are shared so port names stay
        // unique across the whole repo and the review notes read as one list.
        var modules = new List<ModuleDeclaration>();
        foreach (var spec in specs)
        {
            var module = DetectModule(repoRoot, spec, inputs, used, notes);
            if (!dropEmpty || module.Env.Count > 0 || module.Compose.Count > 0)
                modules.Add(module);
        }

        var config = new SprigRepoConfig
        {
            Schema = SprigConfigLoader.SupportedSchema,
            Name = Path.GetFileName(repoRoot.TrimEnd('\\', '/')),
            Inputs = inputs,
            Modules = modules,
        };

        var totalCompose = modules.Sum(m => m.Compose.Count);
        if (inputs.Count == 0 && totalCompose == 0)
            notes.Add("no ports or compose detected — you'll likely need to author .sprig.json by hand");
        notes.Add("review the proposed inputs (names + examples) — the stack will supply their values");
        return new InitProposal(config, notes);
    }

    /// <summary>Detect one module's env/compose, scoped to <paramref name="spec"/>'s path. The returned
    /// declaration's file paths are relative to that path (as <see cref="ModuleDeclaration.Env"/> expects).</summary>
    ModuleDeclaration DetectModule(string repoRoot, ModuleSpec spec, List<InputDeclaration> inputs,
        HashSet<string> used, List<string> notes)
    {
        var envOverrides = new List<EnvOverride>();
        DetectEnv(repoRoot, spec.Path, inputs, used, envOverrides, notes);
        var compose = DetectCompose(repoRoot, spec.Path, inputs, used, notes);
        return new ModuleDeclaration { Name = spec.Name, Path = spec.Path, Env = envOverrides, Compose = compose };
    }

    void DetectEnv(string repoRoot, string moduleRelPath, List<InputDeclaration> inputs, HashSet<string> used,
        List<EnvOverride> envOverrides, List<string> notes)
    {
        var scanRoot = ScanRoot(repoRoot, moduleRelPath);
        if (!Directory.Exists(scanRoot)) return;

        // git-tracked lookups stay keyed by repo-relative path, so enumeration keeps repo-relative paths
        // (also used to read/parse); only the stored File/Templates are rebased under the module path.
        var tracked = new HashSet<string>(_git.ListTrackedFiles(repoRoot), StringComparer.OrdinalIgnoreCase);
        var envFiles = EnumerateEnvFiles(repoRoot, scanRoot)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Group by directory so a tracked env file can seed the untracked file that sits next to it.
        foreach (var dir in envFiles.GroupBy(DirOf, StringComparer.OrdinalIgnoreCase))
        {
            var templates = dir.Where(tracked.Contains).ToList();  // committed .env/.env.example → seed source
            var targets = dir.Where(f => !tracked.Contains(f));    // untracked → safe to override

            foreach (var target in targets)
            {
                var set = new Dictionary<string, string>();

                // Detect port-shaped keys from the target itself first (real values win the example),
                // then from its tracked templates — the worktree seeds from those, so their ports
                // need isolating too. A key already claimed by the target is not re-added.
                void Scan(string relFile)
                {
                    var abs = Path.Combine(repoRoot, relFile);
                    if (!File.Exists(abs)) return;
                    foreach (var (key, value) in ParseEnv(File.ReadAllText(abs)))
                    {
                        if (IsBarePort(value) && !set.ContainsKey(key))
                        {
                            var inputName = UniqueName(Sanitize(key), used);
                            inputs.Add(new InputDeclaration { Name = inputName, Example = value, Description = $"from {relFile} {key}" });
                            set[key] = $"${{sprig.{inputName}}}";
                        }
                        else if (relFile == target && LooksLikeEmbeddedPort(key, value))
                        {
                            notes.Add($"'{key}' in {relFile} may embed a port/URL — declare an input and reference it by hand (e.g. ${{sprig.apiUrl}})");
                        }
                    }
                }

                Scan(target);
                foreach (var t in templates) Scan(t);

                // Nothing port-shaped to isolate → don't seed an override (an empty one is invalid anyway).
                if (set.Count == 0) continue;

                envOverrides.Add(new EnvOverride
                {
                    File = ModuleRelative(target, moduleRelPath),
                    Templates = templates.Count > 0
                        ? templates.Select(t => ModuleRelative(t, moduleRelPath)).ToList()
                        : null,
                    Set = set,
                });
            }
        }
    }

    /// <summary>Repo-relative (forward-slash) paths of every file under <paramref name="scanRoot"/> whose
    /// name matches <paramref name="matches"/>, skipping build/dependency directories and stopping at
    /// <see cref="MaxDepth"/> below the scan root. Paths stay relative to <paramref name="repoRoot"/> (so
    /// git-tracked lookups keep working); <paramref name="scanRoot"/> only bounds the walk. Best-effort —
    /// unreadable directories are skipped, never thrown.</summary>
    static IReadOnlyList<string> EnumerateFiles(string repoRoot, string scanRoot, Func<string, bool> matches)
    {
        var results = new List<string>();

        void Walk(string dir, int depth)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (matches(Path.GetFileName(f)))
                        results.Add(Path.GetRelativePath(repoRoot, f).Replace('\\', '/'));
            }
            catch { /* unreadable dir → skip */ }

            if (depth >= MaxDepth) return;

            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(dir); }
            catch { return; }
            foreach (var sub in subs)
                if (!ExcludedDirs.Contains(Path.GetFileName(sub)))
                    Walk(sub, depth + 1);
        }

        Walk(scanRoot, 0);
        return results;
    }

    /// <summary>Repo-relative paths of every <c>.env</c>-family file under <paramref name="scanRoot"/>.</summary>
    static IReadOnlyList<string> EnumerateEnvFiles(string repoRoot, string scanRoot)
        => EnumerateFiles(repoRoot, scanRoot, IsEnvFamily);

    /// <summary>Repo-relative paths of every docker-compose file under <paramref name="scanRoot"/>.</summary>
    static IReadOnlyList<string> EnumerateComposeFiles(string repoRoot, string scanRoot)
        => EnumerateFiles(repoRoot, scanRoot, IsComposeFile);

    /// <summary>Absolute directory a module's detection scans: the repo root joined with its path (or the
    /// repo root itself for an empty/root path).</summary>
    static string ScanRoot(string repoRoot, string moduleRelPath)
        => string.IsNullOrEmpty(moduleRelPath)
            ? repoRoot
            : Path.Combine(repoRoot, moduleRelPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Rebase a repo-relative path to be relative to <paramref name="moduleRelPath"/> — stripping
    /// the module's directory prefix so the stored value resolves under the module (unchanged for a root
    /// module, or a path that unexpectedly falls outside the module).</summary>
    static string ModuleRelative(string repoRelFile, string moduleRelPath)
    {
        if (string.IsNullOrEmpty(moduleRelPath)) return repoRelFile;
        var prefix = moduleRelPath.Replace('\\', '/').Trim('/') + "/";
        return repoRelFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? repoRelFile[prefix.Length..]
            : repoRelFile;
    }

    /// <summary><c>.env</c> or anything shaped <c>.env.*</c> (e.g. <c>.env.local</c>, <c>.env.example</c>).</summary>
    static bool IsEnvFamily(string fileName)
        => fileName.Equals(".env", StringComparison.OrdinalIgnoreCase)
           || fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase);

    /// <summary>One of the canonical compose file names (<c>docker-compose.yml</c>, <c>compose.yaml</c>, …).</summary>
    static bool IsComposeFile(string fileName)
        => ComposeNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>The directory portion of a repo-relative forward-slash path (empty for the repo root).</summary>
    static string DirOf(string relFile)
    {
        var slash = relFile.LastIndexOf('/');
        return slash < 0 ? "" : relFile[..slash];
    }

    List<ComposeConfig> DetectCompose(string repoRoot, string moduleRelPath, List<InputDeclaration> inputs,
        HashSet<string> used, List<string> notes)
    {
        // Compose files are recursively discovered (monorepos keep several), and — unlike env — are
        // never filtered by git: sprig overrides a compose file by generating a separate copy, so a
        // tracked/committed compose file is a perfectly safe target.
        var result = new List<ComposeConfig>();
        var scanRoot = ScanRoot(repoRoot, moduleRelPath);
        if (!Directory.Exists(scanRoot)) return result;

        // The repo-relative path reads/parses the file; the stored File is rebased under the module.
        foreach (var file in EnumerateComposeFiles(repoRoot, scanRoot).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var overrides = DetectComposeOverrides(repoRoot, file, inputs, used, notes);
            if (overrides.Count > 0)
                result.Add(new ComposeConfig { File = ModuleRelative(file, moduleRelPath), Overrides = overrides });
        }
        return result;
    }

    List<ComposeOverride> DetectComposeOverrides(string repoRoot, string file,
        List<InputDeclaration> inputs, HashSet<string> used, List<string> notes)
    {
        string text;
        try { text = File.ReadAllText(Path.Combine(repoRoot, file)); }
        catch { notes.Add($"could not parse {file} — skipping compose detection"); return []; }
        return ParseComposeOverrides(text, file, inputs, used, notes);
    }

    /// <summary>
    /// Run add-time compose detection over one compose file's <paramref name="composeText"/> — for the
    /// editor's manual "add compose file" flow, so a hand-added file gets the same proposed container-name
    /// and port rewrites (plus their declared inputs) as one found during the initial scan.
    /// <paramref name="existingInputNames"/> are the inputs already declared, so any new port input is
    /// named uniquely against them. Pure; git isn't consulted (compose files are always safe targets).
    /// </summary>
    public static ComposeDetection DetectComposeInText(string composeText, string fileLabel,
        IEnumerable<string> existingInputNames)
    {
        var inputs = new List<InputDeclaration>();
        var notes = new List<string>();
        var used = new HashSet<string>(existingInputNames, StringComparer.OrdinalIgnoreCase);
        var overrides = ParseComposeOverrides(composeText, fileLabel, inputs, used, notes);
        return new ComposeDetection(overrides, inputs, notes);
    }

    /// <summary>The shared core: parse a compose file's text and build its container-name/port overrides,
    /// appending any port inputs to <paramref name="inputs"/> (unique against <paramref name="used"/>) and
    /// advisory <paramref name="notes"/>. Used by both the initial scan and the editor's manual add.</summary>
    static List<ComposeOverride> ParseComposeOverrides(string composeText, string file,
        List<InputDeclaration> inputs, HashSet<string> used, List<string> notes)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(composeText);
            stream.Load(reader);
            root = (YamlMappingNode)stream.Documents[0].RootNode;
        }
        catch
        {
            notes.Add($"could not parse {file} — skipping compose detection");
            return [];
        }

        var overrides = new List<ComposeOverride>();

        if (TryGetMap(root, "services", out var services))
        {
            foreach (var entry in services.Children)
            {
                var svc = ((YamlScalarNode)entry.Key).Value!;
                if (entry.Value is not YamlMappingNode svcNode) continue;

                // container_name: suffix with the workspace slug (always available, not an input).
                if (TryGetScalar(svcNode, "container_name", out var cname))
                    overrides.Add(new ComposeOverride
                    {
                        Path = ["services", svc, "container_name"],
                        Template = $"{cname}--${{sprig.workspace}}",
                    });

                if (svcNode.Children.TryGetValue(new YamlScalarNode("ports"), out var portsNode)
                    && portsNode is YamlSequenceNode { Children.Count: > 0 } seq
                    && seq.Children[0] is YamlScalarNode { Value: { } mapping })
                {
                    var (host, container) = SplitPort(mapping);
                    var inputName = UniqueName(Sanitize(svc) + "_port", used);
                    inputs.Add(new InputDeclaration { Name = inputName, Example = host, Description = $"{svc} host port ({file})" });
                    overrides.Add(new ComposeOverride
                    {
                        Path = ["services", svc, "ports", "0"],
                        Template = $"${{sprig.{inputName}}}:{container}",
                    });
                }
            }
        }

        if (TryGetMap(root, "volumes", out var volumes) && volumes.Children.Count > 0)
            notes.Add($"{file} declares named volume(s) [{string.Join(", ", volumes.Children.Select(v => ((YamlScalarNode)v.Key).Value))}] — " +
                      "the per-workspace project name won't isolate these, and data won't persist across 'down' unless bound to a named volume; review");

        return overrides;
    }

    // --- helpers ---

    internal static IEnumerable<(string Key, string Value)> ParseEnv(string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            yield return (line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
    }

    static bool IsBarePort(string value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1024 and <= 65535;

    static bool LooksLikeEmbeddedPort(string key, string value)
        => key.Contains("port", StringComparison.OrdinalIgnoreCase)
           || key.Contains("url", StringComparison.OrdinalIgnoreCase)
           || value.Contains("://", StringComparison.Ordinal)
           || value.Contains("Port=", StringComparison.OrdinalIgnoreCase);

    static (string Host, string Container) SplitPort(string mapping)
    {
        // "6050:5432", "6050:5432/tcp", "127.0.0.1:6050:5432", or bare "5432"
        var proto = mapping.Split('/')[0];
        var parts = proto.Split(':');
        return parts.Length >= 2 ? (parts[^2], parts[^1]) : (parts[^1], parts[^1]);
    }

    static string Sanitize(string key)
    {
        var chars = key.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 0 ? slug : "value";
    }

    static string UniqueName(string baseName, HashSet<string> used)
    {
        var name = baseName;
        for (var i = 2; !used.Add(name); i++) name = $"{baseName}-{i}";
        return name;
    }

    static bool TryGetMap(YamlMappingNode node, string key, out YamlMappingNode map)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlMappingNode m)
        {
            map = m;
            return true;
        }
        map = null!;
        return false;
    }

    static bool TryGetScalar(YamlMappingNode node, string key, out string value)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s)
        {
            value = s.Value ?? "";
            return true;
        }
        value = "";
        return false;
    }
}
