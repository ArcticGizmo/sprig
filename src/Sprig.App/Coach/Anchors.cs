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
    public const string SettingsPortsInUse = "settings.portsInUse";
    public const string StackCanvas = "stack.canvas";

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
        SettingsPortsInUse,
        StackCanvas,
    ];

    // --- Inside the wiring canvas (resolved by IAnchorSource, keyed on domain identity) ---
    /// <summary>A named stack port on the canvas rail, e.g. <c>stack.port:api_port</c>.</summary>
    public static string StackPort(string port) => $"stack.port:{port}";

    /// <summary>A repo node on the canvas, e.g. <c>stack.node:sample-api</c>.</summary>
    public static string StackNode(string repo) => $"stack.node:{repo}";

    /// <summary>One repo input's pin (its jack + label), e.g. <c>stack.pin:sample-web/apiUrl</c>.</summary>
    public static string StackPin(string repo, string input) => $"stack.pin:{repo}/{input}";

    /// <summary>The canvas's auto-wire button (drawn, not a control).</summary>
    public const string StackAutoWire = "stack.autoWire";
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
