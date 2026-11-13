using System.Globalization;
using System.Text.RegularExpressions;

namespace Sprig.Engine.Hosting;

/// <summary>One line of a runner's log.</summary>
/// <param name="Time">When the host wrote it; null for a log written before lines carried a time.</param>
public sealed record LogEntry(DateTimeOffset? Time, string Text);

/// <summary>
/// How the host writes a runner's log: every line led by the local time it arrived (<c>11:42:03.118 </c>), and a
/// <c>--- 2026-11-04 ---</c> line at the top and whenever the date changes, so the times can be ordered across apps
/// while the file stays easy to read.
/// </summary>
public static partial class LogLineFormat
{
    const string TimeFormat = "HH:mm:ss.fff";

    /// <summary>The time prefix's length, space included.</summary>
    public const int PrefixLength = 13;

    public static string Prefix(DateTime local) => local.ToString(TimeFormat, CultureInfo.InvariantCulture) + " ";

    public static string DateMarker(DateOnly date) => $"--- {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} ---";

    internal static bool TryDateMarker(string line, out DateOnly date)
    {
        date = default;
        var match = DateMarkerPattern().Match(line);
        return match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    internal static bool TryTime(string line, out TimeOnly time)
    {
        time = default;
        return line.Length >= PrefixLength && line[PrefixLength - 1] == ' '
            && TimeOnly.TryParseExact(line.AsSpan(0, PrefixLength - 1), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    [GeneratedRegex(@"^--- (\d{4}-\d{2}-\d{2}) ---$")]
    private static partial Regex DateMarkerPattern();
}

/// <summary>Turns a log's raw lines back into entries, one file at a time and in order: the date markers set the date
/// for the times that follow, and aren't entries themselves.</summary>
public sealed class LogLineParser
{
    DateOnly? _date;

    /// <summary>Null for a date marker.</summary>
    public LogEntry? Parse(string raw)
    {
        if (LogLineFormat.TryDateMarker(raw, out var date))
        {
            _date = date;
            return null;
        }
        if (!LogLineFormat.TryTime(raw, out var time)) return new LogEntry(null, raw);
        var local = (_date ?? DateOnly.FromDateTime(DateTime.Now)).ToDateTime(time, DateTimeKind.Local);
        return new LogEntry(new DateTimeOffset(local), raw[LogLineFormat.PrefixLength..]);
    }

    public void Reset() => _date = null;
}

public static class LogLabels
{
    /// <summary>Shortest unique labels for apps named <c>repo/app</c>: the app's own name, or the whole name when two
    /// repos have an app of that name.</summary>
    public static IReadOnlyDictionary<string, string> Shortest(IEnumerable<string> names)
    {
        var all = names.Distinct().ToArray();
        var shortName = all.ToDictionary(n => n, n => n[(n.IndexOf('/') + 1)..]);
        var clashes = shortName.Values.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return all.ToDictionary(n => n, n => clashes.Contains(shortName[n]) ? n : shortName[n]);
    }
}

public enum LineLevel { Info, Sprig, Warning, Error }

/// <summary>
/// A guess at how serious a log line is, from its text, for colouring it and for "warnings and errors only". Never used
/// for an app's state, so a wrong guess only costs a colour.
/// </summary>
public static partial class LogLevels
{
    /// <param name="text">The line with its colour codes removed.</param>
    /// <param name="runs">The line's coloured runs, when known: red text is taken as an error.</param>
    public static LineLevel Of(string text, IReadOnlyList<AnsiRun>? runs = null)
    {
        if (text.StartsWith("sprig: ", StringComparison.Ordinal))
        {
            var exit = SprigExit().Match(text);
            if (exit.Success) return exit.Groups[1].Value == "0" ? LineLevel.Sprig : LineLevel.Error;
            return text.Contains("restarting", StringComparison.Ordinal) || text.Contains("couldn't", StringComparison.Ordinal)
                ? LineLevel.Warning : LineLevel.Sprig;
        }
        if (Error().IsMatch(text)) return LineLevel.Error;
        if (Warning().IsMatch(text)) return LineLevel.Warning;
        if (runs is not null && runs.Any(r => r.Foreground is 0xF87171 or 0xFCA5A5 && r.Text.Trim().Length > 0)) return LineLevel.Error;
        return LineLevel.Info;
    }

    [GeneratedRegex(@"^sprig: exited with code (-?\d+)")]
    private static partial Regex SprigExit();

    // .NET's console logger (fail:, crit:), unhandled exceptions and their stack frames, compiler errors, npm, and the
    // bracketed or key=value levels most other loggers write.
    [GeneratedRegex(@"^(fail|crit):|^Unhandled exception|^\s+at \S+\(.*\)|: error [A-Z]+\d+|npm ERR!|^\s*(\[[\w:. -]+\]\s*)?(error|ERROR|Error)\b[:\]]?|^\w*(Error|Exception): |\[(ERROR|ERR|FATAL|FTL|CRITICAL)\]|\blevel=(error|fatal)\b|^\s*(ERROR|FATAL)\s")]
    private static partial Regex Error();

    [GeneratedRegex(@"^warn:|: warning [A-Z]+\d+|npm WARN|^\s*(\[[\w:. -]+\]\s*)?(warn|warning|WARN|WARNING|Warning)\b[:\]]?|\[(WARN|WRN|WARNING)\]|\blevel=warn(ing)?\b|^\s*(WARN|WARNING)\s")]
    private static partial Regex Warning();
}
