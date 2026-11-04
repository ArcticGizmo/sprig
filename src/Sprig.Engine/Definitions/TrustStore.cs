using System.Security.Cryptography;
using System.Text;
using Sprig.Engine.Store;

namespace Sprig.Engine.Definitions;

public enum TrustState { Trusted, Unknown, Changed }

/// <param name="ApprovedSha256">The most recently approved hash, when <see cref="State"/> is <see cref="TrustState.Changed"/>.</param>
public sealed record TrustDecision(TrustState State, string Sha256, string? ApprovedSha256 = null);

/// <summary>One approved version of a definition file. A file can have several, one entry each.</summary>
public sealed record TrustEntry(string Path, string Sha256, DateTimeOffset ApprovedAt);

/// <summary>
/// Which versions of each definition file the user has approved, by exact content. Sprig only runs a definition
/// whose current hash was approved, so a branch that changes a sprig.cs asks once instead of running new code
/// silently. Approving a version keeps the earlier ones, so switching between branches doesn't ask again.
/// </summary>
public interface ITrustStore
{
    TrustDecision Check(string definitionPath, string sha256);
    void Approve(string definitionPath, string sha256);
    void Revoke(string definitionPath);
    IReadOnlyList<TrustEntry> List();
}

public sealed class FileTrustStore(ISprigPaths paths) : ITrustStore
{
    /// <summary>Old versions are dropped past this, so a file edited often doesn't grow the store forever.</summary>
    public const int VersionsKept = 20;

    string File => Path.Combine(paths.Root, "trust.json");

    public TrustDecision Check(string definitionPath, string sha256)
    {
        var versions = Load().Where(e => SamePath(e.Path, definitionPath)).ToList();
        if (versions.Count == 0) return new TrustDecision(TrustState.Unknown, sha256);
        return versions.Any(e => string.Equals(e.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            ? new TrustDecision(TrustState.Trusted, sha256)
            : new TrustDecision(TrustState.Changed, sha256, versions.MaxBy(e => e.ApprovedAt)!.Sha256);
    }

    public void Approve(string definitionPath, string sha256)
    {
        var entries = Load();
        var others = entries.Where(e => !SamePath(e.Path, definitionPath));
        var versions = entries.Where(e => SamePath(e.Path, definitionPath) && !string.Equals(e.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.ApprovedAt)
            .Take(VersionsKept - 1);
        JsonFile.Write(File, others.Concat(versions).Append(new TrustEntry(Normalise(definitionPath), sha256, DateTimeOffset.UtcNow)).ToList());
    }

    public void Revoke(string definitionPath)
        => JsonFile.Write(File, Load().Where(e => !SamePath(e.Path, definitionPath)).ToList());

    public IReadOnlyList<TrustEntry> List() => Load();

    List<TrustEntry> Load() => JsonFile.Read<List<TrustEntry>>(File) ?? [];

    static string Normalise(string path) => Path.GetFullPath(path);

    static bool SamePath(string a, string b) => string.Equals(Normalise(a), Normalise(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public static class DefinitionHash
{
    /// <summary>SHA-256 of the text with line endings normalised, so a CRLF checkout of the same file doesn't
    /// ask for trust again.</summary>
    public static string Of(string text)
    {
        var normalised = text.Replace("\r\n", "\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }
}
