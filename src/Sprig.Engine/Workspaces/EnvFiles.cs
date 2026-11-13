using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sprig.Engine.Workspaces;

/// <summary>A file sprig wrote into a repo or worktree, kept so <c>sprig rm</c> can take its values out again.</summary>
/// <param name="Kind"><c>dotenv</c>, <c>launchSettings</c> or <c>appSettings</c>.</param>
/// <param name="Created">Sprig made the file, so removing the workspace deletes it rather than editing it.</param>
/// <param name="Keys">For <c>appSettings</c>: the dotted keys sprig set.</param>
public sealed record WrittenFile(string Path, string Kind, bool Created, string? Profile = null, IReadOnlyList<string>? Keys = null);

/// <summary>
/// Writes an app's values into env files, launch profiles and JSON settings. Each writer changes only what sprig owns
/// (a marked block, one profile, the keys it sets) and keeps everything else in the file, so a developer's own values
/// survive and removing the workspace puts the file back.
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
    /// default. Other profiles, and the rest of the file, are kept as they're written, comments included. Its env starts
    /// from the profile it puts second, so the app keeps what that gave it (usually <c>ASPNETCORE_ENVIRONMENT</c>) under
    /// a plain <c>dotnet run</c>.</summary>
    public static void WriteLaunchProfile(string path, string profile, IReadOnlyList<KeyValuePair<string, string>> values)
    {
        var old = ReadObject(path)["profiles"] as JsonObject ?? [];
        var displaced = old.Where(p => p.Key != profile).Select(p => p.Value as JsonObject)
            .FirstOrDefault(p => p?["commandName"]?.GetValue<string>() == "Project");
        var env = displaced?["environmentVariables"]?.DeepClone() as JsonObject ?? [];
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

        var text = Edit(path);
        string[] at = ["profiles", profile];
        // Only moved when something else is first, so an up that changes nothing leaves the file alone.
        if (text.Names(["profiles"]) is { Count: > 0 } names && names[0] != profile) text.Remove(at);
        text.Set(at, mine.ToJsonString(Indented), first: true);
        WriteText(path, text.Text);
    }

    public static void RemoveLaunchProfile(WrittenFile file)
    {
        if (!File.Exists(file.Path)) return;
        var text = Edit(file.Path);
        text.Remove(["profiles", file.Profile ?? "sprig"]);
        // Taking the last profile out takes the emptied "profiles" with it, leaving nothing of a file sprig made.
        if (file.Created && text.RootIsEmpty) DeleteWithEmptyFolder(file.Path);
        else WriteText(file.Path, text.Text);
    }

    /// <summary>What a comment above a key sprig took over begins with; the rest is the value it had, as written.</summary>
    public const string KeptPrefix = "sprig took this over. Yours was: ";

    /// <summary>A key's sections, from <c>Section__Key</c> (how .NET reads env vars) or <c>Section:Key</c> (how it
    /// names settings).</summary>
    public static string[] SettingPath(string key) => key.Split(["__", ":"], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A key as <see cref="WrittenFile.Keys"/> keeps it. Older records joined the sections with dots.</summary>
    static string[] KeptPath(string key) => key.Contains(':') ? key.Split(':') : key.Split('.');

    /// <summary>
    /// Set each value in a JSON settings file, nested into sections (see <see cref="SettingPath"/>), changing only those
    /// values' text. A key the file already had, that sprig didn't add itself, keeps its value in a comment above it
    /// (<see cref="KeptPrefix"/>), so removing the workspace can put it back; the value stays in the file it was in.
    /// Returns the keys it set, as <c>Section:Key</c>.
    /// </summary>
    /// <param name="owned">The keys sprig set here last time (<see cref="WrittenFile.Keys"/>): ones it added, unless
    /// they carry a kept comment.</param>
    public static IReadOnlyList<string> WriteAppSettings(string path, IReadOnlyList<KeyValuePair<string, string>> values,
        IReadOnlyCollection<string>? owned = null)
    {
        var text = Edit(path);
        var mine = (owned ?? []).Select(k => string.Join(':', KeptPath(k))).ToHashSet(StringComparer.Ordinal);
        var keys = new List<string>();
        foreach (var (key, value) in values)
        {
            var parts = SettingPath(key);
            var name = string.Join(':', parts);
            if (text.Get(parts) is { } had && !mine.Contains(name) && text.CommentAbove(parts, KeptPrefix) is null)
                text.AddCommentAbove(parts, KeptPrefix + Compact(had));
            text.Set(parts, JsoncText.Quote(value));
            if (!keys.Contains(name)) keys.Add(name);
        }
        WriteText(path, text.Text);
        return keys;
    }

    /// <summary>
    /// Take sprig's keys back out. A key it took over gets the value its comment kept, and one it added goes, unless
    /// the value has changed since sprig wrote it: then it's yours, and only sprig's comment goes.
    /// </summary>
    /// <param name="wrote">What sprig last wrote, by key as <see cref="SettingPath"/> reads it; null when unknown,
    /// which counts as unchanged.</param>
    public static void RemoveAppSettings(WrittenFile file, IReadOnlyDictionary<string, string>? wrote = null)
    {
        if (!File.Exists(file.Path)) return;
        var text = Edit(file.Path);
        var written = wrote?.ToDictionary(w => string.Join(':', SettingPath(w.Key)), w => JsoncText.Quote(w.Value), StringComparer.Ordinal);
        foreach (var key in file.Keys ?? [])
        {
            var parts = KeptPath(key);
            var unchanged = written is null || !written.TryGetValue(string.Join(':', parts), out var mine) || text.Get(parts) == mine;
            if (text.CommentAbove(parts, KeptPrefix) is { } kept)
            {
                if (unchanged) text.Set(parts, kept);
                text.RemoveCommentAbove(parts, KeptPrefix);
            }
            else if (unchanged) text.Remove(parts);
        }
        if (file.Created && text.RootIsEmpty) File.Delete(file.Path);
        else WriteText(file.Path, text.Text);
    }

    /// <param name="wrote">For <c>appSettings</c>, what sprig last wrote (see <see cref="RemoveAppSettings"/>).</param>
    public static void Remove(WrittenFile file, IReadOnlyDictionary<string, string>? wrote = null)
    {
        switch (file.Kind)
        {
            case "dotenv": RemoveDotEnv(file); break;
            case "launchSettings": RemoveLaunchProfile(file); break;
            case "appSettings": RemoveAppSettings(file, wrote); break;
        }
    }

    /// <summary>The file as text to edit in place; an empty object when there's no file yet.</summary>
    static JsoncText Edit(string path)
    {
        try
        {
            return new JsoncText(File.Exists(path) ? File.ReadAllText(path) : "");
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidOperationException($"{path}: {e.Message}, so sprig won't change it");
        }
    }

    /// <summary>A value on one line, for a comment.</summary>
    static string Compact(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?.ToJsonString(JsoncText.Readable) ?? json;
        }
        catch (JsonException)
        {
            return json.ReplaceLineEndings(" ");
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

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JsoncText.Readable.Encoder };

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
