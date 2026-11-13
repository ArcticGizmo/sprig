using Sprig.Engine.Definitions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Sprig.Engine.Authoring;

public sealed partial class DefinitionLanguage
{
    internal sealed record OutlinePort(string Name, string? Scheme);

    internal sealed class OutlineResource(string name, string kind)
    {
        public string Name { get; } = name;
        public string Kind { get; } = kind;
        public List<OutlinePort> Ports { get; } = [];
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public string? Path { get; set; }
    }

    internal sealed record OutlineInput(string Name, string Type, string? Default);

    /// <summary>What a definition declares, read leniently from text that may be mid-edit: names for completion and
    /// hover, never for running.</summary>
    internal sealed class Outline
    {
        public bool IsOrchestration { get; private set; }
        public Dictionary<string, OutlineResource> Resources { get; } = new(StringComparer.Ordinal);
        public List<OutlineInput> Inputs { get; } = [];
        public Dictionary<string, (string? Path, string? Use)> Repos { get; } = new(StringComparer.Ordinal);

        /// <param name="editing">The line being typed on, the likeliest not to parse yet; -1 for none.</param>
        public static Outline Of(string text, int editing = -1)
        {
            var outline = new Outline { IsOrchestration = text.Contains("\norchestration:", StringComparison.Ordinal) || text.StartsWith("orchestration:", StringComparison.Ordinal) };
            if (Parse(text, editing) is { } root) outline.Read(root);
            return outline;
        }

        /// <summary>
        /// The text as YAML, leaving out what stops it parsing so the rest still counts: first the line being typed,
        /// then lines with a '{' or '[' not yet closed (an open one swallows the lines after it), then each line the
        /// parser stops at.
        /// </summary>
        static YamlMappingNode? Parse(string text, int editing)
        {
            var lines = text.Split('\n');
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (TryParse(lines, out var root, out var at)) return root;
                if (attempt == 0 && editing >= 0 && editing < lines.Length && lines[editing].Length > 0)
                    lines[editing] = "";
                else if (attempt <= 1 && lines.Any(Unclosed))
                    for (var i = 0; i < lines.Length; i++) { if (Unclosed(lines[i])) lines[i] = ""; }
                else if (at is { } line && line >= 0 && line < lines.Length && lines[line].Length > 0)
                    lines[line] = "";
                else return null;
            }
            return null;
        }

        static bool TryParse(string[] lines, out YamlMappingNode? root, out int? errorLine)
        {
            root = null;
            errorLine = null;
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(string.Join('\n', lines)));
                root = stream.Documents.FirstOrDefault()?.RootNode as YamlMappingNode;
                return true;
            }
            catch (YamlException e)
            {
                errorLine = (int)e.Start.Line - 1;
                return false;
            }
            // Thrown, with no position, when the text ends inside a '{' or '['.
            catch (InvalidOperationException)
            {
                errorLine = Array.FindLastIndex(lines, l => l.Trim().Length > 0);
                return false;
            }
        }

        /// <summary>More '{' or '[' than close on the line, outside a comment and the line's ${…} references.</summary>
        static bool Unclosed(string line)
        {
            var depth = 0;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (c == '#' && (i == 0 || line[i - 1] == ' ')) break;
                if (c == '$' && i + 1 < line.Length && line[i + 1] == '{' && line.IndexOf('}', i) is var close && close > 0) { i = close; continue; }
                if (c is '{' or '[') depth++;
                else if (c is '}' or ']') depth--;
            }
            return depth > 0;
        }

        static YamlNode? Get(YamlNode? node, string key)
            => node is YamlMappingNode map && map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

        static IEnumerable<(string Name, YamlNode Value)> Entries(YamlNode? node)
            => node is YamlMappingNode map
                ? map.Children.Where(c => c.Key is YamlScalarNode { Value.Length: > 0 }).Select(c => (((YamlScalarNode)c.Key).Value!, c.Value))
                : [];

        static string? Text(YamlNode? node) => node is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

        void Read(YamlMappingNode root)
        {
            IsOrchestration = Get(root, "orchestration") is not null;

            foreach (var (file, compose) in Entries(Get(root, "compose")))
                foreach (var (name, service) in Entries(Get(compose, "services")))
                    Add(new OutlineResource(name, $"compose service in {file}"), service, null);

            foreach (var (name, container) in Entries(Get(root, "containers")))
            {
                var preset = Text(Get(container, "preset")) is { } p && DefinitionReader.Presets.TryGetValue(p, out var known) ? known : null;
                var resource = new OutlineResource(name, preset is null ? "container" : $"{Text(Get(container, "preset"))} container");
                foreach (var port in preset?.Ports ?? []) resource.Ports.Add(new OutlinePort(port.Name, port.Scheme));
                foreach (var value in preset?.Values ?? []) resource.Values[value.Name] = value.Value.Replace("{name}", name);
                Add(resource, container, preset);
            }

            foreach (var (name, app) in Entries(Get(root, "apps")))
            {
                var job = Text(Get(app, "once")) == "true";
                var resource = new OutlineResource(name, job ? "job" : Text(Get(app, "run")) is { } run ? $"{run} app" : "app")
                {
                    Path = Text(Get(app, "path")) ?? ".",
                };
                Add(resource, app, null);
            }

            foreach (var (name, input) in Entries(Get(root, "inputs")))
                Inputs.Add(new OutlineInput(name, Text(Get(input, "type")) ?? "text", Text(Get(input, "default"))));

            foreach (var (name, repo) in Entries(Get(root, "repos")))
                Repos[name] = (Text(Get(repo, "path")), Text(Get(repo, "use")));
        }

        void Add(OutlineResource resource, YamlNode node, DefinitionReader.Preset? preset)
        {
            foreach (var (port, spec) in Entries(Get(node, "ports")))
            {
                var at = resource.Ports.FindIndex(p => p.Name == port);
                var scheme = Text(Get(spec, "scheme")) ?? (port is "http" or "https" ? port : at >= 0 ? resource.Ports[at].Scheme : null);
                if (at >= 0) resource.Ports[at] = new OutlinePort(port, scheme);
                else resource.Ports.Add(new OutlinePort(port, scheme));
            }
            foreach (var (value, raw) in Entries(Get(node, "values")))
                if (Text(raw) is { } text) resource.Values[value] = text;
            Resources.TryAdd(resource.Name, resource);
        }
    }
}
