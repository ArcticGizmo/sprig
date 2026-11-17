using System.Text.Json;
using System.Text.Json.Serialization;
using Sprig.Engine.Planning;
using Sprig.Engine.Store;

namespace Sprig.Engine.Workspaces;

/// <summary>A slot a check-out has chosen and is still making. <paramref name="ProcessStarted"/> tells the process that
/// holds it from a later one given the same id.</summary>
public sealed record SlotClaim(string Branch, int Pid, DateTimeOffset ProcessStarted, string Token)
{
    public bool Alive()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(Pid);
            try { return Math.Abs((process.StartTime.ToUniversalTime() - ProcessStarted.UtcDateTime).TotalSeconds) < 2; }
            // Another user's process can't be looked into; counting it alive only makes a check-out choose another slot.
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return !process.HasExited; }
        }
        catch (ArgumentException) { return false; }
    }
}

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

    string ClaimsFile => Path.Combine(paths.Root, "slot-claims.json");

    /// <summary>
    /// Choose a slot and claim it in one step, under a lock, so two check-outs at once (two agents, say) never choose the
    /// same one. <paramref name="choose"/> gets the slots of <paramref name="orchestration"/> others hold now, with the
    /// branch each is for, and returns the slot it chose. The claim lasts until the returned handle is disposed, or its
    /// process ends.
    /// </summary>
    public (T Result, IDisposable Claim) ClaimSlot<T>(string orchestration, string? branch,
        Func<IReadOnlyDictionary<string, string>, (string Slot, T Result)> choose)
    {
        using var _ = new FileLock(ClaimsFile + ".lock");
        var claims = LiveClaims();
        var (slot, result) = choose(Of(claims, orchestration));
        var token = Guid.NewGuid().ToString("N");
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        claims[WorkspaceRecord.KeyOf(orchestration, slot)] = new SlotClaim(branch ?? "", Environment.ProcessId, self.StartTime.ToUniversalTime(), token);
        JsonFile.Write(ClaimsFile, claims, Options);
        return (result, new Release(this, WorkspaceRecord.KeyOf(orchestration, slot), token));
    }

    /// <summary>The slots of <paramref name="orchestration"/> held by a check-out still going, with the branch each is for.</summary>
    public IReadOnlyDictionary<string, string> ClaimedSlots(string orchestration)
    {
        using var _ = new FileLock(ClaimsFile + ".lock");
        return Of(LiveClaims(), orchestration);
    }

    static IReadOnlyDictionary<string, string> Of(Dictionary<string, SlotClaim> claims, string orchestration)
        => claims.Where(c => c.Key.StartsWith(orchestration + "/", StringComparison.Ordinal))
            .ToDictionary(c => c.Key[(orchestration.Length + 1)..], c => c.Value.Branch, StringComparer.Ordinal);

    /// <summary>The claims whose process is still running; a check-out that crashed leaves nothing held.</summary>
    Dictionary<string, SlotClaim> LiveClaims()
    {
        var claims = Read<Dictionary<string, SlotClaim>>(ClaimsFile) ?? [];
        return claims.Where(c => c.Value.Alive()).ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
    }

    sealed class Release(WorkspaceStore store, string key, string token) : IDisposable
    {
        bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            using var _ = new FileLock(store.ClaimsFile + ".lock");
            var claims = store.LiveClaims();
            if (claims.TryGetValue(key, out var claim) && claim.Token == token) claims.Remove(key);
            JsonFile.Write(store.ClaimsFile, claims, Options);
        }
    }

    static T? Read<T>(string path)
    {
        try { return JsonFile.Read<T>(path, Options); }
        catch (JsonException) { return default; }
    }

    static void Write<T>(string path, T value) => JsonFile.Write(path, value, Options);
}
