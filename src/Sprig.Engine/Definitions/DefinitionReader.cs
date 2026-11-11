using Sprig.Engine.Planning;
using Sprig.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Sprig.Engine.Definitions;

/// <param name="Model">Null when there's an error among <paramref name="Problems"/>.</param>
public sealed record ReadResult(DefinitionModel? Model, IReadOnlyList<PlanProblem> Problems)
{
    public bool Ok => Model is not null;
}

/// <summary>
/// Reads a <c>sprig.yaml</c> or an orchestration file into the model the planner works from. Nothing in the file runs:
/// it's read as data, older schemas are upgraded on the way in, and every problem names its line. References
/// (<c>${catalog-api.url}</c>) become the template paths the planner resolves (<c>{catalog-api.http.url}</c>), so the
/// rest of sprig never sees the file's own shape.
/// </summary>
public static partial class DefinitionReader
{
    /// <summary>The schema this sprig writes and reads natively.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Step <c>i</c> turns a file of schema <c>i + 1</c> into schema <c>i + 2</c>, on the YAML tree, so the
    /// reader below only ever sees the current shape. A step is added whenever a change would misread an older file;
    /// a new field with a default needs none.</summary>
    static readonly Func<YamlMappingNode, YamlMappingNode>[] Upgrades = [];

    public static ReadResult Read(string text, string path)
    {
        var file = new FileReader(path);
        var root = file.Parse(text);
        if (root is null) return file.Result(null);

        var schemaNode = root.Children.TryGetValue(new YamlScalarNode("schema"), out var s) ? s : null;
        if (schemaNode is null)
        {
            file.Error(root, $"the file has no schema; start it with 'schema: {CurrentSchema}'");
            return file.Result(null);
        }
        if (schemaNode is not YamlScalarNode { Value: { } schemaText } || !int.TryParse(schemaText, out var schema) || schema < 1)
        {
            file.Error(schemaNode, "schema must be a whole number, such as 1");
            return file.Result(null);
        }
        if (schema > CurrentSchema)
        {
            file.Error(schemaNode, $"the file is written for schema {schema}, and this sprig reads up to {CurrentSchema}; update sprig");
            return file.Result(null);
        }
        for (var step = schema; step < CurrentSchema; step++) root = Upgrades[step - 1](root);

        var isRepo = root.Children.ContainsKey(new YamlScalarNode("repo"));
        var isOrchestration = root.Children.ContainsKey(new YamlScalarNode("orchestration"));
        DefinitionModel? model = (isRepo, isOrchestration) switch
        {
            (true, false) => new RepoReader(file).Read(root),
            (false, true) => new OrchestrationReader(file).Read(root),
            (true, true) => file.Fail(root, "a file is a repo or an orchestration, not both: keep 'repo:' or 'orchestration:'"),
            _ => file.Fail(root, "say what the file describes: 'repo: <name>' or 'orchestration: <name>'"),
        };
        return file.Result(model);
    }

    /// <summary>The file being read: where problems are collected, with the YAML helpers every part uses.</summary>
    internal sealed class FileReader(string path)
    {
        readonly List<PlanProblem> _problems = [];

        public string Path { get; } = path;

        public YamlMappingNode? Parse(string text)
        {
            var stream = new YamlStream();
            try { stream.Load(new StringReader(text)); }
            catch (YamlException e)
            {
                _problems.Add(new PlanProblem(ProblemSeverity.Error, Clean(e), new SourceLocation(Path, (int)e.Start.Line)));
                return null;
            }
            if (stream.Documents.Count == 0)
            {
                _problems.Add(new PlanProblem(ProblemSeverity.Error, "the file is empty", new SourceLocation(Path, 1)));
                return null;
            }
            if (stream.Documents.Count > 1) Error(stream.Documents[1].RootNode, "a definition is one YAML document; remove the '---' and what follows");
            if (stream.Documents[0].RootNode is YamlMappingNode root) return root;
            Error(stream.Documents[0].RootNode, "a definition is a set of 'key: value' lines, starting with 'schema:'");
            return null;
        }

        // YamlDotNet's messages start with the position, which the problem's location already gives.
        static string Clean(YamlException e)
        {
            var message = e.InnerException?.Message ?? e.Message;
            var close = message.IndexOf("): ", StringComparison.Ordinal);
            return message.StartsWith("(Line:", StringComparison.Ordinal) && close > 0 ? message[(close + 3)..] : message;
        }

        public ReadResult Result(DefinitionModel? model)
            => new(_problems.Any(p => p.Severity == ProblemSeverity.Error) ? null : model, _problems);

        public SourceLocation At(YamlNode node) => new(Path, (int)node.Start.Line);

        public void Error(YamlNode node, string message) => Add(new PlanProblem(ProblemSeverity.Error, message, At(node)));

        public void Warn(YamlNode node, string message) => Add(new PlanProblem(ProblemSeverity.Warning, message, At(node)));

        // A value is checked where it's declared and again wherever it's used, so the same problem can come up twice.
        void Add(PlanProblem problem)
        {
            if (!_problems.Contains(problem)) _problems.Add(problem);
        }

        public T? Fail<T>(YamlNode node, string message) where T : class
        {
            Error(node, message);
            return null;
        }

        public DefinitionModel? Fail(YamlNode node, string message) => Fail<DefinitionModel>(node, message);

