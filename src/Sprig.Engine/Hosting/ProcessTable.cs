using System.Diagnostics;
using System.Globalization;

namespace Sprig.Engine.Hosting;

/// <summary>One process, as the process table has it.</summary>
/// <param name="Cpu">CPU time it has used, user and system.</param>
/// <param name="Memory">Bytes resident in RAM.</param>
public sealed record ProcessEntry(int Pid, int Parent, TimeSpan Cpu, long Memory);

/// <summary>
/// Every process on the machine with its parent, for measuring an app where there's no job object (anywhere but
/// Windows): the process sprig started and everything under it, found by following parents. Unlike a job's figure, a
/// child that has exited takes its CPU time with it, so a sample straight after one ends can read low.
/// </summary>
public static class ProcessTable
{
    /// <summary>The table now: from <c>/proc</c> on Linux, from <c>ps</c> elsewhere. Null on Windows, which asks its
    /// jobs, or when it can't be read.</summary>
    public static IReadOnlyList<ProcessEntry>? Take()
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            return OperatingSystem.IsLinux() ? FromProc() : FromPs();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary><paramref name="root"/> and its descendants added up; null when it isn't in the table.</summary>
    public static (TimeSpan Cpu, long Memory, int Processes)? Tree(IReadOnlyList<ProcessEntry> table, int root)
    {
        if (table.FirstOrDefault(p => p.Pid == root) is not { } top) return null;
        var children = table.Where(p => p.Pid != p.Parent).ToLookup(p => p.Parent);
        var (cpu, memory, count) = (TimeSpan.Zero, 0L, 0);
        var seen = new HashSet<int>();
        var pending = new Stack<ProcessEntry>([top]);
        while (pending.Count > 0)
        {
            var p = pending.Pop();
            if (!seen.Add(p.Pid)) continue;
            (cpu, memory, count) = (cpu + p.Cpu, memory + p.Memory, count + 1);
            foreach (var child in children[p.Pid]) pending.Push(child);
        }
        return (cpu, memory, count);
    }

    /// <summary>The kernel reports CPU in USER_HZ ticks, which Linux fixes at 100 for every program it runs.</summary>
    const double TicksPerSecond = 100;

    static List<ProcessEntry> FromProc()
    {
        var entries = new List<ProcessEntry>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
            string text;
            // Gone between listing and reading, which is everyday for short-lived processes.
            try { text = File.ReadAllText(Path.Combine(dir, "stat")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (ParseStat(text, Environment.SystemPageSize) is { } entry) entries.Add(entry);
        }
        return entries;
    }

    /// <summary>A <c>/proc/[pid]/stat</c> line. The command name is in brackets and may hold spaces or brackets of its
    /// own, so fields are counted from the last closing one.</summary>
    internal static ProcessEntry? ParseStat(string text, int pageSize)
    {
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open < 0 || close < open) return null;
        // After the name: state (field 3), ppid (4), ..., utime (14), stime (15), ..., rss (24).
        var fields = text[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 22
            || !int.TryParse(text[..open].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent)
            || !long.TryParse(fields[11], NumberStyles.None, CultureInfo.InvariantCulture, out var user)
            || !long.TryParse(fields[12], NumberStyles.None, CultureInfo.InvariantCulture, out var system)
            || !long.TryParse(fields[21], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pages))
            return null;
        return new ProcessEntry(pid, parent, TimeSpan.FromSeconds((user + system) / TicksPerSecond), Math.Max(0, pages) * pageSize);
    }

    static List<ProcessEntry> FromPs()
    {
        var start = new ProcessStartInfo("ps", ["-axo", "pid=,ppid=,rss=,time="])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var ps = Process.Start(start) ?? throw new InvalidOperationException("ps didn't start");
        var output = ps.StandardOutput.ReadToEndAsync();
        if (!ps.WaitForExit(2000))
        {
            try { ps.Kill(); } catch (InvalidOperationException) { /* already gone */ }
            throw new InvalidOperationException("ps didn't answer");
        }
        return output.Result.Split('\n').Select(ParsePsLine).OfType<ProcessEntry>().ToList();
    }

    /// <summary>A line of <c>ps -o pid=,ppid=,rss=,time=</c>: resident memory in KiB, and CPU time as macOS writes it
    /// (<c>12:34.56</c>, minutes and seconds) or Linux's procps does (<c>[dd-]hh:mm:ss</c>).</summary>
    internal static ProcessEntry? ParsePsLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var kib)
            || ParseCpuTime(parts[3]) is not { } cpu)
            return null;
        return new ProcessEntry(pid, parent, cpu, kib * 1024);
    }

    internal static TimeSpan? ParseCpuTime(string text)
    {
        var days = 0;
        if (text.IndexOf('-') is > 0 and var dash)
        {
            if (!int.TryParse(text[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out days)) return null;
            text = text[(dash + 1)..];
        }
        var units = text.Split(':');
        if (units.Length is < 2 or > 3
            || !double.TryParse(units[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || !int.TryParse(units[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            return null;
        var hours = 0;
        if (units.Length == 3 && !int.TryParse(units[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) return null;
        return TimeSpan.FromDays(days) + TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }
}
