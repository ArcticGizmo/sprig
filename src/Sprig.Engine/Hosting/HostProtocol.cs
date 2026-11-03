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
/// <param name="WaitFor">Runners of the same sprout (by name) that must be ready, or for jobs finished, before this
/// one starts. Its build doesn't wait.</param>
/// <param name="Job">Runs to completion: exit code 0 is success, and it's never "ready".</param>
public sealed record RunnerSpec(
    string Sprout,
    string Name,
    CommandSpec Command,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Env,
    CommandSpec? Build = null,
    string? BuildKey = null,
    int? Port = null,
    IReadOnlyList<string>? WaitFor = null,
    bool Job = false);

public sealed record CommandSpec(string File, IReadOnlyList<string> Args)
{
    public override string ToString() => string.Join(' ', [File, .. Args]);
}

/// <summary><c>Waiting</c>: built, waiting for what it depends on. <c>Running</c> and <c>Completed</c> are a job's
/// equivalents of <c>Ready</c>: running, then finished with code 0. <c>Stopping</c>: asked to stop, not yet gone.</summary>
public enum RunnerState { Building, Waiting, Starting, Ready, Running, Completed, Exited, Stopping, Stopped, Failed }

public static class RunnerStates
{
    /// <summary>Still on its way, or up.</summary>
    public static bool IsAlive(this RunnerState state)
        => state is RunnerState.Building or RunnerState.Waiting or RunnerState.Starting or RunnerState.Ready or RunnerState.Running;

    /// <summary>What a dependent can start after.</summary>
    public static bool Satisfies(this RunnerState state) => state is RunnerState.Ready or RunnerState.Completed;

    /// <summary>Settled one way or the other: nothing more will happen without being asked.</summary>
    public static bool IsSettled(this RunnerState state)
        => state is not (RunnerState.Building or RunnerState.Waiting or RunnerState.Starting or RunnerState.Running or RunnerState.Stopping);
}

/// <param name="Exit">The exit code once it has ended.</param>
public sealed record RunnerStatus(string Sprout, string Name, RunnerState State, int? Pid, int? Port, int? Exit, string LogFile,
    DateTimeOffset Since, string? Error = null);

/// <summary>One request to the host, one JSON line each way.</summary>
/// <param name="Specs">For <c>start</c>: the runners to have going.</param>
/// <param name="Restart">For <c>start</c>: names among <see cref="Specs"/> to start afresh even if running. The others
/// start only if they aren't already going (or, for a job, already finished).</param>
public sealed record HostRequest(string Op, IReadOnlyList<RunnerSpec>? Specs = null, string? Sprout = null, string? Name = null,
    IReadOnlyList<string>? Restart = null);

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
