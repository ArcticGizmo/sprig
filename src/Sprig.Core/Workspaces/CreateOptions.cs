namespace Sprig.Core.Workspaces;

/// <summary>Per-create choices that aren't part of the stack — machine-local, and never recorded on it.</summary>
public sealed record CreateOptions
{
    /// <summary>
    /// Build this workspace with no shared-resource overlays at all: private infrastructure on allocated
    /// ports, exactly as if the feature didn't exist.
    ///
    /// <para>This is a first-class state, not an escape hatch. Wanting one docker project per worktree
    /// because it's predictable and contained is a legitimate preference, and reproducing a bug against a
    /// private database is a legitimate need — so a stack must stay complete and valid without any
    /// overlay, and this is what proves it.</para>
    /// </summary>
    public bool NoShared { get; init; }
}
