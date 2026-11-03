using System.Security.Cryptography;
using System.Text;
using Sprig.Engine.Store;

namespace Sprig.Engine.Definitions;

public enum TrustState { Trusted, Unknown, Changed }

/// <param name="ApprovedSha256">The hash that was approved before, when <see cref="State"/> is <see cref="TrustState.Changed"/>.</param>
public sealed record TrustDecision(TrustState State, string Sha256, string? ApprovedSha256 = null);

public sealed record TrustEntry(string Path, string Sha256, DateTimeOffset ApprovedAt);

/// <summary>
/// Which definition files the user has approved, by exact content. Sprig only runs a definition whose current
/// hash was approved, so a branch that changes a sprig.cs asks again instead of running new code silently.
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
    string File => Path.Combine(paths.Root, "trust.json");

    public TrustDecision Check(string definitionPath, string sha256)
    {
        var entry = Find(Load(), definitionPath);
        if (entry is null) return new TrustDecision(TrustState.Unknown, sha256);
        return string.Equals(entry.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
            ? new TrustDecision(TrustState.Trusted, sha256)
            : new TrustDecision(TrustState.Changed, sha256, entry.Sha256);
    }

    public void Approve(string definitionPath, string sha256)
    {
        var entries = Load().Where(e => !SamePath(e.Path, definitionPath)).ToList();
        entries.Add(new TrustEntry(Normalise(definitionPath), sha256, DateTimeOffset.UtcNow));
        JsonFile.Write(File, entries);
    }

    public void Revoke(string definitionPath)
        => JsonFile.Write(File, Load().Where(e => !SamePath(e.Path, definitionPath)).ToList());

    public IReadOnlyList<TrustEntry> List() => Load();

    List<TrustEntry> Load() => JsonFile.Read<List<TrustEntry>>(File) ?? [];

    static TrustEntry? Find(IEnumerable<TrustEntry> entries, string path) => entries.FirstOrDefault(e => SamePath(e.Path, path));

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
