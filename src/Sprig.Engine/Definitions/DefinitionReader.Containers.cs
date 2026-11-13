using Sprig.Model;
using YamlDotNet.RepresentationModel;

namespace Sprig.Engine.Definitions;

public static partial class DefinitionReader
{
    internal static readonly string[] Roles = ["database", "cache", "queue", "search", "mail", "storage", "identity", "proxy", "web", "api", "worker"];

    internal static readonly string[] InfraRoles = ["database", "cache", "queue", "search", "mail", "storage", "identity", "proxy"];

    /// <summary>What a preset fills in. Anything written beside <c>preset:</c> is added, or replaces the preset's.</summary>
    /// <param name="Values">Its values, with <c>{name}</c> standing for the container's name.</param>
    internal sealed record Preset(
        string Image,
        (string Name, int Container, string? Scheme)[] Ports,
        (string Key, string Value)[] Env,
        (string Name, string Target)[] Volumes,
        string Health,
        string Role,
        (string Name, string Value)[] Values);

    internal static readonly IReadOnlyDictionary<string, Preset> Presets = new Dictionary<string, Preset>(StringComparer.Ordinal)
    {
        ["postgres"] = new("postgres:17", [("tcp", 5432, null)],
            [("POSTGRES_USER", "sprig"), ("POSTGRES_PASSWORD", "sprig")], [("data", "/var/lib/postgresql/data")],
            // Over TCP, because the image's first-run setup serves only the unix socket and would pass a plain pg_isready.
            "pg_isready -h 127.0.0.1 -U sprig", "database",
            [("user", "sprig"), ("password", "sprig")]),
        ["redis"] = new("redis:7", [("tcp", 6379, null)], [], [], "redis-cli ping", "cache",
            [("address", "localhost:${{name}.tcp.port}")]),
        // Health checks run as root. If rabbitmq-diagnostics runs before the server has written its Erlang cookie, it
        // writes one as root that the server (uid 999) can't read, and the server exits at start.
        ["rabbitmq"] = new("rabbitmq:4-management", [("amqp", 5672, null), ("ui", 15672, "http")],
            [("RABBITMQ_DEFAULT_USER", "sprig"), ("RABBITMQ_DEFAULT_PASS", "sprig")], [],
            "test -f /var/lib/rabbitmq/.erlang.cookie && rabbitmq-diagnostics -q ping", "queue",
            [("user", "sprig"), ("password", "sprig"), ("amqp-url", "amqp://sprig:sprig@localhost:${{name}.amqp.port}")]),
        ["mailpit"] = new("axllent/mailpit:v1.27", [("smtp", 1025, null), ("ui", 8025, "http")], [], [], "/mailpit readyz", "mail",
            [("smtp-url", "smtp://localhost:${{name}.smtp.port}")]),
    };

    /// <summary>The containers under <c>containers:</c>, declared in <paramref name="scope"/>. Their env is read once
    /// every resource is declared, by the returned step, since a value can name any of them.</summary>
    internal static (List<ContainerModel> Containers, Action Finish) ReadContainers(FileReader file, Scope scope, YamlNode? node)
    {
        var containers = new List<ContainerModel>();
        var pending = new List<(int Index, Resource Resource, Fields Fields, Preset? Preset)>();
        foreach (var (name, key, value) in file.Entries(node, "containers"))
        {
            if (!file.CheckName(name, key, "a container")) continue;
            var resource = new Resource(name, "container", key);
            if (!scope.Declare(resource)) continue;
            if (file.Fields(value, $"container '{name}'", DefinitionKeys.Names(DefinitionKeys.Container)) is not { } fields) continue;

            Preset? preset = null;
            if (file.Choice(fields["preset"], $"container '{name}''s preset", [.. Presets.Keys]) is { } presetName) preset = Presets[presetName];
            var image = file.Text(fields["image"], $"container '{name}''s image") ?? preset?.Image;
            if (image is null)
            {
                if (fields["preset"] is null) file.Error(key, $"container '{name}' needs an image (image: redis:7) or a preset ({string.Join(", ", Presets.Keys)})");
                continue;
            }

            var ports = (preset?.Ports ?? []).Select(p => new EndpointModel(p.Name, p.Container, null, p.Scheme, file.At(key))).ToList();
            var presetPorts = (preset?.Ports ?? []).ToDictionary(p => p.Name, p => (p.Container, p.Scheme), StringComparer.Ordinal);
            foreach (var written in ReadPorts(file, fields["ports"], resource, inContainer: true, presetPorts))
            {
                var at = ports.FindIndex(p => p.Name == written.Name);
                if (at >= 0) ports[at] = written;
                else ports.Add(written);
            }
            resource.Ports.Clear();
            resource.Ports.AddRange(ports.Select(p => new PortInfo(p.Name, p.Scheme)));

            var volumes = (preset?.Volumes ?? []).Select(v => new VolumeModel(v.Name, v.Target)).ToList();
            foreach (var (volume, volumeKey, target) in file.Entries(fields["volumes"], $"container '{name}''s volumes"))
            {
                if (!file.CheckName(volume, volumeKey, "a volume") || file.Text(target, $"volume '{volume}'") is not { } path) continue;
                volumes.RemoveAll(v => v.Name == volume);
                volumes.Add(new VolumeModel(volume, path));
            }

            foreach (var v in preset?.Values ?? []) resource.Values[v.Name] = new ValueInfo(v.Value.Replace("{name}", name), key);
            ReadValues(file, fields["values"], resource);

            var health = file.Text(fields["health"], $"container '{name}''s health") ?? preset?.Health;
            var role = file.Choice(fields["role"], $"container '{name}''s role", InfraRoles) ?? preset?.Role;
            containers.Add(new ContainerModel(name, image, file.At(key), ports, [], volumes, health, role));
            pending.Add((containers.Count - 1, resource, fields, preset));
        }

        return (containers, () =>
        {
            foreach (var (index, resource, fields, preset) in pending)
            {
                var env = (preset?.Env ?? []).Select(e => new EnvModel(e.Key, Literal(e.Value), null, file.At(resource.Node))).ToList();
                foreach (var (envKey, keyNode, envValue) in file.Entries(fields["env"], $"container '{resource.Name}''s env"))
                {
                    if (file.Value(envValue, $"env {envKey}") is not { } raw || scope.Template(raw, envValue) is not { } template) continue;
                    env.RemoveAll(e => e.Key == envKey);
                    env.Add(new EnvModel(envKey, template, null, file.At(keyNode)));
                }
                containers[index] = containers[index] with { Env = env };
            }
        });
    }

    /// <summary>A resource's own <c>values:</c>, referenced as <c>${name.value}</c>.</summary>
    internal static void ReadValues(FileReader file, YamlNode? node, Resource resource)
    {
        foreach (var (name, key, value) in file.Entries(node, $"'{resource.Name}''s values"))
        {
            if (!file.CheckName(name, key, "a value")) continue;
            if (name is "port" or "url" || resource.Ports.Any(p => p.Name == name))
            {
                file.Error(key, $"'{name}' is already one of '{resource.Name}''s ports; call the value something else");
                continue;
            }
            if (file.Value(value, $"value '{name}'") is { } raw) resource.Values[name] = new ValueInfo(raw, value);
        }
    }
}
