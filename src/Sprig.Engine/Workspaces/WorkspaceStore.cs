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

    /// <summary>
    /// Remove the logs the workspace's apps wrote. A slot keeps its key from one branch to the next, so without this the
    /// next branch in it would open on the last one's logs. A log still held open is left; its app's next run starts it
    /// afresh anyway.
    /// </summary>
    public void DeleteLogs(string key)
    {
        var folder = Hosting.HostProtocol.WorkspaceLogFolder(paths, key);
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        // Up to the logs folder itself, each folder that's now empty.
        var logs = Path.GetFullPath(Hosting.HostProtocol.LogFolder(paths));
        foreach (var dir in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(folder))
            DeleteIfEmpty(dir);
        for (var parent = Path.GetDirectoryName(Path.GetFullPath(folder)); parent is not null && parent.Length > logs.Length; parent = Path.GetDirectoryName(parent))
            DeleteIfEmpty(parent);
    }

    static void DeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static T? Read<T>(string path)
    {
        try { return JsonFile.Read<T>(path, Options); }
        catch (JsonException) { return default; }
    }

    static void Write<T>(string path, T value) => JsonFile.Write(path, value, Options);
}
