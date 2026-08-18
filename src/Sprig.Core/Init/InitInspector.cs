using System.Globalization;
using Sprig.Core.Config;
using Sprig.Core.Git;
using YamlDotNet.RepresentationModel;

namespace Sprig.Core.Init;

/// <summary>A proposed <c>.sprig.json</c> plus advisory notes for the user to review.</summary>
public sealed record InitProposal(SprigRepoConfig Config, IReadOnlyList<string> Notes);

/// <summary>A module to scaffold: its <see cref="Name"/> (tab label) and the repo-relative
/// <see cref="Path"/> (subdirectory) its detection is scoped to. Empty path = the repo root.</summary>
public sealed record ModuleSpec(string Name, string Path);

/// <summary>
/// Detects a repo's isolation surface and proposes a <c>.sprig.json</c> (the map model): it turns
/// port-shaped env keys and compose ports into <b>provided</b> capabilities the repo owns
/// (auto-allocated per workspace), and rewrites the matching env/compose values to reference them.
/// Heuristic and advisory — a starting point the user edits.
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
    /// Scaffold a single default module at the repo root, scanning the whole tree: a detected listen/service
    /// port becomes a <b>provided</b> capability the repo owns (auto-allocated per workspace), and the matching
    /// env/compose value is rewritten to <c>${sprig.&lt;cap&gt;.port}</c>. <b>needs</b> (external services this
    /// repo consumes) can't be inferred reliably — an embedded URL is surfaced as a note for the author to
    /// declare by hand. An empty module (nothing detected) is dropped, so a bare repo proposes none.
    /// </summary>
    public InitProposal InspectMap(string repoRoot)
        => InspectMapModules(repoRoot, [new ModuleSpec(SprigConfigMigration.DefaultModuleName, "")], dropEmpty: true);

    /// <summary>
    /// Multiple-modules counterpart to <see cref="InspectMap(string)"/>: scaffold each given module map-native,
    /// scoping provides detection to its path. Capability names stay unique across the whole repo and the
    /// review notes read as one list. Every named module is kept even when its path yields no capability — the
    /// user asked for it, and can declare provides/needs in the editor. Falls back to the single-default
    /// <see cref="InspectMap(string)"/> when no modules are supplied.
    /// </summary>
    public InitProposal InspectMap(string repoRoot, IReadOnlyList<ModuleSpec> modules)
        => modules.Count == 0 ? InspectMap(repoRoot) : InspectMapModules(repoRoot, modules, dropEmpty: false);

    InitProposal InspectMapModules(string repoRoot, IReadOnlyList<ModuleSpec> specs, bool dropEmpty)
    {
        var notes = new List<string>();
        var usedCaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var modules = new List<ModuleDeclaration>();
        foreach (var spec in specs)
        {
            var provides = new List<ProvidedCapability>();
            var env = new List<EnvOverride>();
            DetectEnvProvides(repoRoot, spec.Path, provides, usedCaps, env, notes);
            var compose = DetectComposeProvides(repoRoot, spec.Path, provides, usedCaps, notes);

            var module = new ModuleDeclaration
            {
                Name = spec.Name, Path = spec.Path,
                Provides = provides, Env = env, Compose = compose,
            };
            if (!dropEmpty || provides.Count > 0 || env.Count > 0 || compose.Count > 0)
                modules.Add(module);
        }

        var config = new SprigRepoConfig
        {
            Schema = SprigConfigLoader.SupportedSchema,
            Name = Path.GetFileName(repoRoot.TrimEnd('\\', '/')),
            Modules = modules,
        };

        if (modules.Count == 0)
            notes.Add("no ports or compose detected — declare this repo's provides/needs by hand");
        notes.Add("review the provided capabilities; add any 'needs' (services this repo consumes) by hand");
        return new InitProposal(config, notes);
    }

    /// <summary>Env detection for the map model: each bare-port key becomes its own provided capability with
    /// a single <c>port</c> output, and its value is rewritten to reference it. Same git-aware targeting as
    /// <see cref="DetectEnv"/> — only untracked files are overridden, tracked neighbours seed as templates.</summary>
    void DetectEnvProvides(string repoRoot, string moduleRelPath, List<ProvidedCapability> provides,
        HashSet<string> usedCaps, List<EnvOverride> envOverrides, List<string> notes)
    {
        var scanRoot = ScanRoot(repoRoot, moduleRelPath);
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
                    File = ModuleRelative(target, moduleRelPath),
                    Templates = templates.Count > 0
                        ? templates.Select(t => ModuleRelative(t, moduleRelPath)).ToList()
                        : null,
                    Set = set,
                });
            }
        }
    }

    /// <summary>Compose detection for the map model: each service with a published port becomes a provided
    /// capability named after the service (single <c>port</c> output), and the port mapping is rewritten to
    /// reference it. container_name is still suffixed with the workspace slug.</summary>
    List<ComposeConfig> DetectComposeProvides(string repoRoot, string moduleRelPath,
        List<ProvidedCapability> provides, HashSet<string> usedCaps, List<string> notes)
    {
        var result = new List<ComposeConfig>();
        var scanRoot = ScanRoot(repoRoot, moduleRelPath);
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
                result.Add(new ComposeConfig { File = ModuleRelative(file, moduleRelPath), Overrides = overrides });
        }
        return result;
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
