using System.Diagnostics;
using System.Text.Json;
using Sprig.Engine.Store;
using Sprig.Model;

namespace Sprig.Engine.Definitions;

public abstract record DefinitionResult(string Path, string Sha256, TimeSpan Elapsed);

/// <summary>The model, either freshly evaluated or from the cache.</summary>
public sealed record Evaluated(string Path, string Sha256, TimeSpan Elapsed, DefinitionModel Model, bool FromCache)
    : DefinitionResult(Path, Sha256, Elapsed);

/// <summary>Not run: this exact content hasn't been approved. <paramref name="ApprovedSha256"/> is set when an earlier
/// version was.</summary>
public sealed record NeedsTrust(string Path, string Sha256, TimeSpan Elapsed, string? ApprovedSha256)
    : DefinitionResult(Path, Sha256, Elapsed);

/// <summary>Not run: the file uses directives a definition may not.</summary>
public sealed record Rejected(string Path, string Sha256, TimeSpan Elapsed, IReadOnlyList<DirectivePolicy.Violation> Violations)
    : DefinitionResult(Path, Sha256, Elapsed);

/// <summary>Ran, but didn't produce a model: a compile error, an exception, or the time limit.</summary>
public sealed record Failed(string Path, string Sha256, TimeSpan Elapsed, string Reason, string Output)
    : DefinitionResult(Path, Sha256, Elapsed);

/// <summary>
/// Turns a sprig.cs into its model. Checks the directives, then trust, then the cache, and only then runs the
/// definition. The model is cached by content hash and SDK version, so an unchanged definition never runs twice.
/// </summary>
public sealed class DefinitionRunner(ITrustStore trust, IDefinitionHost host, ISprigPaths paths, DefinitionRunnerOptions? options = null)
{
    readonly DefinitionRunnerOptions _options = options ?? new DefinitionRunnerOptions();

    public DefinitionResult Evaluate(string definitionPath)
    {
        var clock = Stopwatch.StartNew();
        var path = Path.GetFullPath(definitionPath);
        var text = File.ReadAllText(path);
        var sha = DefinitionHash.Of(text);

        var violations = DirectivePolicy.Check(text, _options.AllowLocalSdkProject);
        if (violations.Count > 0) return new Rejected(path, sha, clock.Elapsed, violations);

        var decision = trust.Check(path, sha);
        if (decision.State != TrustState.Trusted) return new NeedsTrust(path, sha, clock.Elapsed, decision.ApprovedSha256);

        var cacheFile = CacheFile(sha);
        if (_options.UseCache && TryRead(cacheFile) is { } cached)
            return new Evaluated(path, sha, clock.Elapsed, cached, FromCache: true);

        var outFile = Path.Combine(Path.GetTempPath(), $"sprig-model-{Guid.NewGuid():N}.json");
        try
        {
            var result = host.Run(path, outFile, _options.Timeout);
            if (result.TimedOut)
                return new Failed(path, sha, clock.Elapsed, $"didn't finish within {_options.Timeout.TotalSeconds:0}s", result.Output);
            if (result.ExitCode != 0)
                return new Failed(path, sha, clock.Elapsed, $"exited with code {result.ExitCode}", result.Output);
            if (TryRead(outFile) is not { } model)
                return new Failed(path, sha, clock.Elapsed, "ran but wrote no model; does it end with 'return repo.Emit(args);'?", result.Output);

            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            File.Copy(outFile, cacheFile, overwrite: true);
            return new Evaluated(path, sha, clock.Elapsed, model, FromCache: false);
        }
        finally
        {
            try { File.Delete(outFile); } catch { /* temp file */ }
        }
    }

    string CacheFile(string sha) => Path.Combine(paths.Root, "cache", "models", $"{sha}-{Sprig.Sdk.Version}.json");

    static DefinitionModel? TryRead(string file)
    {
        if (!File.Exists(file)) return null;
        try { return JsonSerializer.Deserialize(File.ReadAllText(file), ModelJsonContext.Default.DefinitionModel); }
        catch (JsonException) { return null; }
    }
}

public sealed record DefinitionRunnerOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    public bool UseCache { get; init; } = true;

    /// <summary>Lets a definition use <c>#:project</c> on a local Sprig.Sdk.csproj. On in dev builds, where the SDK
    /// isn't published as a package yet.</summary>
    public bool AllowLocalSdkProject { get; init; } = AppProfile.IsDev;
}
