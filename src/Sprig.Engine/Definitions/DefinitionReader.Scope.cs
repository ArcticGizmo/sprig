using System.Text;
using Sprig.Model;
using YamlDotNet.RepresentationModel;

namespace Sprig.Engine.Definitions;

public static partial class DefinitionReader
{
    /// <summary>A port as references see it: its name, and its scheme when it has a url.</summary>
    internal sealed record PortInfo(string Name, string? Scheme);

    /// <summary>A value a resource declares or its preset gives, as the author wrote it (or as the preset spells it).</summary>
    internal sealed record ValueInfo(string Raw, YamlNode Node);

    /// <summary>Something a reference can name: an app, a job, a container or a compose service.</summary>
    internal sealed class Resource(string name, string kind, YamlNode node)
    {
        public string Name { get; } = name;
        public string Kind { get; } = kind;
        public YamlNode Node { get; } = node;
        public List<PortInfo> Ports { get; } = [];
        public Dictionary<string, ValueInfo> Values { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// What references in one file can name, and how each turns into a planner template. A resource's own values are
    /// expanded where they're used, so the model only ever holds ports, settings, exports and the workspace's name.
    /// </summary>
    internal sealed class Scope(FileReader file)
    {
        static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "inputs", "outputs", "workspace" };

        readonly HashSet<string> _inLoops = new(StringComparer.Ordinal);

        public Dictionary<string, Resource> Resources { get; } = new(StringComparer.Ordinal);

        /// <summary>A repo's inputs; null in an orchestration, which has none of its own.</summary>
        public HashSet<string>? Inputs { get; init; }

        /// <summary>An orchestration's repos, by the name the file gives them, to the path the model keys them by.</summary>
        public Dictionary<string, string>? Repos { get; init; }

        public bool Declare(Resource resource)
        {
            if (Reserved.Contains(resource.Name))
            {
                file.Error(resource.Node, $"'{resource.Name}' is a word references use; call it something else");
                return false;
            }
            if (Resources.TryGetValue(resource.Name, out var other) || Repos?.ContainsKey(resource.Name) == true)
            {
                file.Error(resource.Node, $"'{resource.Name}' is already the name of {(other is null ? "a repo" : $"{An(other.Kind)} {other.Kind}")} here; names are shared by everything in the file");
                return false;
            }
            Resources[resource.Name] = resource;
            return true;
        }

        static string An(string kind) => kind[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an" : "a";

        /// <summary>Every declared value, used or not, so a mistake in one is reported where it's written.</summary>
        public void CheckValues()
        {
            foreach (var resource in Resources.Values)
                foreach (var (name, value) in resource.Values)
                    Template($"${{{resource.Name}.{name}}}", value.Node);
        }

        /// <summary>A value that may hold references, as a planner template. Null after reporting a problem.</summary>
        public string? Template(string raw, YamlNode node) => Template(raw, node, []);

        string? Template(string raw, YamlNode node, List<string> expanding)
        {
            var template = new StringBuilder();
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] == '$' && At(raw, i + 1, "${"))
                {
                    template.Append("${{");
                    i += 2;
                    continue;
                }
                if (raw[i] == '$' && At(raw, i, "${"))
                {
                    var end = raw.IndexOf('}', i + 2);
                    if (end < 0)
                    {
                        file.Error(node, $"'${{' at character {i + 1} has no closing '}}'");
                        return null;
                    }
                    if (Reference(raw[(i + 2)..end].Trim(), node, expanding) is not { } resolved) return null;
                    template.Append(resolved);
                    i = end;
                    continue;
                }
                template.Append(raw[i] switch { '{' => "{{", '}' => "}}", var c => c.ToString() });
            }
            return template.ToString();
        }

        static bool At(string text, int index, string what) => index + what.Length <= text.Length && string.CompareOrdinal(text, index, what, 0, what.Length) == 0;