        /// <summary>A mapping's entries, checking each key is one of <paramref name="allowed"/>. Null when the node
        /// isn't a mapping.</summary>
        public Fields? Fields(YamlNode? node, string what, params string[] allowed)
        {
            if (node is null) return null;
            if (node is YamlScalarNode empty && IsNull(empty)) return new Fields(new YamlMappingNode());
            if (node is not YamlMappingNode map)
            {
                Error(node, $"{what} is a set of 'key: value' lines");
                return null;
            }
            var fields = new Fields(map);
            foreach (var (key, _) in map.Children)
            {
                if (key is not YamlScalarNode { Value: { } name }) { Error(key, $"a key in {what} must be plain text"); continue; }
                if (!allowed.Contains(name, StringComparer.Ordinal))
                    Error(key, $"{what} has no '{name}'. It takes {string.Join(", ", allowed.Select(a => $"'{a}'"))}");
            }
            return fields;
        }

        /// <summary>A mapping whose keys are names the author chose (apps, files, values).</summary>
        public IReadOnlyList<(string Name, YamlNode Key, YamlNode Value)> Entries(YamlNode? node, string what)
        {
            if (node is null || node is YamlScalarNode empty && IsNull(empty)) return [];
            if (node is not YamlMappingNode map)
            {
                Error(node, $"{what} is a set of 'name: …' lines");
                return [];
            }
            var entries = new List<(string, YamlNode, YamlNode)>();
            foreach (var (key, value) in map.Children)
            {
                if (key is YamlScalarNode { Value: { Length: > 0 } name }) entries.Add((name, key, value));
                else Error(key, $"a name in {what} must be plain text");
            }
            return entries;
        }

        /// <summary>A single value, as written. Null when it's missing, empty, or <c>~</c>.</summary>
        public string? Text(YamlNode? node, string what)
        {
            if (node is null) return null;
            if (node is not YamlScalarNode scalar)
            {
                Error(node, node is YamlMappingNode
                    ? $"{what} is a single value, not a set of lines. A value starting with '{{' needs quotes"
                    : $"{what} is a single value, not a list");
                return null;
            }
            return IsNull(scalar) ? null : scalar.Value;
        }

        /// <summary>A value written into a file or onto a container: as written, with nothing meaning empty.</summary>
        public string? Value(YamlNode node, string what)
            => node is YamlScalarNode scalar ? (IsNull(scalar) ? "" : scalar.Value) : Text(node, what);

        static bool IsNull(YamlScalarNode scalar)
            => scalar.Style == YamlDotNet.Core.ScalarStyle.Plain && scalar.Value is null or "" or "~" or "null" or "Null" or "NULL";

        public IReadOnlyList<(string Text, YamlNode Node)> List(YamlNode? node, string what)
        {
            if (node is null) return [];
            if (node is YamlScalarNode single && Text(single, what) is { } one) return [(one, single)];
            if (node is not YamlSequenceNode sequence)
            {
                Error(node, $"{what} is a list, such as [a, b]");
                return [];
            }
            var items = new List<(string, YamlNode)>();
            foreach (var item in sequence.Children)
                if (Text(item, $"an item of {what}") is { } text) items.Add((text, item));
            return items;
        }

        public bool? Flag(YamlNode? node, string what)
        {
            if (Text(node, what) is not { } text) return null;
            if (text is "true" or "True" or "TRUE") return true;
            if (text is "false" or "False" or "FALSE") return false;
            Error(node!, $"{what} is true or false");
            return null;
        }

        public int? Number(YamlNode? node, string what, int min, int max)
        {
            if (Text(node, what) is not { } text) return null;
            if (int.TryParse(text, out var n) && n >= min && n <= max) return n;
            Error(node!, $"{what} is a whole number from {min} to {max}");
            return null;
        }

        /// <summary>One of <paramref name="choices"/>.</summary>
        public string? Choice(YamlNode? node, string what, params string[] choices)
        {
            if (Text(node, what) is not { } text) return null;
            if (choices.Contains(text, StringComparer.Ordinal)) return text;
            Error(node!, $"{what} is one of {string.Join(", ", choices)}");
            return null;
        }

        /// <summary>A name things are referred to by: no dots, slashes or spaces, which references and keys use.</summary>
        public bool CheckName(string name, YamlNode node, string what)
        {
            if (name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')) return true;
            Error(node, $"'{name}' can't be {what}'s name: use letters, digits, '-' and '_'");
            return false;
        }

        /// <summary>A path relative to the root, with forward slashes and no <c>..</c> out of it; "" for the root.</summary>
        public string? RelativePath(YamlNode? node, string what)
        {
            if (Text(node, what) is not { } text) return null;
            var path = text.Replace('\\', '/').Trim();
            while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
            path = path.TrimEnd('/');
            if (path == ".") path = "";
            if (System.IO.Path.IsPathRooted(path) || path.Split('/').Contains(".."))
            {
                Error(node!, $"{what} is relative to the repo and stays inside it");
                return null;
            }
            return path;
        }
    }

    /// <summary>A mapping's values by key.</summary>
    internal sealed class Fields(YamlMappingNode map)
    {
        public YamlMappingNode Node { get; } = map;

        public YamlNode? this[string key] => Node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    }

    /// <summary><paramref name="value"/> as template text: braces doubled, so none of it is taken for a reference.</summary>
    internal static string Literal(string value) => value.Replace("{", "{{").Replace("}", "}}");
}
