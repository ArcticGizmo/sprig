namespace Sprig.Engine.Authoring;

public sealed partial class DefinitionLanguage
{
    /// <summary>The text's lines by offset, without their line breaks.</summary>
    internal sealed class Lines
    {
        readonly string _text;
        readonly List<int> _starts = [0];

        Lines(string text)
        {
            _text = text;
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n') _starts.Add(i + 1);
        }

        public static Lines Of(string text) => new(text);

        public int Count => _starts.Count;

        public int LineAt(int offset)
        {
            var index = _starts.BinarySearch(Math.Clamp(offset, 0, _text.Length));
            return index >= 0 ? index : ~index - 1;
        }

        public (int Start, int End) Span(int line)
        {
            var start = _starts[line];
            var end = line + 1 < _starts.Count ? _starts[line + 1] - 1 : _text.Length;
            if (end > start && _text[end - 1] == '\r') end--;
            return (start, end);
        }

        public string Text(int line)
        {
            var (start, end) = Span(line);
            return _text[start..end];
        }

        /// <summary>The line without its indentation, or the whole line when it's blank.</summary>
        public (int Start, int End) ContentSpan(int line)
        {
            var (start, end) = Span(line);
            var first = start;
            while (first < end && char.IsWhiteSpace(_text[first])) first++;
            return first == end ? (start, end) : (first, end);
        }
    }

    /// <summary>Where the caret is: under which keys, and whether a key, a value, a list item or a reference is being
    /// typed there.</summary>
    /// <param name="Path">The keys above the caret, outermost first; for a value, ending with its own key.</param>
    /// <param name="Word">What's typed of the thing being completed.</param>
    /// <param name="Reference">What's typed after an unclosed <c>${</c>.</param>
    internal sealed record Spot(int Line, IReadOnlyList<string> Path, string Word, bool IsKey, bool InList, string? Reference);

    internal static Spot Locate(string text, int caret)
    {
        var lines = Lines.Of(text);
        var line = lines.LineAt(caret);
        var (lineStart, _) = lines.Span(line);
        var before = lines.Text(line)[..Math.Min(caret - lineStart, lines.Text(line).Length)];

        var open = before.LastIndexOf("${", StringComparison.Ordinal);
        if (open >= 0 && before.IndexOf('}', open) < 0)
        {
            var reference = before[(open + 2)..];
            return new Spot(line, [], reference[(reference.LastIndexOf('.') + 1)..], false, false, reference);
        }

        var content = before.TrimStart();
        var indent = before.Length - content.Length;
        var item = content.StartsWith("- ", StringComparison.Ordinal) || content == "-";
        if (item)
        {
            content = content.Length > 1 ? content[2..] : "";
            indent += 2;
        }
        var parents = Parents(lines, line, indent);

        var colon = KeyColon(content);
        if (colon < 0)
            return item
                ? new Spot(line, parents, content.Trim(), false, true, null)
                : new Spot(line, parents, Unquote(content.Trim()), true, false, null);

        var path = new List<string>(parents) { Unquote(content[..colon].Trim()) };
        return Flow(line, path, content[(colon + 1)..]);
    }

    /// <summary>A value, which may be a flow mapping or list (<c>{ http: any }</c>, <c>[a, b]</c>) still being typed.</summary>
    static Spot Flow(int line, List<string> path, string rest)
    {
        var frames = new Stack<(char Kind, List<string> Path)>();
        var segment = "";
        string? innerKey = null;
        foreach (var c in rest)
        {
            switch (c)
            {
                case '{' or '[':
                    var at = frames.Count == 0 ? path : frames.Peek().Kind == '{' && innerKey is not null ? [.. frames.Peek().Path, innerKey] : frames.Peek().Path;
                    frames.Push((c, at));
                    segment = "";
                    innerKey = null;
                    break;
                case '}' or ']':
                    if (frames.Count > 0) frames.Pop();
                    segment = "";
                    innerKey = null;
                    break;
                case ',':
                    segment = "";
                    innerKey = null;
                    break;
                case ':' when frames.Count > 0 && frames.Peek().Kind == '{' && innerKey is null:
                    innerKey = Unquote(segment.Trim());
                    segment = "";
                    break;
                default:
                    segment += c;
                    break;
            }
        }
        var word = Unquote(segment.TrimStart());
        if (frames.Count == 0) return new Spot(line, path, word, false, false, null);
        var (kind, framePath) = frames.Peek();
        if (kind == '[') return new Spot(line, framePath, word, false, true, null);
        return innerKey is null
            ? new Spot(line, framePath, word, true, false, null)
            : new Spot(line, [.. framePath, innerKey], word, false, false, null);
    }

    /// <summary>The keys of the lines that hold <paramref name="line"/>, by their smaller indentation.</summary>
    static List<string> Parents(Lines lines, int line, int indent)
    {
        var parents = new List<string>();
        var want = indent;
        for (var i = line - 1; i >= 0 && want > 0; i--)
        {
            var text = lines.Text(i);
            var content = text.TrimStart();
            if (content.Length == 0 || content.StartsWith('#')) continue;
            var at = text.Length - content.Length;
            if (at >= want) continue;
            want = at;
            if (content.StartsWith("- ", StringComparison.Ordinal)) continue;
            var colon = KeyColon(content);
            if (colon > 0) parents.Insert(0, Unquote(content[..colon].Trim()));
        }
        return parents;
    }

    /// <summary>Where a line's key ends: the first <c>:</c> followed by a space or the end, outside quotes.</summary>
    internal static int KeyColon(string content)
    {
        char? quote = null;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (quote is not null) { if (c == quote) quote = null; continue; }
            if (c is '\'' or '"' && i == 0) { quote = c; continue; }
            if (c is '{' or '[') return -1;
            if (c == ':' && (i + 1 == content.Length || content[i + 1] == ' ')) return i;
        }
        return -1;
    }

    static string Unquote(string text)
        => text.Length >= 2 && text[0] is '\'' or '"' && text[^1] == text[0] ? text[1..^1] : text.TrimStart('\'', '"');
}
