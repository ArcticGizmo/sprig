using System.Collections.Generic;
using Avalonia;

namespace Sprig.App.Coach;

/// <summary>
/// The vocabulary of coachmark anchors — every element the coach is allowed to point at, named once.
///
/// Consts rather than loose strings so a script referencing a dead anchor is a compile error, and so the
/// full set is greppable from one place. Two kinds resolve differently (see <see cref="AnchorResolver"/>):
/// ordinary controls carry the id as an <c>AutomationProperties.AutomationId</c> in XAML — one attribute,
/// no logic, and it doubles as accessibility metadata — while custom-drawn surfaces publish rects through
/// <see cref="IAnchorSource"/>, because their contents are pixels, not controls.
/// </summary>
public static class Anchors
{
    // --- Chrome (AutomationId in XAML) ---
    public const string ReposAdd = "repos.add";
    public const string RepoModules = "repo.modules";
    public const string RepoAddModule = "repo.addModule";
    public const string RepoDetail = "repo.detail";
    public const string WorkspaceNew = "workspace.new";
    public const string WorkspaceCreate = "workspace.create";
    public const string WorkspaceDetail = "workspace.detail";
    public const string WorkspaceRepair = "workspace.repair";
    public const string WorkspaceDocker = "workspace.docker";
    public const string PoolCheckoutConfirm = "pool.checkout.confirm";
    public const string SettingsPortsInUse = "settings.portsInUse";

    /// <summary>
    /// Every anchor that must be declared as an <c>AutomationProperties.AutomationId</c> in the views.
    ///
    /// This list is what makes the coach's brittleness manageable: a test asserts each of these still
    /// appears in the XAML, and that the XAML declares no anchor that isn't listed here. Deleting or
    /// renaming a coached control then fails the build instead of silently leaving a callout pointing at
    /// nothing — which is the objection that normally sinks coachmarks.
    /// </summary>
    public static IReadOnlyList<string> Chrome { get; } =
    [
        ReposAdd,
        RepoModules,
        RepoAddModule,
        RepoDetail,
        WorkspaceNew,
        WorkspaceCreate,
        WorkspaceDetail,
        WorkspaceRepair,
        WorkspaceDocker,
        PoolCheckoutConfirm,
        SettingsPortsInUse,
    ];

    // --- List rows (AutomationId bound from row data in XAML) ---

    /// <summary>A specific repo's row in the Repos list, e.g. <c>repo.row:sample-api</c> — so a coachmark
    /// can spotlight one repo among several and dim the rest.</summary>
    public static string RepoRow(string name) => $"repo.row:{name}";

    /// <summary>A left-nav entry by its page title, e.g. <c>nav:Maps</c> — so a step can point at where
    /// you'd go next.</summary>
    public static string Nav(string pageTitle) => $"nav:{pageTitle}";
}

/// <summary>
/// Implemented by custom-drawn controls so the coach can point inside them. A control that renders its
/// own content owns the only copy of that content's geometry; this publishes it rather than duplicating
/// the layout maths in the coach.
/// </summary>
public interface IAnchorSource
{
    /// <summary>
    /// The bounds of <paramref name="anchorId"/> in this control's own coordinate space, or false if this
    /// source doesn't know that anchor (or it isn't currently drawn).
    /// </summary>
    bool TryGetAnchor(string anchorId, out Rect bounds);
}
