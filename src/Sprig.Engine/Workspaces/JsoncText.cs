using System.Text;
using System.Text.Json;

namespace Sprig.Engine.Workspaces;

/// <summary>
/// A JSON settings file edited as text: setting a value changes only that value's characters, a new key goes in beside
/// its section's last one, and removing a key takes only its line. Comments, key order and the file's own layout all
/// stay, which a parse-and-rewrite can't promise: .NET's JSON writer drops comments, and .NET's settings files allow
/// them. Every edit parses the text afresh, so positions are never stale.
/// </summary>
public sealed class JsoncText
{
    public string Text { get; private set; }

    public JsoncText(string text)
    {
        Text = text.Trim().Length == 0 ? "{\n}\n" : text;
        _crlf = Text.Contains("\r\n", StringComparison.Ordinal);
        _root = Parse();
    }

    /// <summary>The file's line endings, which what's added follows.</summary>
    readonly bool _crlf;

    /// <summary>Writes <c>&amp;</c>, <c>+</c> and other characters as they are rather than as <c>&</c>: these files
    /// are read by the app's configuration, never put into a web page.</summary>
    public static readonly JsonSerializerOptions Readable = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary><paramref name="value"/> as a JSON string.</summary>
    public static string Quote(string value) => JsonSerializer.Serialize(value, Readable);

    /// <summary>A value: an object's properties are read; anything else is a span of text.</summary>
    sealed record Node(int Start, int End, List<Prop>? Props);

    /// <param name="Start">Where the property's name starts.</param>
    sealed record Prop(string Name, int Start, Node Value);

    Node _root;

