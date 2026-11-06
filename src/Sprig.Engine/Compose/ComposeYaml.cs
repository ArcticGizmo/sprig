using System.Text;
using System.Text.RegularExpressions;
using Sprig.Engine.Planning;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Sprig.Engine.Compose;

public sealed class ComposeException(string message) : Exception(message);

/// <summary>Reads and writes compose files for workspaces. Sprig never edits a repo's own compose file; it writes a copy
/// into the workspace's store folder and runs that with the repo as the project directory.</summary>
public static partial class ComposeYaml
{
    /// <summary>The services in a compose file, and which of them fix their container name.</summary>
    public static ComposeFileInfo Inspect(string yaml)
    {
        var services = Services(Load(yaml));
        var names = services.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value!).ToArray();
        string[] With(string key) => services.Children
            .Where(kv => kv.Value is YamlMappingNode m && Find(m, key) is not null)
            .Select(kv => ((YamlScalarNode)kv.Key).Value!)
            .ToArray();
        return new ComposeFileInfo(names, With("container_name"), With("profiles"));
    }

    /// <summary>
    /// The repo's compose file as one workspace runs it: only the services the repo names, each on the host ports sprig
    /// allocated, with no fixed container names or profiles, and the definition's overrides applied.
    /// </summary>
    public static string ForWorkspace(string yaml, PlannedCompose compose)
    {
        var root = Load(yaml);
        var services = Services(root);
        var keep = compose.Services.ToDictionary(s => s.Name, StringComparer.Ordinal);

        foreach (var key in services.Children.Keys.OfType<YamlScalarNode>().ToArray())
        {
            if (!keep.ContainsKey(key.Value!)) services.Children.Remove(key);
        }

        foreach (var (name, planned) in keep)
        {
            if (Find(services, name) is not YamlMappingNode service)
                throw new ComposeException($"{compose.File} has no service named '{name}'");

            Remove(service, "profiles");
            Remove(service, "container_name");
            Remove(service, "ports");
            if (planned.Mappings.Count > 0)
                service.Children.Add(new YamlScalarNode("ports"), Ports(planned.Mappings));
            if (planned.Health is { } health)
            {
                Remove(service, "healthcheck");
                service.Children.Add(new YamlScalarNode("healthcheck"), HealthCheck(health));
            }
            PruneDependsOn(service, keep.Keys);
        }

        foreach (var o in compose.Overrides.Where(o => !o.Generated))
        {
            if (o.Value.Value is not { } value) throw new ComposeException($"{o.Path} has no value");
            SetAtPath(root, o.Path.Split('.'), value);
        }

        return Save(root);
    }

    /// <summary>A compose file for the containers a repo or orchestration declares in code. Each service is named
    /// <c>&lt;owner&gt;-&lt;name&gt;</c>, so two repos can each have a <c>pg</c>.</summary>
    public static string ForContainers(IEnumerable<PlannedContainer> containers)
    {
        var services = new YamlMappingNode();
        var volumes = new YamlMappingNode();

        foreach (var c in containers)
        {
            var name = ServiceName(c.Owner, c.Name);
            var service = new YamlMappingNode { { "image", c.Image } };

            if (c.Env.Count > 0)
            {
                var env = new YamlMappingNode();
                foreach (var e in c.Env)
                    env.Add(e.Key, Quoted((e.Value.Value ?? throw new ComposeException($"{c.Name}: {e.Key} has no value")).Replace("$", "$$")));
                service.Add("environment", env);
            }
            if (c.Mappings.Count > 0) service.Add("ports", Ports(c.Mappings));
            if (c.Mounts.Count > 0)
            {
                var mounts = new YamlSequenceNode();
                foreach (var v in c.Mounts)
                {
                    var volume = $"{name}-{v.Name}";
                    mounts.Add(Quoted($"{volume}:{v.Target}"));
                    volumes.Add(volume, new YamlMappingNode());
                }
                service.Add("volumes", mounts);
            }
            if (c.Health is { } health) service.Add("healthcheck", HealthCheck(health));
            services.Add(name, service);
        }

        var root = new YamlMappingNode { { "services", services } };
        if (volumes.Children.Count > 0) root.Add("volumes", volumes);
        return Save(root);
    }

    public static string ServiceName(string owner, string name) => Sanitise($"{owner}-{name}");

    /// <summary>A compose project name: lower case letters, digits, dashes and underscores, starting with a letter or
    /// digit.</summary>
    public static string ProjectName(params string[] parts)
        => Sanitise(string.Join("-", parts.Where(p => !string.IsNullOrEmpty(p))));

    static string Sanitise(string text)
    {
        var cleaned = NotAllowed().Replace(text.ToLowerInvariant(), "-").Trim('-', '_');
        return cleaned.Length == 0 ? "sprig" : cleaned;
    }

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex NotAllowed();

    // Quoted, because YAML 1.1 reads an unquoted 8001:5432 as a base-60 number.
    static YamlSequenceNode Ports(IEnumerable<PlannedMapping> mappings)
    {
        var ports = new YamlSequenceNode();
        foreach (var m in mappings)
        {
            if (m.Host is not { } host) throw new ComposeException($"endpoint '{m.Endpoint}' has no host port");
            ports.Add(Quoted($"127.0.0.1:{host}:{m.Target}"));
        }
        return ports;
    }

    // Checked often, for long enough to cover a slow first start (initdb, RabbitMQ's boot).
    static YamlMappingNode HealthCheck(string command) => new()
    {
        { "test", new YamlSequenceNode(Quoted("CMD-SHELL"), Quoted(command)) },
        { "interval", "2s" },
        { "timeout", "5s" },
        { "retries", "90" },
    };

    static void PruneDependsOn(YamlMappingNode service, IEnumerable<string> kept)
    {
        var keep = kept.ToHashSet(StringComparer.Ordinal);
        switch (Find(service, "depends_on"))
        {
            case YamlSequenceNode list:
                foreach (var item in list.Children.OfType<YamlScalarNode>().Where(i => !keep.Contains(i.Value!)).ToArray())
                    list.Children.Remove(item);
                if (list.Children.Count == 0) Remove(service, "depends_on");
                break;
            case YamlMappingNode map:
                foreach (var key in map.Children.Keys.OfType<YamlScalarNode>().Where(k => !keep.Contains(k.Value!)).ToArray())
                    map.Children.Remove(key);
                if (map.Children.Count == 0) Remove(service, "depends_on");
                break;
        }
    }

    /// <summary>Missing sections inside a service are created (so <c>services.pg.environment.X</c> works on a service
    /// with no environment yet), but a missing service is an error: a typo shouldn't add a half-made service.</summary>
    static void SetAtPath(YamlNode root, string[] path, string value)
    {
        var current = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var creates = i >= 2 || (i == 1 && path[0] != "services");
            current = current switch
            {
                YamlMappingNode map when Find(map, path[i]) is { } child => child,
                YamlMappingNode map when creates => Add(map, path[i]),
                YamlSequenceNode seq when int.TryParse(path[i], out var index) && index >= 0 && index < seq.Children.Count => seq.Children[index],
                _ => throw new ComposeException($"compose path {string.Join('.', path)} not found (at '{path[i]}')"),
            };
        }

        static YamlMappingNode Add(YamlMappingNode map, string key)
        {
            var child = new YamlMappingNode();
            map.Children.Add(new YamlScalarNode(key), child);
            return child;
        }

        var leaf = path[^1];
        switch (current)
        {
            case YamlMappingNode map:
                Remove(map, leaf);
                map.Children.Add(new YamlScalarNode(leaf), Quoted(value));
                break;
            case YamlSequenceNode seq when int.TryParse(leaf, out var index) && index >= 0 && index < seq.Children.Count:
                seq.Children[index] = Quoted(value);
                break;
            default:
                throw new ComposeException($"compose path {string.Join('.', path)} not found (at '{leaf}')");
        }
    }

    static YamlMappingNode Load(string yaml)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (YamlException e) { throw new ComposeException($"couldn't read the compose file: {e.Message}"); }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new ComposeException("the compose file is empty");
        return root;
    }

    static YamlMappingNode Services(YamlMappingNode root)
        => Find(root, "services") as YamlMappingNode ?? throw new ComposeException("the compose file has no services");

    static YamlNode? Find(YamlMappingNode map, string key)
        => map.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode k && k.Value == key).Value;

    static void Remove(YamlMappingNode map, string key)
    {
        var k = map.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(n => n.Value == key);
        if (k is not null) map.Children.Remove(k);
    }

    static YamlScalarNode Quoted(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };

    static string Save(YamlMappingNode root)
    {
        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        // YamlDotNet ends a document with "...", which compose accepts but people reading the file don't expect.
        var text = sb.ToString().TrimEnd();
        if (text.EndsWith("...", StringComparison.Ordinal)) text = text[..^3].TrimEnd();
        return text + "\n";
    }
}
