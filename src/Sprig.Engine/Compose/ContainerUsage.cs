using System.Globalization;
using System.Text.Json;
using Sprig.Engine.Hosting;
using Sprig.Engine.Processes;

namespace Sprig.Engine.Compose;

/// <summary>
/// What each running compose container uses, from <c>docker stats</c>. Stats don't carry labels, so <c>docker ps</c> says
/// first which container is which project's service; a repo's compose file may name its containers anything.
/// </summary>
public static class ContainerUsage
{
    /// <summary>A Go template field, so <c>docker ps</c> prints a label by name without the rest of them.</summary>
    const string Labels = "{{.ID}}\t{{.Label \"com.docker.compose.project\"}}\t{{.Label \"com.docker.compose.service\"}}";

    /// <summary>Every running compose container's usage, by project and service; empty when none is running. Takes a
    /// second or two, since docker measures CPU between two readings.</summary>
    /// <param name="projects">Only these projects' containers are measured; null for every compose project's.</param>
    /// <exception cref="ProcessException">Docker didn't answer.</exception>
    public static IReadOnlyDictionary<(string Project, string Service), ResourceUsage> Read(IProcessRunner runner, IReadOnlySet<string>? projects = null)
    {
        var ps = runner.Run("docker", ["ps", "--filter", "label=com.docker.compose.project", "--format", Labels]).EnsureSuccess();
        var containers = ParseContainers(ps.StdOut).Where(c => projects?.Contains(c.Value.Project) ?? true).ToDictionary();
        if (containers.Count == 0) return new Dictionary<(string, string), ResourceUsage>();
        var stats = runner.Run("docker", ["stats", "--no-stream", "--format", "{{json .}}", .. containers.Keys]);
        // A container that stopped between the two calls makes stats fail for all of them; the next poll goes without it.
        if (!stats.Success) return new Dictionary<(string, string), ResourceUsage>();
        return Join(containers, ParseStats(stats.StdOut, Environment.ProcessorCount));
    }

    /// <summary>Container ID to its project and service, from <see cref="Labels"/> lines.</summary>
    public static IReadOnlyDictionary<string, (string Project, string Service)> ParseContainers(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 3 && parts.All(p => p.Length > 0))
            .GroupBy(parts => parts[0])
            .ToDictionary(g => g.Key, g => (g.First()[1], g.First()[2]));

    /// <summary>Container ID to its usage, from one JSON object a line. Docker counts CPU as a share of one core, so it's
    /// divided by <paramref name="processors"/> to compare with what apps use.</summary>
    public static IReadOnlyDictionary<string, ResourceUsage> ParseStats(string text, int processors)
    {
        var usage = new Dictionary<string, ResourceUsage>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                var id = Text(e, "ID");
                if (id.Length == 0) continue;
                var cpu = Percent(Text(e, "CPUPerc")) is { } perCore ? perCore / Math.Max(1, processors) : (double?)null;
                var memory = Bytes(Text(e, "MemUsage").Split('/')[0]) ?? 0;
                var pids = int.TryParse(Text(e, "PIDs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
                usage[id] = new ResourceUsage(cpu, memory, pids);
            }
            catch (JsonException) { /* one bad line costs only its own container */ }
        }
        return usage;
    }

    /// <summary>IDs from <c>ps</c> and <c>stats</c> are matched on the shorter of the two, since either may be cut short.</summary>
    static IReadOnlyDictionary<(string Project, string Service), ResourceUsage> Join(
        IReadOnlyDictionary<string, (string Project, string Service)> containers, IReadOnlyDictionary<string, ResourceUsage> stats)
    {
        var joined = new Dictionary<(string, string), ResourceUsage>();
        foreach (var (id, usage) in stats)
        {
            var match = containers.FirstOrDefault(c => c.Key.StartsWith(id, StringComparison.Ordinal) || id.StartsWith(c.Key, StringComparison.Ordinal));
            if (match.Key is null) continue;
            // A service scaled to several containers counts as one.
            joined[match.Value] = joined.TryGetValue(match.Value, out var other) ? ResourceUsage.Sum([other, usage])! : usage;
        }
        return joined;
    }

    static double? Percent(string text)
        => double.TryParse(text.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary><c>45.3MiB</c>, <c>1.2GiB</c>, <c>512kB</c>, <c>0B</c>.</summary>
    public static long? Bytes(string text)
    {
        var t = text.Trim();
        var split = t.Length;
        while (split > 0 && char.IsLetter(t[split - 1])) split--;
        if (!double.TryParse(t[..split], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
        double scale = t[split..].ToLowerInvariant() switch
        {
            "" or "b" => 1,
            "kib" => 1024,
            "kb" => 1000,
            "mib" => 1024 * 1024,
            "mb" => 1000 * 1000,
            "gib" => 1024d * 1024 * 1024,
            "gb" => 1000d * 1000 * 1000,
            "tib" => 1024d * 1024 * 1024 * 1024,
            _ => double.NaN,
        };
        return double.IsNaN(scale) ? null : (long)(value * scale);
    }

    static string Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
