using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Core.Config;

namespace Sprig.App;

/// <summary>Serializes a proposed <see cref="SprigRepoConfig"/> to a <c>.sprig.json</c> (camelCase, indented).</summary>
internal static class ConfigJson
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write(SprigRepoConfig config, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(config, Options) + "\n");
}
