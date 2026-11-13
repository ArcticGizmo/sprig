using System.Text.Json;
using Sprig.Engine.Processes;

namespace Sprig.Engine.Compose;

/// <summary>One service's container, as <c>docker compose ps --format json</c> describes it.</summary>
/// <param name="State">created, running, exited, restarting…</param>
/// <param name="Health">healthy, unhealthy, starting, or empty when it has no health check.</param>
public sealed record ComposeService(string Service, string State, string Health, int ExitCode)
{
    /// <summary>Why <c>up --wait</c> gave up on it, when it did.</summary>
    public bool Failed => State is "exited" or "dead" or "restarting" || Health is "unhealthy" or "starting";

    public string Why => (State, Health) switch
    {
        ("exited" or "dead", _) => $"{Service} stopped while starting (exit {ExitCode})",
        ("restarting", _) => $"{Service} keeps stopping and restarting (last exit {ExitCode})",
        (_, "unhealthy") => $"{Service} is running, but its health check keeps failing",
        (_, "starting") => $"{Service} didn't pass its health check within {(int)DockerCompose.WaitTimeout.TotalMinutes} minutes",
        _ => $"{Service} is {State}",
    };

    /// <summary>Compose prints one object per line; older versions print a single array.</summary>
    public static IReadOnlyList<ComposeService> Parse(string json)
    {
        var text = json.Trim();
        if (text.Length == 0) return [];
        var elements = new List<JsonElement>();
        try
        {
            if (text.StartsWith('['))
            {
                using var doc = JsonDocument.Parse(text);
                elements.AddRange(doc.RootElement.EnumerateArray().Select(e => e.Clone()));
            }
            else
            {
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var doc = JsonDocument.Parse(line);
                    elements.Add(doc.RootElement.Clone());
                }
            }
        }
        catch (JsonException) { return []; }

        return elements.Select(e => new ComposeService(
            Text(e, "Service"),
            Text(e, "State").ToLowerInvariant(),
            Text(e, "Health").ToLowerInvariant(),
            e.TryGetProperty("ExitCode", out var code) && code.ValueKind == JsonValueKind.Number ? code.GetInt32() : 0)).ToArray();
    }

    static string Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>A compose project didn't come up. The message says which service and how; <see cref="Result"/> has all of
/// what compose printed.</summary>
public sealed class ComposeUpException(string message, ProcessResult result) : Exception(message)
{
    public ProcessResult Result { get; } = result;
}
