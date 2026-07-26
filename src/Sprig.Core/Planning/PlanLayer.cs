namespace Sprig.Core.Planning;

/// <summary>
/// Which layer produced a value. Precedence runs strictly downward — a later layer may overwrite an
/// earlier one, nothing ever writes upward — so this doubles as the ordering used when layers disagree.
/// </summary>
public enum PlanLayer
{
    /// <summary>The repo's own <c>.sprig.json</c> — tracked, shared with the team.</summary>
    Repo = 0,

    /// <summary>The stack definition — central store, exportable, supplies every declared input.</summary>
    Stack = 1,

    /// <summary>
    /// A machine-local shared-resource overlay. Never present in a file you share with anyone; applied
    /// by sprig as a transform over the resolved plan (see docs/shared-infrastructure-ux.html).
    /// </summary>
    Shared = 2,
}