        string? Reference(string path, YamlNode node, List<string> expanding)
        {
            var bits = path.Split('.');
            if (bits.Any(b => b.Length == 0))
            {
                file.Error(node, $"'${{{path}}}' isn't a reference; write it like ${{catalog-api.url}} or ${{inputs.home-url}}");
                return null;
            }

            if (bits[0] == "workspace")
            {
                if (bits is [_, "name"]) return "{workspace.name}";
                file.Error(node, $"'${{{path}}}': the workspace only has a name, ${{workspace.name}}");
                return null;
            }

            if (bits[0] == "inputs")
            {
                if (Inputs is null)
                {
                    file.Error(node, "an orchestration has no inputs of its own; it gives repos' inputs a value under 'inputs:'");
                    return null;
                }
                if (bits.Length != 2)
                {
                    file.Error(node, $"'${{{path}}}' names an input like ${{inputs.home-url}}");
                    return null;
                }
                if (!Inputs.Contains(bits[1]))
                {
                    file.Error(node, $"there's no input '{bits[1]}'; declare it under 'inputs:'");
                    return null;
                }
                return $"{{setting.{bits[1]}}}";
            }

            if (Repos?.TryGetValue(bits[0], out var repoPath) == true)
            {
                if (bits is [_, "outputs", var output]) return $"{{export.{repoPath}.{output}}}";
                file.Error(node, $"'${{{path}}}' names a repo; use one of its outputs, like ${{{bits[0]}.outputs.<name>}}");
                return null;
            }

            if (!Resources.TryGetValue(bits[0], out var resource))
            {
                file.Error(node, $"'{bits[0]}' isn't declared in this file" + (Inputs is not null ? "; to use something from another repo, make it an input" : ""));
                return null;
            }

            if (bits.Length == 2 && bits[1] is "port" or "url")
            {
                if (resource.Ports.Count == 0)
                {
                    file.Error(node, $"'{resource.Name}' has no ports, so no .{bits[1]}");
                    return null;
                }
                return PortReference(resource, resource.Ports[0], bits[1], node);
            }
            if (bits.Length == 2 && resource.Values.TryGetValue(bits[1], out var value))
            {
                var key = $"{resource.Name}.{bits[1]}";
                if (expanding.Contains(key))
                {
                    var loop = expanding[expanding.IndexOf(key)..];
                    // Checking each value finds the same loop from each of its members; one report is enough.
                    if (!loop.Any(_inLoops.Contains))
                        file.Error(node, $"'{key}' refers back to itself through {string.Join(" → ", loop)}");
                    _inLoops.UnionWith(loop);
                    return null;
                }
                expanding.Add(key);
                try { return Template(value.Raw, value.Node, expanding); }
                finally { expanding.Remove(key); }
            }
            if (bits.Length == 3 && resource.Ports.FirstOrDefault(p => p.Name == bits[1]) is { } port && bits[2] is "port" or "url")
                return PortReference(resource, port, bits[2], node);

            file.Error(node, $"'{resource.Name}' has no '{string.Join('.', bits[1..])}'. It has {Offers(resource)}");
            return null;
        }

        string? PortReference(Resource resource, PortInfo port, string what, YamlNode node)
        {
            if (what == "url" && port.Scheme is null)
            {
                file.Error(node, $"'{resource.Name}'s port '{port.Name}' has no url: give it a scheme, as {{ scheme: http }}");
                return null;
            }
            return $"{{{resource.Name}.{port.Name}.{what}}}";
        }

