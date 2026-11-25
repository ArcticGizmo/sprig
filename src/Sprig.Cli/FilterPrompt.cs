using Spectre.Console;
using Spectre.Console.Rendering;

namespace Sprig.Cli;

/// <summary>How a search finds things, the same in a list as on the command line: every word of it somewhere in the
/// text, in any case and any order.</summary>
static class SearchWords
{
    public static bool Match(string text, string search)
        => search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What a <see cref="FilterPrompt"/> shows and where its cursor is, apart from the console so it can be tested. The
/// row after the last match is "none", which no search hides, so there's always a way out.
/// </summary>
sealed class FilterList<T> where T : class
{
    readonly (T Item, string Text)[] all;

    public FilterList(IEnumerable<T> items, Func<T, string> text)
    {
        all = items.Select(i => (i, text(i))).ToArray();
        Shown = all.Select(a => a.Item).ToArray();
    }

    public string Query { get; private set; } = "";
    public IReadOnlyList<T> Shown { get; private set; }
    public int Cursor { get; private set; }
    public int Rows => Shown.Count + 1;
    public bool OnNone => Cursor == Shown.Count;
    public T? Picked => OnNone ? null : Shown[Cursor];

    public void Type(char c) => Search(Query + c);
    public void Backspace() { if (Query.Length > 0) Search(Query[..^1]); }
    public void Clear() => Search("");
    public void Move(int by) => Cursor = Math.Clamp(Cursor + by, 0, Rows - 1);
    public void ToFirst() => Cursor = 0;
    public void ToLast() => Cursor = Rows - 1;

    /// <summary>The first row of a page of <paramref name="size"/> that keeps the cursor near its middle.</summary>
    public int PageStart(int size) => Math.Max(0, Math.Min(Cursor - size / 2, Rows - size));

    // A changed search puts the cursor back on the best match, the first.
    void Search(string query)
    {
        Query = query;
        Shown = all.Where(a => SearchWords.Match(a.Text, query)).Select(a => a.Item).ToArray();
        Cursor = 0;
    }
}

/// <summary>
/// A list to pick one from where typing hides what doesn't match, rather than Spectre's search, which only highlights
/// and leaves a long list as long. Escape clears the search, or with none, picks "none".
/// </summary>
static class FilterPrompt
{
    const int PageSize = 15;

    public static T? Pick<T>(IAnsiConsole console, string title, IEnumerable<T> choices, Func<T, string> describe) where T : class
    {
        var list = new FilterList<T>(choices, c => Markup.Remove(describe(c)));
        T? picked = null;
        var live = console.Live(Render(title, list, describe));
        live.AutoClear = true;
        live.Start(ctx =>
        {
            // Live draws only when refreshed, and the loop waits on a key before its first.
            ctx.Refresh();
            while (true)
            {
                if (console.Input.ReadKey(intercept: true) is not { } key) continue;
                switch (key.Key)
                {
                    case ConsoleKey.Enter: picked = list.Picked; return;
                    case ConsoleKey.Escape when list.Query.Length == 0: return;
                    case ConsoleKey.Escape: list.Clear(); break;
                    case ConsoleKey.UpArrow: list.Move(-1); break;
                    case ConsoleKey.DownArrow: list.Move(1); break;
                    case ConsoleKey.PageUp: list.Move(-PageSize); break;
                    case ConsoleKey.PageDown: list.Move(PageSize); break;
                    case ConsoleKey.Home: list.ToFirst(); break;
                    case ConsoleKey.End: list.ToLast(); break;
                    case ConsoleKey.Backspace: list.Backspace(); break;
                    default:
                        if (char.IsControl(key.KeyChar)) continue;
                        list.Type(key.KeyChar);
                        break;
                }
                ctx.UpdateTarget(Render(title, list, describe));
            }
        });
        return picked;
    }

    static IRenderable Render<T>(string title, FilterList<T> list, Func<T, string> describe) where T : class
    {
        var lines = new List<IRenderable>
        {
            new Markup(title),
            new Markup(list.Query.Length == 0 ? "[dim](type to search)[/]" : $"[blue]/[/] {Markup.Escape(list.Query)}"),
        };
        if (list.Shown.Count == 0) lines.Add(new Markup($"  [dim]nothing matches[/]"));

        var start = list.PageStart(PageSize);
        var end = Math.Min(list.Rows, start + PageSize);
        if (start > 0) lines.Add(new Markup($"  [dim]↑ {start} more[/]"));
        for (var row = start; row < end; row++)
        {
            var text = row < list.Shown.Count ? describe(list.Shown[row]) : "[dim]none[/]";
            lines.Add(new Markup(row == list.Cursor ? $"[blue]>[/] [bold]{text}[/]" : $"  {text}"));
        }
        if (end < list.Rows) lines.Add(new Markup($"  [dim]↓ {list.Rows - end} more[/]"));
        return new Rows(lines);
    }
}
