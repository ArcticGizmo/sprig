using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sprig.Engine.Sprouts;

/// <summary>A file sprig wrote into a repo or worktree, kept so <c>sprig rm</c> can take its values out again.</summary>
/// <param name="Kind"><c>dotenv</c>, <c>launchSettings</c> or <c>appSettings</c>.</param>
/// <param name="Created">Sprig made the file, so removing the sprout deletes it rather than editing it.</param>
/// <param name="Keys">For <c>appSettings</c>: the dotted keys sprig set.</param>
public sealed record WrittenFile(string Path, string Kind, bool Created, string? Profile = null, IReadOnlyList<string>? Keys = null);

/// <summary>
/// Writes an app's values into env files, launch profiles and JSON settings. Each writer changes only what sprig owns
/// (a marked block, one profile, the keys it sets) and keeps everything else in the file, so a developer's own values
/// survive and removing the sprout puts the file back.
/// </summary>
public static class EnvFiles
{
    public const string BeginMarker = "# >>> sprig >>>";
    public const string EndMarker = "# <<< sprig <<<";

    /// <summary>
    /// Put <paramref name="values"/> in a marked block at the top and the bottom of a dotenv file, so they win under
    /// loaders that take the first value and those that take the last. A new file starts from <paramref name="seed"/>
    /// (the same file in the repo's main checkout), so a worktree keeps the developer's local values.
    /// </summary>
    public static void WriteDotEnv(string path, IReadOnlyList<KeyValuePair<string, string>> values, string? seed)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : seed is not null && File.Exists(seed) ? File.ReadAllText(seed) : "";
        var body = StripBlocks(existing).TrimEnd('\n', '\r');
        var block = new StringBuilder().Append(BeginMarker).Append('\n');
        foreach (var (key, value) in values) block.Append(key).Append('=').Append(DotEnvValue(value)).Append('\n');
        block.Append(EndMarker).Append('\n');

        var text = new StringBuilder().Append(block);
        if (body.Length > 0) text.Append(body).Append('\n');
        text.Append(block);
        WriteText(path, text.ToString());
    }

    public static void RemoveDotEnv(WrittenFile file)
    {
        if (!File.Exists(file.Path)) return;
        var rest = StripBlocks(File.ReadAllText(file.Path));
        if (file.Created && rest.Trim().Length == 0) File.Delete(file.Path);
        else WriteText(file.Path, rest);
    }

    /// <summary>A launch profile named <paramref name="profile"/>, placed first so <c>dotnet run</c> uses it by
    /// default. Other profiles are kept as they are.</summary>
    public static void WriteLaunchProfile(string path, string profile, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        var root = ReadObject(path);
        var old = root["profiles"] as JsonObject ?? [];

        var env = new JsonObject();
        foreach (var (key, value) in values) env[key] = value;
        var mine = new JsonObject
        {
            ["commandName"] = "Project",
            ["dotnetRunMessages"] = true,
            ["launchBrowser"] = false,
        };
        // dotnet run turns applicationUrl into ASPNETCORE_URLS, so it must agree with whichever url var the app is given.
        if (values.LastOrDefault(v => v.Key.Equals("Urls", StringComparison.OrdinalIgnoreCase) || v.Key == "ASPNETCORE_URLS") is { Value: { } urls })
            mine["applicationUrl"] = urls;
        mine["environmentVariables"] = env;

        var profiles = new JsonObject { [profile] = mine };
        foreach (var (name, value) in old.ToArray())
        {
            if (name == profile) continue;
            old.Remove(name);
            profiles[name] = value;
        }
        root.Remove("profiles");
        root["profiles"] = profiles;
        WriteJson(path, root);
    }

    public static void RemoveLaunchProfile(WrittenFile file)
    {
        if (!File.Exists(file.Path)) return;
        var root = ReadObject(file.Path);
        (root["profiles"] as JsonObject)?.Remove(file.Profile ?? "sprig");
        if (file.Created && root["profiles"] is JsonObject { Count: 0 } && root.Count == 1) DeleteWithEmptyFolder(file.Path);
        else WriteJson(file.Path, root);
    }

    /// <summary>Set each value in a JSON settings file, with <c>Section__Key</c> nested into sections the way .NET
    /// configuration reads env vars. Returns the dotted keys it set.</summary>
    public static IReadOnlyList<string> WriteAppSettings(string path, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        var root = ReadObject(path);
        var keys = new List<string>();
        foreach (var (key, value) in values)
        {
            var parts = key.Split("__", StringSplitOptions.RemoveEmptyEntries);
            var node = root;
            foreach (var part in parts[..^1])
            {
                if (node[part] is not JsonObject child)
                {
                    child = [];
                    node[part] = child;
                }
                node = child;
            }
            node[parts[^1]] = value;
            keys.Add(string.Join('.', parts));
        }
        WriteJson(path, root);
        return keys;
    }

    public static void RemoveAppSettings(WrittenFile file)
    {
        if (!File.Exists(file.Path)) return;
        var root = ReadObject(file.Path);
        foreach (var key in file.Keys ?? [])
        {
            var parts = key.Split('.');
            var chain = new List<JsonObject> { root };
            foreach (var part in parts[..^1])
            {
                if (chain[^1][part] is not JsonObject child) break;
                chain.Add(child);
            }
            if (chain.Count != parts.Length) continue;
            chain[^1].Remove(parts[^1]);
            // Drop sections the removal left empty, innermost first.
            for (var i = chain.Count - 1; i > 0 && chain[i].Count == 0; i--) chain[i - 1].Remove(parts[i - 1]);
        }
        if (file.Created && root.Count == 0) File.Delete(file.Path);
        else WriteJson(file.Path, root);
    }

    public static void Remove(WrittenFile file)
    {
        switch (file.Kind)
        {
            case "dotenv": RemoveDotEnv(file); break;
            case "launchSettings": RemoveLaunchProfile(file); break;
            case "appSettings": RemoveAppSettings(file); break;
        }
    }

    internal static string StripBlocks(string content)
    {
        var kept = new List<string>();
        var inside = false;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
        {
            if (!inside && line.TrimEnd() == BeginMarker) { inside = true; continue; }
            if (inside)
            {
                if (line.TrimEnd() == EndMarker) inside = false;
                continue;
            }
            kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    /// <summary>Quoted only when a dotenv parser would otherwise cut the value short or misread it.</summary>
    internal static string DotEnvValue(string value)
    {
        if (value.Length > 0 && value.IndexOfAny([' ', '#', '"', '\'', '\n', '\r', '\t', '\\']) < 0) return value;
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
    }

    static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path)) return [];
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? throw new InvalidOperationException($"{path} isn't a JSON object");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"{path} isn't valid JSON, so sprig won't change it: {e.Message}");
        }
    }

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static void WriteJson(string path, JsonObject root) => WriteText(path, root.ToJsonString(Indented) + "\n");

    /// <summary>Left alone when it already says this: dev servers (Vite) and config watchers (ASP.NET's reloadOnChange)
    /// restart or reload on any write, so an <c>up</c> that changes nothing would bounce running apps.</summary>
    static void WriteText(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    static void DeleteWithEmptyFolder(string path)
    {
        File.Delete(path);
        var dir = System.IO.Path.GetDirectoryName(path)!;
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }
}
