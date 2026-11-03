using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;

namespace Sprig.Engine.Sprouts;

/// <summary>Each sprout's record, its last plan and its generated compose files, under
/// <c>&lt;store&gt;/sprouts/&lt;trellis&gt;/&lt;sprout&gt;</c>.</summary>
public sealed class SproutStore(ISprigPaths paths)
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    string Root => Path.Combine(paths.Root, "sprouts");

    public string Folder(string trellis, string sprout) => Path.Combine(Root, trellis, sprout);

    public SproutRecord? Get(string trellis, string sprout) => Read<SproutRecord>(Path.Combine(Folder(trellis, sprout), "sprout.json"));

    public IReadOnlyList<SproutRecord> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateFiles(Root, "sprout.json", SearchOption.AllDirectories)
            .Select(Read<SproutRecord>)
            .OfType<SproutRecord>()
            .OrderBy(r => r.Trellis, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Save(SproutRecord record) => Write(Path.Combine(Folder(record.Trellis, record.Name), "sprout.json"), record);

    /// <summary>The plan the sprout last came up with: the values every app was given.</summary>
    public Plan? GetPlan(string trellis, string sprout) => Read<Plan>(Path.Combine(Folder(trellis, sprout), "plan.json"));

    public void SavePlan(Plan plan) => Write(Path.Combine(Folder(plan.Trellis, plan.Sprout), "plan.json"), plan);

    public void Delete(string trellis, string sprout)
    {
        var folder = Folder(trellis, sprout);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        var parent = Path.GetDirectoryName(folder)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
    }

    static T? Read<T>(string path)
    {
        try { return JsonFile.Read<T>(path, Options); }
        catch (JsonException) { return default; }
    }

    static void Write<T>(string path, T value) => JsonFile.Write(path, value, Options);
}