        static string Offers(Resource resource)
        {
            var offers = new List<string>();
            if (resource.Ports.Count > 0)
            {
                offers.Add(".port");
                if (resource.Ports[0].Scheme is not null) offers.Add(".url");
                foreach (var p in resource.Ports)
                    offers.Add(p.Scheme is null ? $".{p.Name}.port" : $".{p.Name}.port, .{p.Name}.url");
            }
            offers.AddRange(resource.Values.Keys.Select(k => "." + k));
            return offers.Count == 0 ? "nothing to refer to" : string.Join(", ", offers);
        }
    }

    /// <summary>Ports, as an app, container or compose service writes them.</summary>
    /// <param name="preset">A preset's ports, whose container port and scheme a port of the same name keeps unless it
    /// says otherwise.</param>
    internal static IReadOnlyList<EndpointModel> ReadPorts(FileReader file, YamlNode? node, Resource resource, bool inContainer,
        IReadOnlyDictionary<string, (int Container, string? Scheme)>? preset = null)
    {
        var endpoints = new List<EndpointModel>();
        foreach (var (name, key, value) in file.Entries(node, "ports"))
        {
            if (!file.CheckName(name, key, "a port")) continue;
            var known = preset?.TryGetValue(name, out var p) == true ? p : ((int Container, string? Scheme)?)null;
            int? container = known?.Container;
            string? scheme = known is not null ? known.Value.Scheme : name is "http" or "https" ? name : null;
            YamlNode? host = value;
            string? health = null;
            int? main = null;
            if (value is YamlMappingNode)
            {
                var fields = file.Fields(value, $"port '{name}'", DefinitionKeys.Names(inContainer ? DefinitionKeys.Port : DefinitionKeys.AppPort))!;
                host = fields["host"];
                container = file.Number(fields["container"], $"port '{name}''s container port", 1, 65535) ?? container;
                main = file.Number(fields["main"], $"port '{name}''s main port", 1, 65535);
                scheme = file.Text(fields["scheme"], $"port '{name}''s scheme") ?? scheme;
                if (!inContainer && file.Text(fields["health"], $"port '{name}''s health") is { } path)
                {
                    if (!path.StartsWith('/')) file.Error(fields["health"]!, $"port '{name}''s health is a path on it, starting with /");
                    else if (scheme is not ("http" or "https")) file.Error(fields["health"]!, $"port '{name}' has a health path but isn't http; give it scheme: http");
                    else health = path;
                }
                if (inContainer && container is null && fields["container"] is null)
                    file.Error(value, $"port '{name}' needs the port inside the container: {{ container: 5432 }}");
            }
            else if (inContainer && container is null)
            {
                file.Error(value, $"port '{name}' needs the port inside the container: {name}: {{ container: <port> }}");
                continue;
            }
            var pin = Pin(file, host, name);
            if (pin is { Ok: false }) continue;
            endpoints.Add(new EndpointModel(name, container, pin?.Spec, scheme, file.At(key), health, main));
            resource.Ports.Add(new PortInfo(name, scheme));
        }
        return endpoints;
    }

    internal sealed record PinSpec(bool Ok, string? Spec);

    /// <summary>Where on this machine a port may be: any (null), one port, or a list of ports and ranges.</summary>
    static PinSpec? Pin(FileReader file, YamlNode? node, string port)
    {
        if (node is null) return null;
        if (node is YamlScalarNode scalar)
        {
            if (file.Text(scalar, $"port '{port}'") is not { } text || text == "any") return null;
            return Part(text, scalar) ? new PinSpec(true, text) : new PinSpec(false, null);
        }
        if (node is YamlSequenceNode list)
        {
            var parts = new List<string>();
            foreach (var item in list.Children)
            {
                if (file.Text(item, $"port '{port}'") is not { } text || !Part(text, item)) return new PinSpec(false, null);
                parts.Add(text);
            }
            if (parts.Count > 0) return new PinSpec(true, string.Join(',', parts));
            file.Error(node, $"port '{port}' lists no ports");
            return new PinSpec(false, null);
        }
        file.Error(node, $"port '{port}' is any, a port, or a list such as [8000, 8110-8115]");
        return new PinSpec(false, null);

        bool Part(string text, YamlNode at)
        {
            var range = text.Split('-', 2);
            if (range.All(r => int.TryParse(r.Trim(), out var n) && n is >= 1 and <= 65535)
                && (range.Length == 1 || int.Parse(range[0]) <= int.Parse(range[1])))
                return true;
            file.Error(at, $"'{text}' isn't a port or a range such as 8110-8115");
            return false;
        }
    }
}
