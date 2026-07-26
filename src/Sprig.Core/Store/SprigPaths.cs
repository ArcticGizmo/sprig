namespace Sprig.Core.Store;

/// <summary>
/// The machine-local central store layout (default root <c>%LOCALAPPDATA%\sprig</c>, or
/// <c>sprig (Dev)</c> for a dev instance — see <see cref="AppProfile"/>).
/// An interface so tests can point it at a temp dir and the Core stays OS-agnostic.
/// </summary>
public interface ISprigPaths
{
    /// <summary>Root of the central store.</summary>
    string Root { get; }
    /// <summary>Directory holding one folder per workspace instance.</summary>
    string InstancesDir { get; }
    /// <summary>Directory holding stack/template definitions.</summary>
    string StacksDir { get; }
    /// <summary>Directory holding machine-local shared-resource definitions and their compose fragments.</summary>
    string SharedDir { get; }
    /// <summary>The known-repos registry file.</summary>
    string ReposFile { get; }
    /// <summary>The port-allocation store file.</summary>
    string PortsFile { get; }
    /// <summary>The user-settings file.</summary>
    string SettingsFile { get; }
    /// <summary>This instance's folder (generated compose, record).</summary>
    string InstanceDir(string workspace);
    /// <summary>This instance's record file.</summary>
    string InstanceRecordFile(string workspace);
}

/// <summary>Filesystem implementation of <see cref="ISprigPaths"/>.</summary>
public sealed class SprigPaths : ISprigPaths
{
    public SprigPaths(string? root = null)
        => Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.DataFolderName);

    /// <summary>
    /// Root of the guided tour's throwaway demo store — a sibling of the real one, never the same
    /// directory. Pass it where a store root is expected to run entirely against the sample.
    /// </summary>
    public static string DemoRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppProfile.DemoFolderName);

    public string Root { get; }
    public string InstancesDir => Path.Combine(Root, "instances");
    public string StacksDir => Path.Combine(Root, "stacks");
    public string SharedDir => Path.Combine(Root, "shared");
    public string ReposFile => Path.Combine(Root, "repos.json");
    public string PortsFile => Path.Combine(Root, "ports.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string InstanceDir(string workspace) => Path.Combine(InstancesDir, workspace);
    public string InstanceRecordFile(string workspace) => Path.Combine(InstanceDir(workspace), "instance.json");
}
