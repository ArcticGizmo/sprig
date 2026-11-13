using System.Globalization;

namespace Sprig.Engine.Hosting;

/// <summary>What an app's processes, or a container, are using now.</summary>
/// <param name="Cpu">Percent of the whole machine (every core), as Task Manager counts it, so apps and containers add up
/// against the same 100. Null until there have been two samples to measure between.</param>
/// <param name="Memory">Bytes in RAM: the working set of every process in an app's job, or a container's own figure from
/// docker.</param>
/// <param name="Processes">How many processes that is; zero when not known.</param>
public sealed record ResourceUsage(double? Cpu, long Memory, int Processes = 0)
{
    /// <summary><c>3.2% · 412 MB</c>.</summary>
    public string Text => $"{CpuText(Cpu)} · {MemoryText(Memory)}";

    /// <summary><c>3.2% CPU · 412 MB · 3 processes</c>: the count too, since a dev server's workers are often where the
    /// memory went.</summary>
    public string Line => $"{CpuText(Cpu)} CPU · {MemoryText(Memory)}" + (Processes > 1 ? $" · {Processes} processes" : "");

    public static string CpuText(double? cpu) => cpu switch
    {
        null => "–%",
        < 0.05 => "0%",
        < 10 => cpu.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%",
        _ => cpu.Value.ToString("0", CultureInfo.InvariantCulture) + "%",
    };

    /// <summary>In 1024s, labelled MB and GB as Windows labels them.</summary>
    public static string MemoryText(long bytes)
    {
        const double mb = 1024 * 1024, gb = mb * 1024;
        return bytes >= gb ? (bytes / gb).ToString(bytes >= 10 * gb ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB"
            : (bytes / mb).ToString("0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>Several added together, for a workspace or everything sprig runs; null when there's nothing to add. CPU is
    /// left unknown only when every part's is.</summary>
    public static ResourceUsage? Sum(IEnumerable<ResourceUsage?> parts)
    {
        var known = parts.OfType<ResourceUsage>().ToArray();
        if (known.Length == 0) return null;
        var cpus = known.Select(u => u.Cpu).OfType<double>().ToArray();
        return new ResourceUsage(cpus.Length == 0 ? null : cpus.Sum(), known.Sum(u => u.Memory), known.Sum(u => u.Processes));
    }
}
