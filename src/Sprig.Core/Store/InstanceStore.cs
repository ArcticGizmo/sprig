namespace Sprig.Core.Store;

/// <summary>Reads and writes <see cref="InstanceRecord"/>s under the central store. Tolerant of a missing store.</summary>
public sealed class InstanceStore(ISprigPaths paths)
{
    /// <summary>Load one instance record, or <c>null</c> if it does not exist.</summary>
    public InstanceRecord? TryLoad(string workspace)
        => JsonFile.Read<InstanceRecord>(paths.InstanceRecordFile(workspace));

    /// <summary>Load every instance record present in the store (skips unreadable folders).</summary>
    public IReadOnlyList<InstanceRecord> LoadAll()
    {
        if (!Directory.Exists(paths.InstancesDir)) return [];

        var records = new List<InstanceRecord>();
        foreach (var dir in Directory.EnumerateDirectories(paths.InstancesDir))
        {
            var workspace = Path.GetFileName(dir);
            var record = TryLoad(workspace);
            if (record is not null) records.Add(record);
        }
        return records;
    }

    /// <summary>Write an instance record atomically.</summary>
    public void Save(InstanceRecord record)
        => JsonFile.Write(paths.InstanceRecordFile(record.Workspace), record);

    /// <summary>Remove an instance's folder from the store (idempotent).</summary>
    public void Delete(string workspace)
    {
        var dir = paths.InstanceDir(workspace);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
