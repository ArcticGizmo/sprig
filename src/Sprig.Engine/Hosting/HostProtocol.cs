using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Engine.Store;

namespace Sprig.Engine.Hosting;

/// <summary>How to run one app for a sprout.</summary>
/// <param name="Sprout">The sprout's key, <c>&lt;trellis&gt;/&lt;sprout&gt;</c>.</param>
/// <param name="Name">The app, as <c>&lt;repo&gt;/&lt;app&gt;</c>.</param>
/// <param name="Build">Run first and must succeed; runs one at a time per <see cref="BuildKey"/>.</param>
/// <param name="BuildKey">Builds sharing a key (the repo folder) never overlap, since they share output folders.</param>
/// <param name="Port">The app is ready once something accepts connections on this port.</param>
public sealed record RunnerSpec(
    string Sprout,
    string Name,
    CommandSpec Command,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Env,
    CommandSpec? Build = null,
    string? BuildKey = null,
    int? Port = null);

public sealed record CommandSpec(string File, IReadOnlyList<string> Args)
{
    public override string ToString() => string.Join(' ', [File, .. Args]);
}

public enum RunnerState { Building, Starting, Ready, Exited, Stopped, Failed }

/// <param name="Exit">The exit code once it has ended.</param>
public sealed record RunnerStatus(string Sprout, string Name, RunnerState State, int? Pid, int? Port, int? Exit, string LogFile,
    DateTimeOffset Since, string? Error = null);

/// <summary>One request to the host, one JSON line each way.</summary>
public sealed record HostRequest(string Op, RunnerSpec? Spec = null, string? Sprout = null, string? Name = null);

public sealed record HostResponse(bool Ok, string? Error = null, int? Pid = null, string? Version = null,
    IReadOnlyList<RunnerStatus>? Runners = null);

public static class HostProtocol
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>One host per store, so a throwaway <c>SPRIG_STORE</c> gets its own host and never touches the real
    /// one's apps.</summary>
    public static string PipeName(ISprigPaths paths) => "sprig-host-" + StoreId(paths);

    public static string MutexName(ISprigPaths paths) => @"Local\sprig-host-" + StoreId(paths);

    public static string LogFolder(ISprigPaths paths) => Path.Combine(paths.Root, "logs");

    public static string LogFile(ISprigPaths paths, string sprout, string name)
        => Path.Combine(LogFolder(paths), Safe(sprout), Safe(name) + ".log");

    static string StoreId(ISprigPaths paths)
    {
        var root = Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16].ToLowerInvariant();
    }

    static string Safe(string text) => string.Join(Path.DirectorySeparatorChar,
        text.Split('/').Select(part => string.Concat(part.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))));
}
