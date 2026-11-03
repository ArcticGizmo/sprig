using Sprig.Engine.Hosting;
using Spectre.Console;

namespace Sprig.Cli.Commands;

/// <summary>
/// Several apps' logs as one stream, each line led by its app's name in that app's colour, the way
/// <c>docker compose up</c> shows its containers. The apps' own colours pass through to a terminal and are removed when
/// the output is redirected. Sprig's own lines are dimmed so the apps' output stands out.
/// </summary>
sealed class LogFollower
{
    static readonly string[] Colours = ["cyan", "yellow", "green", "magenta", "blue", "aqua", "orange1", "violet", "lime", "deepskyblue1"];

    readonly List<(string Label, string Colour, LogTail Tail)> _logs = [];
    readonly IAnsiConsole _ansi;
    readonly TextWriter _out;
    readonly bool _colour;
    int _width;

    /// <param name="colour">Keep the apps' own escape codes; false for redirected output.</param>
    public LogFollower(IAnsiConsole ansi, TextWriter output, bool colour)
    {
        _ansi = ansi;
        _out = output;
        _colour = colour;
    }

    /// <summary>
    /// Shortest unique labels: an app's own name, or <c>repo/app</c> when two repos have an app of that name.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Labels(IEnumerable<string> names)
    {
        var all = names.ToArray();
        var shortName = all.ToDictionary(n => n, n => n[(n.IndexOf('/') + 1)..]);
        var clashes = shortName.Values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return all.ToDictionary(n => n, n => clashes.Contains(shortName[n]) ? n : shortName[n]);
    }

    public void Add(string label, string logFile)
    {
        if (_logs.Any(l => l.Tail.File == logFile)) return;
        _logs.Add((label, Colours[_logs.Count % Colours.Length], new LogTail(logFile)));
        _width = Math.Max(_width, label.Length);
    }

    /// <summary>Print what each log has gained since the last call; true when anything was printed.</summary>
    public bool Poll()
    {
        var any = false;
        foreach (var (label, colour, tail) in _logs)
        {
            foreach (var line in tail.ReadNew())
            {
                any = true;
                _ansi.Markup($"[{colour}]{Markup.Escape(label.PadRight(_width))} |[/] ");
                var text = _colour ? line : AnsiText.Strip(line);
                if (text.StartsWith("sprig: ", StringComparison.Ordinal)) _ansi.MarkupLine($"[dim]{Markup.Escape(AnsiText.Strip(text))}[/]");
                else
                {
                    // Written past the markup renderer, so the app's own colours reach the terminal untouched. The reset
                    // stops a colour the app left on from bleeding into the next app's prefix.
                    _out.WriteLine(_colour && text.Contains('\x1B') ? text + "\x1B[0m" : text);
                }
            }
        }
        if (any) _out.Flush();
        return any;
    }
}
