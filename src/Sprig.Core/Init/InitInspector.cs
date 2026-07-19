using System.Globalization;
using Sprig.Core.Config;
using YamlDotNet.RepresentationModel;

namespace Sprig.Core.Init;

/// <summary>A proposed <c>.sprig.json</c> plus advisory notes for the user to review.</summary>
public sealed record InitProposal(SprigRepoConfig Config, IReadOnlyList<string> Notes);

/// <summary>
/// Detects a repo's isolation surface and proposes a <c>.sprig.json</c>: port-shaped env keys,
/// compose services (container name + first published port), and named-volume hints. Heuristic
/// and advisory — always a starting point the user edits.
/// </summary>
public sealed class InitInspector
{
    static readonly string[] EnvFileNames =
        [".env", ".env.local", ".env.development", ".env.development.local"];
    static readonly string[] ComposeNames =
        ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    public InitProposal Inspect(string repoRoot)
    {
        var notes = new List<string>();
        var ports = new List<PortDeclaration>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var envOverrides = new List<EnvOverride>();

        DetectEnv(repoRoot, ports, used, envOverrides, notes);
        var compose = DetectCompose(repoRoot, ports, used, notes);

        var name = Path.GetFileName(repoRoot.TrimEnd('\\', '/'));
        var config = new SprigRepoConfig
        {
            Name = name,
            Ports = ports,
            Env = envOverrides,
            Compose = compose,
        };

        if (ports.Count == 0 && compose is null)
            notes.Add("no ports or compose detected — you'll likely need to author .sprig.json by hand");
        notes.Add("review the proposal (port names, which keys to override) before using it");
        return new InitProposal(config, notes);
    }

    void DetectEnv(string repoRoot, List<PortDeclaration> ports, HashSet<string> used,
        List<EnvOverride> envOverrides, List<string> notes)
    {
        foreach (var file in EnvFileNames)
        {
            var path = Path.Combine(repoRoot, file);
            if (!File.Exists(path)) continue;

            var set = new Dictionary<string, string>();
            foreach (var (key, value) in ParseEnv(File.ReadAllText(path)))
            {
                if (IsBarePort(value))
                {
                    var portName = UniqueName(Sanitize(key), used);
                    ports.Add(new PortDeclaration { Name = portName, Description = $"from {file} {key}" });
                    set[key] = $"${{sprig.ports.{portName}}}";
                }
                else if (LooksLikeEmbeddedPort(key, value))
                {
                    notes.Add($"'{key}' in {file} may embed a port/URL — parameterize it by hand (e.g. Port=${{sprig.ports.…}})");
                }
            }
            if (set.Count > 0)
                envOverrides.Add(new EnvOverride { File = file, Set = set });
        }
    }

    ComposeConfig? DetectCompose(string repoRoot, List<PortDeclaration> ports, HashSet<string> used, List<string> notes)
    {
        var file = ComposeNames.FirstOrDefault(n => File.Exists(Path.Combine(repoRoot, n)));
        if (file is null) return null;

        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(File.ReadAllText(Path.Combine(repoRoot, file)));
            stream.Load(reader);
            root = (YamlMappingNode)stream.Documents[0].RootNode;
        }
        catch
        {
            notes.Add($"could not parse {file} — skipping compose detection");
            return null;
        }

        var overrides = new List<ComposeOverride>();

        if (TryGetMap(root, "services", out var services))
        {
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
                    var container = ContainerPort(mapping);
                    var portName = UniqueName(Sanitize(svc), used);
                    ports.Add(new PortDeclaration { Name = portName, Description = $"{svc} published port" });
                    overrides.Add(new ComposeOverride
                    {
                        Path = ["services", svc, "ports", "0"],
                        Template = $"${{sprig.ports.{portName}}}:{container}",
                    });
                }
            }
        }

        if (TryGetMap(root, "volumes", out var volumes) && volumes.Children.Count > 0)
            notes.Add($"compose declares named volume(s) [{string.Join(", ", volumes.Children.Select(v => ((YamlScalarNode)v.Key).Value))}] — " +
                      "the per-workspace project name won't isolate these, and data won't persist across 'down' unless bound to a named volume; review");

        return overrides.Count > 0 ? new ComposeConfig { File = file, Overrides = overrides } : null;
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

    static string ContainerPort(string mapping)
    {
        // "6050:5432", "6050:5432/tcp", "127.0.0.1:6050:5432", or bare "5432"
        var proto = mapping.Split('/')[0];
        var parts = proto.Split(':');
        return parts[^1];
    }

    static string Sanitize(string key)
    {
        var chars = key.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Length > 0 ? slug : "port";
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
