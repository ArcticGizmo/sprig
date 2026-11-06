using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;

namespace Sprig.Engine.Workspaces;

/// <summary>Each workspace's record, its last plan and its generated compose files, under
/// <c>&lt;store&gt;/workspaces/&lt;orchestration&gt;/&lt;workspace&gt;</c>.</summary>
public sealed class WorkspaceStore(ISprigPaths paths)
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    string Root => Path.Combine(paths.Root, "workspaces");

    public string Folder(string orchestration, string workspace) => Path.Combine(Root, orchestration, workspace);

    public WorkspaceRecord? Get(string orchestration, string workspace) => Read<WorkspaceRecord>(Path.Combine(Folder(orchestration, workspace), "workspace.json"));

    public IReadOnlyList<WorkspaceRecord> List()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateFiles(Root, "workspace.json", SearchOption.AllDirectories)
            .Select(Read<WorkspaceRecord>)
            .OfType<WorkspaceRecord>()
            .OrderBy(r => r.Orchestration, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Save(WorkspaceRecord record) => Write(Path.Combine(Folder(record.Orchestration, record.Name), "workspace.json"), record);

    /// <summary>The plan the workspace last came up with: the values every app was given.</summary>
    public Plan? GetPlan(string orchestration, string workspace) => Read<Plan>(Path.Combine(Folder(orchestration, workspace), "plan.json"));

    public void SavePlan(Plan plan) => Write(Path.Combine(Folder(plan.Orchestration, plan.Workspace), "plan.json"), plan);

    public void Delete(string orchestration, string workspace)
    {
        var folder = Folder(orchestration, workspace);
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
