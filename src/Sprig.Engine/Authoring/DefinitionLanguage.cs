using Sprig.Engine.Definitions;
using Sprig.Engine.Planning;

namespace Sprig.Engine.Authoring;

/// <param name="Line">1-based.</param>
/// <param name="Severity"><c>Error</c> or <c>Warning</c>.</param>
public sealed record EditorProblem(int Start, int End, int Line, string Severity, string Message);

/// <param name="Kind">What it is, shown after it in the list (<c>app</c>, <c>key</c>, <c>folder</c>…).</param>
/// <param name="Detail">A sentence on what choosing it means.</param>
public sealed record EditorCompletion(string Label, string Kind, string? Detail = null);

/// <param name="SpanStart">Where the word being completed starts; choosing an item replaces from there to the caret.</param>
public sealed record EditorCompletions(int SpanStart, IReadOnlyList<EditorCompletion> Items);

public sealed record EditorHover(int Start, int End, string Title, string? Docs);

/// <summary>Where a definition being edited sits, for completions that come from outside its text.</summary>
/// <param name="Folder">The repo's folder for a repo definition; the orchestration file's folder for an orchestration.
/// Null when there's none to look in.</param>
/// <param name="RepoIds">The Repos list's IDs, for an orchestration's <c>use:</c>.</param>
public sealed record DefinitionContext(string? Folder, Func<IReadOnlyList<string>>? RepoIds = null);

/// <summary>
/// What the definition editor offers as you type: completion of the format's keys and choices, of names the file
/// declares (in <c>${…}</c> references, <c>wait-for</c>, <c>read-by</c>), and of what's in the repo itself (compose files
/// and their services, folders, the keys already in a file sprig will write into); problems from the reader, on their
/// lines; and on hover, what a key means or where a reference's value comes from. All of it works on text that's
/// half-typed, so none of it needs the file to parse.
/// </summary>
public sealed partial class DefinitionLanguage(DefinitionContext context)
{
    public IReadOnlyList<EditorProblem> Diagnose(string text, string path)
    {
        var lines = Lines.Of(text);
        return DefinitionReader.Read(text, path).Problems.Select(p =>
        {
            var line = Math.Clamp(p.Location?.Line ?? 1, 1, lines.Count);
            var (start, end) = lines.ContentSpan(line - 1);
            return new EditorProblem(start, end, line, p.Severity == ProblemSeverity.Error ? "Error" : "Warning", p.Message);
        }).ToArray();
    }

    public EditorCompletions? Complete(string text, int caret)
    {
        var at = Locate(text, caret);
        var outline = Outline.Of(text, at.Line);
        var items = at switch
        {
            { Reference: { } reference } => ReferenceItems(outline, reference),
            { IsKey: true } => KeyItems(outline, at.Path, at.Word, text),
            _ => ValueItems(outline, at.Path, at.Word, at.InList),
        };
        var matching = items
            .Where(i => i.Label.StartsWith(at.Word, StringComparison.OrdinalIgnoreCase) && i.Label != at.Word)
            .DistinctBy(i => i.Label)
            .ToArray();
        return matching.Length == 0 ? null : new EditorCompletions(caret - at.Word.Length, matching);
    }

    public EditorHover? Hover(string text, int offset)
    {
        var lines = Lines.Of(text);
        var line = lines.LineAt(offset);
        var (lineStart, _) = lines.Span(line);
        var lineText = lines.Text(line);
        var column = offset - lineStart;

        // Inside a ${…}: what the reference is and where its value comes from.
        for (var open = lineText.IndexOf("${", StringComparison.Ordinal); open >= 0; open = lineText.IndexOf("${", open + 2, StringComparison.Ordinal))
        {
            var close = lineText.IndexOf('}', open);
            if (close < 0 || column < open || column > close) continue;
            var reference = lineText[(open + 2)..close].Trim();
            if (DescribeReference(Outline.Of(text), reference) is { } docs)
                return new EditorHover(lineStart + open, lineStart + close + 1, "${" + reference + "}", docs);
        }

        // On a key: what it's for.
        var content = lineText.TrimStart();
        var indent = lineText.Length - content.Length;
        if (content.StartsWith("- ", StringComparison.Ordinal)) return null;
        var colon = content.IndexOf(':');
        if (colon <= 0 || column < indent || column > indent + colon) return null;
        var key = content[..colon].Trim();
        var path = Locate(text, lineStart + indent).Path;
        return KeyDocs(Outline.Of(text).IsOrchestration, path, key) is { } doc
            ? new EditorHover(lineStart + indent, lineStart + indent + colon, key, doc)
            : null;
    }
}
