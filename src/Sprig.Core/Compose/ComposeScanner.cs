using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Sprig.Core.Compose;

/// <summary>What a compose value is, when sprig recognises it — drives smart defaults in the editor.
/// Every other scalar is a plain <see cref="Value"/> that can still be templated.</summary>
public enum ComposeTokenKind
{
    /// <summary>An unrecognised scalar — templatable, no special default.</summary>
    Value,

    /// <summary>A service's <c>container_name</c>.</summary>
    ContainerName,

    /// <summary>A published (fixed host) port entry.</summary>
    PublishedPort,

    /// <summary>A named-volume reference (templating it also renames the definition).</summary>
    NamedVolume,
}

/// <summary>
/// One templatable scalar located in the compose text: the exact span
/// (<see cref="Line"/>/<see cref="StartColumn"/> are 0-based) so a view can slice the raw line, plus
/// <see cref="Path"/> — the ordered map-key / list-index segments locating it in the document (the
/// same key a <see cref="Config.ComposeOverride"/> is stored under). <see cref="Kind"/> and the
/// optional hints are set when sprig recognises the value.
/// </summary>
public sealed record ComposeToken(
    ComposeTokenKind Kind,
    string? Service,
    int Line,
    int StartColumn,
    int Length,
    string Text,
    IReadOnlyList<string> Path,
    int? TargetPort = null,
    string? VolumeName = null);

/// <summary>A line of the compose file plus the tokens found on it, ordered left to right.</summary>
public sealed record ComposeOutlineLine(string Text, IReadOnlyList<ComposeToken> Tokens);

/// <summary>
/// The compose file rendered as lines with located value tokens. When <see cref="Parsed"/> is false
/// the file couldn't be parsed (see <see cref="Error"/>) and no tokens are offered — the lines are
/// still present so a view can show the file read-only.
/// </summary>
public sealed class ComposeOutline
{
    public IReadOnlyList<ComposeOutlineLine> Lines { get; }
    public IReadOnlyList<ComposeToken> Tokens { get; }
    public bool Parsed { get; }
    public string? Error { get; }

    public ComposeOutline(IReadOnlyList<ComposeOutlineLine> lines, IReadOnlyList<ComposeToken> tokens, bool parsed, string? error)
    {
        Lines = lines;
        Tokens = tokens;
        Parsed = parsed;
        Error = error;
    }
}

