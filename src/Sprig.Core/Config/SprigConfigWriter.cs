using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sprig.Core.Config;

/// <summary>Serializes a <see cref="SprigRepoConfig"/> in the <c>.sprig.json</c> house style (camelCase,
/// indented, nulls omitted) — one shape whether it lands in a repo or in sprig's store.</summary>
public static class SprigConfigWriter
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(SprigRepoConfig config) => JsonSerializer.Serialize(config, Options);

    public static void Write(SprigRepoConfig config, string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        File.WriteAllText(path, Serialize(config) + "\n");
    }
}
