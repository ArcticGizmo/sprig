using Sprig.Core.Processes;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Demo;

/// <summary>Thrown when the sample setup can't be built or safely removed.</summary>
public sealed class SampleSetupException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Builds a complete, working sprig setup out of nothing, for the guided tour: two throwaway git
/// repos, a stack wiring them together, and one workspace created from it.
///
/// Two rules make this safe and honest:
/// <list type="number">
/// <item><b>It uses the real engine.</b> Registry → stack store → resolver →
/// <see cref="WorkspaceService.Create"/>, exactly as the app does. Nothing is faked and no store
/// JSON is hand-written, so what the user sees is what sprig actually produces.</item>
/// <item><b>It only ever touches its own store.</b> Point it at a demo root
/// (<see cref="AppProfile.DemoFolderName"/>). The sample repos live <i>inside</i> that root, and
/// <see cref="WorkspaceService.Create"/> puts each worktree beside its repo — so every artefact,
/// including the worktrees, is under one directory that <see cref="Destroy"/> deletes.</item>
/// </list>
///
/// A demo store is worth nothing, so this class never repairs one: anything unexpected is destroyed
/// and rebuilt.
/// </summary>
public sealed class SampleSetup(
    ISprigPaths paths,
    IProcessRunner runner,
    RepoRegistryStore repos,
    StackStore stacks,
    StackResolver resolver,
    WorkspaceService workspaces)
{
    /// <summary>The workspace the tour creates.</summary>
    public const string WorkspaceName = "tour";

    /// <summary>Marker file written at the root of a demo store; <see cref="Destroy"/> refuses without it.</summary>
    public const string MarkerFileName = ".sprig-demo";

    const string MarkerContent = """
        This directory is a sprig DEMO store, created by the guided tour.

        Everything in it — including the sample repos under .\sample and their worktrees — is
        disposable, and is deleted when you leave the tour. Nothing here affects your real sprig
        store or your own repos.
        """;

    /// <summary>Where the throwaway sample repos live (inside the demo store, so cleanup is one delete).</summary>
    public string SampleReposDir => Path.Combine(paths.Root, "sample");

    string MarkerPath => Path.Combine(paths.Root, MarkerFileName);

    /// <summary>
    /// The already-built sample, or null if there isn't a usable one. "Usable" means the record
    /// exists <i>and</i> every worktree it names is still on disk — a half-deleted sample counts as
    /// absent, because rebuilding is always cheaper than reasoning about it.
    /// </summary>
    public InstanceRecord? Existing()
    {
        var record = workspaces.Get(WorkspaceName);
        if (record is null) return null;
        return record.Repos.All(r => Directory.Exists(r.WorktreePath)) ? record : null;
    }

    /// <summary>
    /// Build the sample setup, or return the existing one. Idempotent, and safe to call after a
    /// crash or a half-finished attempt: leftover state is destroyed rather than repaired.
    /// </summary>
    /// <param name="progress">Optional checklist progress from the underlying workspace create.</param>
    public InstanceRecord Build(IProgress<WorkspaceStepProgress>? progress = null)
    {
        MarkStore();

        if (Existing() is { } ready) return ready;

        // Leftovers from an abandoned attempt: no state here is worth keeping.
        if (Directory.Exists(SampleReposDir) || workspaces.Get(WorkspaceName) is not null)
        {
            Destroy();
            MarkStore();
        }

        try
        {
            ScaffoldRepo(SampleFixtures.ApiRepo, SampleFixtures.ApiFiles);
            ScaffoldRepo(SampleFixtures.WebRepo, SampleFixtures.WebFiles);

            repos.Add(Path.Combine(SampleReposDir, SampleFixtures.ApiRepo));
            repos.Add(Path.Combine(SampleReposDir, SampleFixtures.WebRepo));

            stacks.Save(SampleFixtures.Stack());

            return workspaces.Create(resolver.Resolve(SampleFixtures.StackName), WorkspaceName, progress);
        }
        catch (Exception ex)
        {
            // Never leave the user in a half-built tour: unwind, then report what actually failed.
            TryQuietly(Destroy);
            throw new SampleSetupException($"could not build the sample setup: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Remove everything the sample owns — containers, worktrees, branches, store, sample repos —
    /// by deleting the demo store root. Best-effort throughout: a stopped Docker daemon or a
    /// half-built sample must never block cleanup of the files.
    /// </summary>
    public void Destroy()
    {
        if (!Directory.Exists(paths.Root)) return;

        // This method deletes a directory tree, so it verifies it owns that tree first. Build writes
        // the marker before anything else, so even a half-built store has one; a root without it is
        // not ours and never gets deleted.
        if (!File.Exists(MarkerPath))
            throw new SampleSetupException(
                $"refusing to delete '{paths.Root}': no {MarkerFileName} marker, so this is not a sprig demo store");

        if (workspaces.Get(WorkspaceName) is not null)
        {
            TryQuietly(() => workspaces.Down(WorkspaceName, removeVolumes: true));
            TryQuietly(() => workspaces.Remove(WorkspaceName, force: true));
        }

        DeleteTree(paths.Root);
    }

    void MarkStore()
    {
        Directory.CreateDirectory(paths.Root);
        File.WriteAllText(MarkerPath, MarkerContent);
    }

    void ScaffoldRepo(string name, IReadOnlyList<SampleFile> files)
    {
        var dir = Path.Combine(SampleReposDir, name);
        SampleFixtures.WriteTo(files, dir);

        Git(dir, "init", "-b", "main");
        Git(dir, "add", "-A");
        // Identity and signing are forced per-command rather than inherited: this throwaway repo must
        // commit successfully on a machine with no git identity configured, and a global commit.gpgsign
        // would otherwise block the tour waiting on a passphrase prompt no one can answer.
        Git(dir,
            "-c", "user.email=demo@sprig.local",
            "-c", "user.name=sprig demo",
            "-c", "commit.gpgsign=false",
            "commit", "-m", "Sample repo for the sprig guided tour");
    }

    void Git(string workingDirectory, params string[] args)
        => runner.Run("git", args, workingDirectory).EnsureSuccess();

    /// <summary>
    /// Delete a tree containing git repos, on Windows. Two things get in the way:
    /// <list type="bullet">
    /// <item>git writes its object files <b>read-only</b>, and <see cref="Directory.Delete(string,bool)"/>
    /// refuses those — so the attribute is cleared first. Retrying alone never fixes this.</item>
    /// <item>git briefly holds pack files open after a worktree operation — so a transient
    /// <see cref="IOException"/> is retried.</item>
    /// </list>
    /// </summary>
    static void DeleteTree(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                    throw new SampleSetupException(
                        $"could not delete the demo store at '{path}': {ex.Message}", ex);
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Drop the read-only bit from every file under <paramref name="dir"/> (git's objects).</summary>
    static void ClearReadOnlyAttributes(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }

    static void TryQuietly(Action action)
    {
        try { action(); }
        catch { /* teardown is best-effort by design — see the summary */ }
    }
}
