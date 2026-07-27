using Sprig.Core.Processes;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Core.Demo;

/// <summary>Thrown when the sample setup can't be built or safely removed.</summary>
public sealed class SampleSetupException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// How far to build the sample. Guides start the sandbox at the stage <i>before</i> the concept they teach,
/// so the user performs the step themselves: "register your first repo" starts at <see cref="RepoOnDisk"/>,
/// the polyrepo wiring guide at <see cref="ReposRegistered"/>, and so on. The guided tour uses
/// <see cref="Running"/> — the whole thing, already done.
/// </summary>
public enum SampleStage
{
    /// <summary>Sample repos are real git repos on disk, but nothing is registered with sprig yet.</summary>
    RepoOnDisk,
    /// <summary>Both repos registered; no stack defined.</summary>
    ReposRegistered,
    /// <summary>A stack wiring the repos is saved; no workspace created.</summary>
    StackWired,
    /// <summary>A workspace exists — the full worked example.</summary>
    Running,
}

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

    // Step ids for the build checklist. Deliberately coarse: someone meeting sprig for the first time
    // is served better by four plain-English phases than by the full per-repo create checklist (which
    // names worktrees and env clobbering — concepts the tour hasn't taught yet).
    static class Steps
    {
        public const string Scaffold = "sample:scaffold";
        public const string Register = "sample:register";
        public const string Stack = "sample:stack";
        public const string Workspace = "sample:workspace";
    }

    /// <summary>
    /// The checklist <see cref="BuildTo"/> reports against, for a UI to render up front — only the steps that
    /// actually run for <paramref name="stage"/>, so a guide starting at an early stage doesn't show later
    /// rows that never complete. Matches the ids <see cref="BuildTo"/> reports.
    /// </summary>
    public static IReadOnlyList<WorkspaceStep> PlanBuild(SampleStage stage = SampleStage.Running)
    {
        var steps = new List<WorkspaceStep> { new(Steps.Scaffold, "Create two sample repos") };
        if (stage >= SampleStage.ReposRegistered) steps.Add(new(Steps.Register, "Register them with sprig"));
        if (stage >= SampleStage.StackWired) steps.Add(new(Steps.Stack, "Define a stack that wires them together"));
        if (stage >= SampleStage.Running) steps.Add(new(Steps.Workspace, $"Create the '{WorkspaceName}' workspace"));
        return steps;
    }

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
    /// Build the whole worked example (<see cref="SampleStage.Running"/>), or return the existing one.
    /// Idempotent, and safe after a crash or a half-finished attempt: leftover state is destroyed rather
    /// than repaired.
    /// </summary>
    /// <param name="progress">Optional checklist progress from the underlying workspace create.</param>
    public InstanceRecord Build(IProgress<WorkspaceStepProgress>? progress = null)
    {
        MarkStore();
        if (Existing() is { } ready) return ready;
        BuildTo(SampleStage.Running, progress);
        return Existing()!;
    }

    /// <summary>
    /// Reset the demo store and build the sample up to (and including) <paramref name="stage"/>. Always
    /// starts clean — a guide hands the user a known, replayable starting point, so a previous attempt's
    /// leftovers are cleared rather than reasoned about.
    /// </summary>
    public void BuildTo(SampleStage stage, IProgress<WorkspaceStepProgress>? progress = null)
    {
        MarkStore();

        // A guide mutates the sandbox (that's the point), so re-entering must reset it. Rebuilding from
        // scratch each time keeps guides independent and immune to a half-finished previous run.
        if (Directory.Exists(SampleReposDir) || workspaces.Get(WorkspaceName) is not null)
        {
            Destroy();
            MarkStore();
        }

        try
        {
            Run(progress, Steps.Scaffold, () =>
            {
                ScaffoldRepo(SampleFixtures.ApiRepo, SampleFixtures.ApiFiles);
                ScaffoldRepo(SampleFixtures.WebRepo, SampleFixtures.WebFiles);
            });
            if (stage == SampleStage.RepoOnDisk) return;

            Run(progress, Steps.Register, () =>
            {
                repos.Add(Path.Combine(SampleReposDir, SampleFixtures.ApiRepo));
                repos.Add(Path.Combine(SampleReposDir, SampleFixtures.WebRepo));
            });
            if (stage == SampleStage.ReposRegistered) return;

            Run(progress, Steps.Stack, () => stacks.Save(SampleFixtures.Stack()));
            if (stage == SampleStage.StackWired) return;

            // The inner create reports against its own step ids, which this coarse checklist has no
            // rows for; a UI ignores unknown ids, so they're simply not forwarded.
            Run(progress, Steps.Workspace,
                () => workspaces.Create(resolver.Resolve(SampleFixtures.StackName), WorkspaceName));
        }
        catch (Exception ex)
        {
            // Never leave the user in a half-built sandbox: unwind, then report what actually failed.
            TryQuietly(Destroy);
            throw new SampleSetupException($"could not build the sample setup: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The stage the demo store is currently at, or null if there's no sample present. Read when deciding
    /// whether a guide's starting stage is already in place (a cheap reuse) or needs a rebuild.
    /// </summary>
    public SampleStage? CurrentStage()
    {
        if (!Directory.Exists(SampleReposDir)) return null;
        if (Existing() is not null) return SampleStage.Running;
        if (stacks.Get(SampleFixtures.StackName) is not null) return SampleStage.StackWired;
        if (repos.Get(SampleFixtures.ApiRepo) is not null) return SampleStage.ReposRegistered;
        return SampleStage.RepoOnDisk;
    }

    /// <summary>
    /// Deliberately break the sample workspace by deleting one repo's worktree folder out from under git —
    /// the "someone ran rm on it" scenario the drift guide teaches. Reality now disagrees with the instance
    /// record; <c>WorkspaceReconciler</c> classifies it as a missing folder, and Repair rebuilds it. The
    /// source repo is never touched.
    /// </summary>
    public void BreakWorktree()
    {
        var record = workspaces.Get(WorkspaceName)
            ?? throw new SampleSetupException("no sample workspace to break — build it first");
        var repo = record.Repos.FirstOrDefault()
            ?? throw new SampleSetupException("the sample workspace has no repos");
        if (Directory.Exists(repo.WorktreePath)) DeleteTree(repo.WorktreePath);
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

    /// <summary>Run one build phase, reporting Running → Done around it (Error if it throws).</summary>
    static void Run(IProgress<WorkspaceStepProgress>? progress, string stepId, Action work)
    {
        progress?.Report(new(stepId, WorkspaceStepState.Running));
        try { work(); }
        catch (Exception ex)
        {
            progress?.Report(new(stepId, WorkspaceStepState.Error, ex.Message));
            throw;
        }
        progress?.Report(new(stepId, WorkspaceStepState.Done));
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
