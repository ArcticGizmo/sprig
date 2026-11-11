using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Sprig.Engine.Authoring;

public sealed record EditResult(bool Ok, string Text, string? Error = null)
{
    public static EditResult Fail(string text, string error) => new(false, text, error);
}

/// <summary>
/// Makes the small edits the app offers directly in a definition's YAML. The YAML parser finds where the edit goes,
/// then the edit is plain text replaced at that spot, so every other character of the file, comments and layout
/// included, is left exactly as written.
/// </summary>
public static class DefinitionEditor
{
    /// <summary>Turn an orchestration's repo at <paramref name="repoPath"/> into one used by its ID on the Repos list:
    /// <c>path: ../shop-web</c> becomes <c>use: shop-web</c>, under the same name.</summary>
    public static EditResult RepoToUseRepo(string text, string repoPath, string id)
    {
        if (Repos(text) is not { } repos) return EditResult.Fail(text, "the orchestration has no repos: to change");
        foreach (var (_, value) in repos.Children)
            if (Entry(value, "path") is { } path && Same(path.Value.Value, repoPath))
                return Apply(text, path.Key.Start, path.Value.End, $"use: {YamlText.Scalar(id)}");
        return EditResult.Fail(text, $"no repo with path: {repoPath} to change");
    }

    /// <summary>Change which Repos-list entry an orchestration's <c>use:</c> names.</summary>
    public static EditResult SetUseRepo(string text, string from, string to)
    {
        if (Repos(text) is not { } repos) return EditResult.Fail(text, "the orchestration has no repos: to change");
        foreach (var (_, value) in repos.Children)
            if (Entry(value, "use") is { } use && use.Value.Value == from)
                return Apply(text, use.Value.Start, use.Value.End, YamlText.Scalar(to));
        return EditResult.Fail(text, $"no repo with use: {from} to change");
    }

    /// <summary>The Repos-list IDs an orchestration names with <c>use:</c>; none when it can't be read.</summary>
    public static IReadOnlyList<string> UsedIds(string text)
        => Repos(text)?.Children.Select(c => Entry(c.Value, "use")?.Value.Value).OfType<string>().ToArray() ?? [];

    static YamlMappingNode? Repos(string text)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (Exception e) when (e is YamlException or InvalidOperationException) { return null; }
        return stream.Documents.FirstOrDefault()?.RootNode is YamlMappingNode root
               && root.Children.TryGetValue(new YamlScalarNode("repos"), out var repos)
            ? repos as YamlMappingNode
            : null;
    }

    static (YamlScalarNode Key, YamlScalarNode Value)? Entry(YamlNode repo, string key)
        => repo is YamlMappingNode map && map.Children.FirstOrDefault(c => c.Key is YamlScalarNode { Value: var k } && k == key) is
            { Key: YamlScalarNode found, Value: YamlScalarNode value }
            ? (found, value)
            : null;

    static bool Same(string? a, string b) => a is not null && a.Replace('\\', '/').TrimEnd('/') == b.Replace('\\', '/').TrimEnd('/');

    static EditResult Apply(string text, Mark start, Mark end, string replacement)
    {
        var updated = string.Concat(text.AsSpan(0, (int)start.Index), replacement, text.AsSpan((int)end.Index));
        try { new YamlStream().Load(new StringReader(updated)); }
        catch (Exception e) when (e is YamlException or InvalidOperationException) { return EditResult.Fail(text, "the edit would break the file: " + e.Message); }
        return new EditResult(true, updated);
    }
}
