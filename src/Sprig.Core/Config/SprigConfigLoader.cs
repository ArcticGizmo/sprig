using System.Text.Json;

namespace Sprig.Core.Config;

/// <summary>Thrown when a <c>.sprig.json</c> cannot be read or parsed into a config object.</summary>
public sealed class SprigConfigException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Reads and parses <c>.sprig.json</c> files. Parsing failures throw; content problems are for the validator.</summary>
public static class SprigConfigLoader
{
    public const int SupportedSchema = 1;

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Load and parse a <c>.sprig.json</c> from disk.</summary>
    /// <exception cref="SprigConfigException">File missing, unreadable, or not valid JSON.</exception>
    public static SprigRepoConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new SprigConfigException($"no .sprig.json found at '{path}'");

        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { throw new SprigConfigException($"could not read '{path}': {ex.Message}", ex); }

        return Parse(text, path);
    }

    /// <summary>Parse <c>.sprig.json</c> text into a config object.</summary>
    /// <exception cref="SprigConfigException">Text is not valid JSON or does not shape into a config.</exception>
    public static SprigRepoConfig Parse(string json, string? source = null)
    {
        var where = source is null ? "" : $" in '{source}'";
        SprigRepoConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<SprigRepoConfig>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new SprigConfigException($"invalid JSON{where}: {ex.Message}", ex);
        }

        if (config is null)
            throw new SprigConfigException($"'.sprig.json'{where} parsed to null");

        return config;
    }
}
