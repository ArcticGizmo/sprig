using System.Text;
using Sprig.Core.Config;
using Sprig.Core.Substitution;
using YamlDotNet.RepresentationModel;

namespace Sprig.Core.Compose;

/// <summary>Thrown when the source compose YAML can't be parsed or an override path doesn't exist.</summary>
public sealed class ComposeException(string message) : Exception(message);

/// <summary>
/// Produces a per-instance compose file by applying the declared path-based overrides to the
/// source YAML (values resolved via the substitution engine). Only the targeted scalars change;
/// everything else is preserved. The generated file is stored centrally and always run with
/// <c>--project-directory &lt;worktree&gt;</c> so relative paths resolve there (see S2).
/// </summary>
public sealed class ComposeGenerator
{
    /// <summary>Apply overrides to <paramref name="sourceYaml"/> and return the generated YAML text.</summary>
    public string Generate(string sourceYaml, ComposeConfig compose, IVariableSource scope)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(sourceYaml))
        {
            try { stream.Load(reader); }
            catch (Exception ex) { throw new ComposeException($"could not parse compose YAML: {ex.Message}"); }
        }

        if (stream.Documents.Count == 0)
            throw new ComposeException("compose file is empty");

        var root = stream.Documents[0].RootNode;
        foreach (var over in compose.Overrides)
        {
            var value = SubstitutionEngine.Resolve(over.Template, scope);
            SetAtPath(root, over.Path, value);
        }

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            stream.Save(writer, assignAnchors: false);
        return sb.ToString();
    }

    /// <summary>Read the source compose file, apply overrides, write to <paramref name="destPath"/>; returns it.</summary>
    public string GenerateToFile(string sourceComposePath, ComposeConfig compose, IVariableSource scope, string destPath)
    {
        if (!File.Exists(sourceComposePath))
            throw new ComposeException($"compose file not found: {sourceComposePath}");

        var yaml = Generate(File.ReadAllText(sourceComposePath), compose, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.WriteAllText(destPath, yaml);
        return destPath;
    }

    static void SetAtPath(YamlNode root, IReadOnlyList<string> path, string value)
    {
        var current = root;
        for (var i = 0; i < path.Count - 1; i++)
            current = Descend(current, path, i);
        SetLeaf(current, path, value);
    }

    static YamlNode Descend(YamlNode node, IReadOnlyList<string> path, int i)
    {
        var seg = path[i];
        switch (node)
        {
            case YamlMappingNode map when TryGetMapValue(map, seg, out var child):
                return child;
            case YamlSequenceNode seq when int.TryParse(seg, out var idx) && idx >= 0 && idx < seq.Children.Count:
                return seq.Children[idx];
            default:
                throw PathError(path, i);
        }
    }

    static void SetLeaf(YamlNode parent, IReadOnlyList<string> path, string value)
    {
        var seg = path[^1];
        switch (parent)
        {
            case YamlMappingNode map:
                var existing = FindKey(map, seg);
                if (existing is not null) map.Children[existing] = new YamlScalarNode(value);
                else map.Children.Add(new YamlScalarNode(seg), new YamlScalarNode(value));
                break;
            case YamlSequenceNode seq when int.TryParse(seg, out var idx) && idx >= 0 && idx < seq.Children.Count:
                seq.Children[idx] = new YamlScalarNode(value);
                break;
            default:
                throw PathError(path, path.Count - 1);
        }
    }

    static bool TryGetMapValue(YamlMappingNode map, string key, out YamlNode value)
    {
        var k = FindKey(map, key);
        if (k is not null) { value = map.Children[k]; return true; }
        value = null!;
        return false;
    }

    static YamlScalarNode? FindKey(YamlMappingNode map, string key)
        => map.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == key);

    static ComposeException PathError(IReadOnlyList<string> path, int i)
        => new($"compose override path not found: [{string.Join(", ", path)}] (at segment '{path[i]}')");
}