    static readonly JsonReaderOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    Node Parse()
    {
        var bytes = Encoding.UTF8.GetBytes(Text);
        var reader = new Utf8JsonReader(bytes, Options);
        // Positions come back in bytes; edits are made in characters.
        int Chars(long at) => Encoding.UTF8.GetCharCount(bytes, 0, (int)at);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidOperationException("it isn't a JSON object");
            return Read(ref reader);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"it isn't valid JSON: {e.Message}");
        }

        Node Read(ref Utf8JsonReader r)
        {
            var start = Chars(r.TokenStartIndex);
            if (r.TokenType == JsonTokenType.StartArray)
            {
                r.Skip();
                return new Node(start, Chars(r.TokenStartIndex) + 1, null);
            }
            if (r.TokenType != JsonTokenType.StartObject) return new Node(start, Chars(r.BytesConsumed), null);
            var props = new List<Prop>();
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var name = r.GetString()!;
                var nameStart = Chars(r.TokenStartIndex);
                r.Read();
                props.Add(new Prop(name, nameStart, Read(ref r)));
            }
            return new Node(start, Chars(r.TokenStartIndex) + 1, props);
        }
    }

    void Splice(int at, int length, string with)
    {
        if (_crlf) with = with.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Text = Text[..at] + with + Text[(at + length)..];
        _root = Parse();
    }

    Prop? Find(IReadOnlyList<string> path, out Node parent)
    {
        parent = _root;
        for (var i = 0; i < path.Count; i++)
        {
            var prop = parent.Props?.FirstOrDefault(p => p.Name == path[i]);
            if (prop is null) return null;
            if (i == path.Count - 1) return prop;
            if (prop.Value.Props is null) return null;
            parent = prop.Value;
        }
        return null;
    }

    /// <summary>The value at <paramref name="path"/> as written, or null when there's none.</summary>
    public string? Get(IReadOnlyList<string> path)
        => Find(path, out _) is { } prop ? Text[prop.Value.Start..prop.Value.End] : null;

    /// <summary>Whether <paramref name="path"/> is an object with nothing in it.</summary>
    public bool IsEmptyObject(IReadOnlyList<string> path)
        => (path.Count == 0 ? _root : Find(path, out _)?.Value) is { Props.Count: 0 };

    public bool RootIsEmpty => _root.Props!.Count == 0;

    /// <summary>The names in the object at <paramref name="path"/>, in order.</summary>
    public IReadOnlyList<string> Names(IReadOnlyList<string> path)
        => (path.Count == 0 ? _root : Find(path, out _)?.Value)?.Props?.Select(p => p.Name).ToArray() ?? [];

    /// <summary>
    /// Give <paramref name="path"/> the value <paramref name="json"/> (written JSON, which may span lines). An existing
    /// value has its characters replaced; otherwise the key goes in after its section's last one, or first with
    /// <paramref name="first"/>, making any sections on the way. A section in the way that isn't an object is replaced.
    /// </summary>
    public void Set(IReadOnlyList<string> path, string json, bool first = false)
    {
        var parent = _root;
        for (var i = 0; i < path.Count; i++)
        {
            var prop = parent.Props!.FirstOrDefault(p => p.Name == path[i]);
            var rest = path.Skip(i).ToArray();
            if (prop is null)
            {
                Insert(parent, rest, json, first && i == path.Count - 1);
                return;
            }
            if (i == path.Count - 1)
            {
                Splice(prop.Value.Start, prop.Value.End - prop.Value.Start, Indented(json, IndentOf(prop.Start)));
                return;
            }
            if (prop.Value.Props is null)
            {
                Splice(prop.Value.Start, prop.Value.End - prop.Value.Start, Indented(Nested(path.Skip(i + 1).ToArray(), json), IndentOf(prop.Start)));
                return;
            }
            parent = prop.Value;
        }
    }

    /// <summary>Take <paramref name="path"/>'s key out with its line and its comma, then any section that leaves empty.
    /// Comments on the lines above it stay.</summary>
    public void Remove(IReadOnlyList<string> path)
    {
        if (Find(path, out var parent) is not { } prop) return;
        var index = parent.Props!.IndexOf(prop);
        var start = LineStartIfBlank(prop.Start);
        var end = prop.Value.End;
        var comma = NextCode(end);
        if (comma < Text.Length && Text[comma] == ',') end = comma + 1;
        else if (index > 0 && PreviousComma(prop.Start) is { } before)
        {
            // The last key: the comma before it would be left trailing.
            Splice(start, EndOfLine(end) - start, "");
            Splice(before, 1, "");
            Prune(path);
            return;
        }
        Splice(start, EndOfLine(end) - start, "");
        Prune(path);
    }

    void Prune(IReadOnlyList<string> path)
    {
        if (path.Count > 1 && IsEmptyObject(path.Take(path.Count - 1).ToArray())) Remove(path.Take(path.Count - 1).ToArray());
    }

    /// <summary>What follows <paramref name="prefix"/> in sprig's comment on <paramref name="path"/>'s key: a <c>//</c>
    /// line just above it, or, for a key that shares its line, a <c>/* */</c> just before it. Null when there's none.</summary>
    public string? CommentAbove(IReadOnlyList<string> path, string prefix)
        => CommentOf(path, prefix) is { } c ? c.Content[prefix.Length..] : null;

    /// <summary>A comment for <paramref name="path"/>'s key, placed so it can come out again leaving the text exactly as
    /// it was: its own line above a key that starts a line, else just before the key.</summary>
    public void AddCommentAbove(IReadOnlyList<string> path, string comment)
    {
        if (Find(path, out _) is not { } prop) return;
        if (AtLineStart(prop.Start)) Splice(LineStartIfBlank(prop.Start), 0, $"{IndentOf(prop.Start)}// {comment}\n");
        // Inside a block comment, "*/" would end it early; "\/" is the same character to JSON.
        else Splice(prop.Start, 0, $"/* {comment.Replace("*/", "*\\/")} */ ");
    }

    public void RemoveCommentAbove(IReadOnlyList<string> path, string prefix)
    {
        if (CommentOf(path, prefix) is { } c) Splice(c.Start, c.End - c.Start, "");
    }

    /// <param name="Start">Where removing it starts.</param>
    /// <param name="End">Where removing it ends: past its line for a line comment, at the key for one before it.</param>
    /// <param name="Content">Its text, without the comment's own markers.</param>
    readonly record struct Comment(int Start, int End, string Content);

    Comment? CommentOf(IReadOnlyList<string> path, string prefix)
    {
        if (Find(path, out _) is not { } prop) return null;
        var before = prop.Start;
        while (before > 0 && Text[before - 1] is ' ' or '\t') before--;
        if (before >= 2 && Text.AsSpan(0, before).EndsWith("*/"))
        {
            var open = Text.LastIndexOf("/*", before - 2, StringComparison.Ordinal);
            var content = open < 0 ? "" : Text[(open + 2)..(before - 2)].Trim().Replace("*\\/", "*/");
            return open >= 0 && content.StartsWith(prefix, StringComparison.Ordinal) ? new Comment(open, prop.Start, content) : null;
        }
        var lineStart = LineStartIfBlank(prop.Start);
        if (lineStart == 0 || Text[lineStart - 1] != '\n') return null;
        var end = lineStart - 1;
        if (end > 0 && Text[end - 1] == '\r') end--;
        var start = Text.LastIndexOf('\n', Math.Max(end - 1, 0)) + 1;
        if (start > end) return null;
        var line = Text[start..end].Trim();
        return line.StartsWith("// " + prefix, StringComparison.Ordinal) ? new Comment(start, lineStart, line["// ".Length..]) : null;
    }

    /// <summary>Only blanks come before <paramref name="at"/> on its line.</summary>
    bool AtLineStart(int at) { var start = LineStartIfBlank(at); return start == 0 || Text[start - 1] == '\n'; }

    void Insert(Node parent, IReadOnlyList<string> path, string json, bool first)
    {
        var props = parent.Props!;
        var braceIndent = IndentOf(parent.Start);
        var indent = props.Count > 0 ? IndentOf(props[0].Start) : braceIndent + Unit;
        var property = $"{Quote(path[0])}: {Indented(Nested(path.Skip(1).ToArray(), json), indent)}";
        if (props.Count == 0)
        {
            Splice(parent.Start + 1, parent.End - 1 - (parent.Start + 1), $"\n{indent}{property}\n{braceIndent}");
            return;
        }
        if (first)
        {
            if (AtLineStart(props[0].Start)) Splice(LineStartIfBlank(props[0].Start), 0, $"{indent}{property},\n");
            else Splice(props[0].Start, 0, $"{property}, ");
            return;
        }
        var last = props[^1];
        var after = NextCode(last.Value.End);
        var close = parent.End - 1;
        // The comma first: a trailing comma still parses, a missing one doesn't.
        if (after >= Text.Length || Text[after] != ',')
        {
            Splice(last.Value.End, 0, ",");
            close++;
        }
        if (AtLineStart(close)) Splice(LineStartIfBlank(close), 0, $"{indent}{property}\n");
        else Splice(close, 0, $"\n{indent}{property}\n{braceIndent}");
    }

    /// <summary>A value under keys that don't exist yet, as one section inside another.</summary>
    string Nested(IReadOnlyList<string> path, string json)
    {
        var text = json;
        for (var i = path.Count - 1; i >= 0; i--)
            text = $"{{\n{Unit}{Quote(path[i])}: {Indented(text, Unit)}\n}}";
        return text;
    }

    /// <summary>A value that spans lines, lined up under a key indented by <paramref name="indent"/>.</summary>
    static string Indented(string json, string indent) => json.Replace("\r\n", "\n").Replace("\n", "\n" + indent);

    /// <summary>One level of indentation, as the file already uses it.</summary>
    string Unit
    {
        get
        {
            foreach (var line in Text.Split('\n'))
            {
                var trimmed = line.TrimStart(' ', '\t');
                if (trimmed.Length < line.Length && trimmed.StartsWith('"')) return line[..(line.Length - trimmed.Length)];
            }
            return "  ";
        }
    }

    string IndentOf(int at)
    {
        var start = Text.LastIndexOf('\n', Math.Max(at - 1, 0)) + 1;
        if (at == 0) start = 0;
        var end = start;
        while (end < at && Text[end] is ' ' or '\t') end++;
        return Text[start..end];
    }

    /// <summary>The start of <paramref name="at"/>'s line when only blanks come before it there, else
    /// <paramref name="at"/>.</summary>
    int LineStartIfBlank(int at)
    {
        var start = at;
        while (start > 0 && Text[start - 1] is ' ' or '\t') start--;
        return start == 0 || Text[start - 1] == '\n' ? start : at;
    }

    /// <summary>Just past <paramref name="at"/>'s line ending when only blanks or a comment follow it there, so taking
    /// a key out takes its whole line; else <paramref name="at"/>.</summary>
    int EndOfLine(int at)
    {
        var i = at;
        while (i < Text.Length && Text[i] is ' ' or '\t') i++;
        if (i < Text.Length && Text[i] == '\r') i++;
        return i < Text.Length && Text[i] == '\n' ? i + 1 : i == Text.Length ? i : at;
    }

    /// <summary>The next character from <paramref name="at"/> that isn't a blank or in a comment.</summary>
    int NextCode(int at)
    {
        var i = at;
        while (i < Text.Length)
        {
            if (char.IsWhiteSpace(Text[i])) i++;
            else if (Text.AsSpan(i).StartsWith("//")) i = Text.IndexOf('\n', i) is var n and >= 0 ? n + 1 : Text.Length;
            else if (Text.AsSpan(i).StartsWith("/*")) i = Text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var c and >= 0 ? c + 2 : Text.Length;
            else break;
        }
        return i;
    }

    /// <summary>The comma just before <paramref name="at"/>, across blanks and comments; null when there isn't one.</summary>
    int? PreviousComma(int at)
    {
        // Scanned back to the previous key's value end, which the parse knows, so a comment's own commas don't count.
        var values = AllValueEnds().Where(e => e <= at).ToArray();
        if (values.Length == 0) return null;
        var from = values.Max();
        var comma = NextCode(from);
        return comma < at && Text[comma] == ',' ? comma : null;
    }

    IEnumerable<int> AllValueEnds()
    {
        var pending = new Stack<Node>([_root]);
        while (pending.Count > 0)
            foreach (var prop in pending.Pop().Props ?? [])
            {
                yield return prop.Value.End;
                if (prop.Value.Props is not null) pending.Push(prop.Value);
            }
    }
}