/// <summary>
/// Locates every templatable scalar in a compose file's <em>text</em>, so an editor can overlay a
/// clickable token on each value the user actually wrote, keyed by a stable path. Uses YamlDotNet's
/// representation model (<see cref="YamlStream"/>), whose nodes carry source marks, to find the exact
/// span of each value. The top-level <c>name</c> is left out (project isolation owns it). Read-only.
/// </summary>
public static class ComposeScanner
{
    public static ComposeOutline Scan(string composeText)
    {
        composeText ??= string.Empty;
        var (lineTexts, lineStarts) = SplitLines(composeText);

        YamlMappingNode? root = null;
        string? error = null;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(composeText));
            if (stream.Documents.Count > 0)
                root = stream.Documents[0].RootNode as YamlMappingNode;
        }
        catch (YamlException ex)
        {
            error = ex.Message;
        }

        var tokens = new List<ComposeToken>();
        if (root is not null)
        {
            var namedVolumes = NamedVolumes(root);
            WalkMapping(root, new List<string>(), composeText, lineStarts, namedVolumes, isRoot: true, tokens);
        }

        var byLine = tokens
            .GroupBy(t => t.Line)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ComposeToken>)g.OrderBy(t => t.StartColumn).ToList());

        var lines = new List<ComposeOutlineLine>(lineTexts.Count);
        for (var i = 0; i < lineTexts.Count; i++)
            lines.Add(new ComposeOutlineLine(
                lineTexts[i],
                byLine.TryGetValue(i, out var ts) ? ts : Array.Empty<ComposeToken>()));

        return new ComposeOutline(lines, tokens, error is null, error);
    }

    // -- walk -----------------------------------------------------------------

    private static void WalkNode(
        YamlNode node, List<string> path, string text, IReadOnlyList<int> lineStarts,
        HashSet<string> namedVolumes, List<ComposeToken> tokens)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                Emit(scalar, path, text, lineStarts, namedVolumes, tokens);
                break;
            case YamlMappingNode map:
                WalkMapping(map, path, text, lineStarts, namedVolumes, isRoot: false, tokens);
                break;
            case YamlSequenceNode seq:
                for (var i = 0; i < seq.Children.Count; i++)
                {
                    path.Add(i.ToString());
                    WalkNode(seq.Children[i], path, text, lineStarts, namedVolumes, tokens);
                    path.RemoveAt(path.Count - 1);
                }
                break;
        }
    }

    private static void WalkMapping(
        YamlMappingNode map, List<string> path, string text, IReadOnlyList<int> lineStarts,
        HashSet<string> namedVolumes, bool isRoot, List<ComposeToken> tokens)
    {
        foreach (var (keyNode, valueNode) in map.Children)
        {
            if ((keyNode as YamlScalarNode)?.Value is not { } key)
                continue;
            if (isRoot && key == "name")
                continue; // project name is owned by the per-workspace project toggle, not a value token

            path.Add(key);
            WalkNode(valueNode, path, text, lineStarts, namedVolumes, tokens);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static void Emit(
        YamlScalarNode node, List<string> path, string text, IReadOnlyList<int> lineStarts,
        HashSet<string> namedVolumes, List<ComposeToken> tokens)
    {
        var (start, end) = Span(node, text);
        if (end <= start)
            return;
        var (line, column) = Locate(lineStarts, start);
        var (kind, service, target, volume) = Classify(path, node.Value, namedVolumes);
        tokens.Add(new ComposeToken(kind, service, line, column, end - start, text[start..end], new List<string>(path), target, volume));
    }

    /// <summary>Recognise the isolation-relevant kinds from a value's path, for smarter defaults.</summary>
    private static (ComposeTokenKind Kind, string? Service, int? Target, string? Volume) Classify(
        List<string> path, string? value, HashSet<string> namedVolumes)
    {
        // services.<svc>.container_name
        if (path.Count == 3 && path[0] == "services" && path[2] == "container_name")
            return (ComposeTokenKind.ContainerName, path[1], null, null);

        // services.<svc>.ports[i]  (short form "host:container")
        if (path.Count == 4 && path[0] == "services" && path[2] == "ports"
            && value is { } portValue && ParseShortTarget(portValue) is { } t)
            return (ComposeTokenKind.PublishedPort, path[1], t, null);

        // services.<svc>.volumes[i]  (named-volume source)
        if (path.Count == 4 && path[0] == "services" && path[2] == "volumes" && value is { } volValue)
        {
            var source = volValue.Split(':')[0];
            if (namedVolumes.Contains(source))
                return (ComposeTokenKind.NamedVolume, path[1], null, source);
        }

        return (ComposeTokenKind.Value, ServiceOf(path), null, null);
    }

    private static string? ServiceOf(List<string> path)
        => path.Count >= 2 && path[0] == "services" ? path[1] : null;

    private static HashSet<string> NamedVolumes(YamlMappingNode root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (GetChild(root, "volumes") is YamlMappingNode volumes)
            foreach (var key in volumes.Children.Keys)
                if (key is YamlScalarNode { Value: { } v })
                    names.Add(v);
        return names;
    }

    // -- helpers --------------------------------------------------------------

    private static YamlNode? GetChild(YamlMappingNode map, string key)
    {
        foreach (var (k, v) in map.Children)
            if (k is YamlScalarNode { Value: { } value } && value == key)
                return v;
        return null;
    }

    /// <summary>
    /// The scalar's span in the raw text. YamlDotNet's end mark can run to the next token, so trailing
    /// whitespace is trimmed back off the slice.
    /// </summary>
    private static (int Start, int End) Span(YamlNode node, string text)
    {
        var start = (int)node.Start.Index;
        var end = (int)node.End.Index;
        if (start < 0 || end > text.Length || end <= start)
            return (start, start);
        var slice = text[start..end];
        return (start, start + slice.TrimEnd().Length);
    }

    /// <summary>The container-side port of a short-form entry that publishes a fixed host port, else null.</summary>
    private static int? ParseShortTarget(string shortForm)
    {
        var core = shortForm.Split('/')[0]; // drop any /proto
        var parts = core.Split(':');
        if (parts.Length < 2) // a bare "5432" is not published
            return null;
        return int.TryParse(parts[^1], out var target) ? target : null;
    }

    private static (List<string> Lines, List<int> Starts) SplitLines(string text)
    {
        var lines = new List<string>();
        var starts = new List<int>();
        var pos = 0;
        while (true)
        {
            var nl = text.IndexOf('\n', pos);
            if (nl < 0)
            {
                starts.Add(pos);
                lines.Add(text[pos..]);
                break;
            }
            var end = nl > pos && text[nl - 1] == '\r' ? nl - 1 : nl;
            starts.Add(pos);
            lines.Add(text[pos..end]);
            pos = nl + 1;
        }
        return (lines, starts);
    }

    private static (int Line, int Column) Locate(IReadOnlyList<int> starts, int index)
    {
        int lo = 0, hi = starts.Count - 1, line = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (starts[mid] <= index) { line = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return (line, index - starts[line]);
    }
}
