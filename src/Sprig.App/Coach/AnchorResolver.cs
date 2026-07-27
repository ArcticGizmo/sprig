using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Sprig.App.Coach;

/// <summary>
/// Finds the on-screen rectangle for an anchor id, in the coordinate space of a root visual.
///
/// Two lookups, because the app has two kinds of target. An ordinary control is found by matching
/// <c>AutomationProperties.AutomationId</c> while walking the visual tree — so day-to-day code declares an
/// anchor with one XAML attribute and no coach-specific code. A custom-drawn surface (the wiring canvas)
/// can't be found that way because its contents aren't controls, so any <see cref="IAnchorSource"/> in the
/// tree is asked whether it owns the anchor, and its local rect is transformed up.
/// </summary>
public static class AnchorResolver
{
    /// <summary>
    /// Resolve <paramref name="anchorId"/> to a rect in <paramref name="root"/>'s coordinates.
    /// Scrolls the target into view first when it sits inside a <see cref="ScrollViewer"/>, then measures —
    /// measuring before scrolling would give the position it used to be at, or one that's clipped away.
    /// </summary>
    /// <returns>False when the anchor isn't in the tree right now (wrong page, overlay closed, not drawn).</returns>
    public static bool TryResolve(Visual root, string anchorId, out Rect bounds)
    {
        bounds = default;

        if (TryFindControl(root, anchorId) is { } control)
        {
            // A control off-screen inside a scroller has to be brought in before its bounds mean anything.
            if (control.FindAncestorOfType<ScrollViewer>() is not null)
            {
                control.BringIntoView();
                // BringIntoView queues layout; flush it so the rect below is the post-scroll one.
                (root as Layoutable)?.UpdateLayout();
            }

            return TryTransform(control, root, new Rect(control.Bounds.Size), out bounds);
        }

        // Not a control — ask the custom-drawn surfaces.
        foreach (var source in root.GetVisualDescendants().OfType<IAnchorSource>())
        {
            if (!source.TryGetAnchor(anchorId, out var local)) continue;
            if (source is not Visual visual) continue;

            if (visual is Control { } host && host.FindAncestorOfType<ScrollViewer>() is not null)
            {
                // Scroll the *anchor's* rect into view, not the whole (often huge) canvas.
                host.BringIntoView(local);
                (root as Layoutable)?.UpdateLayout();
            }

            return TryTransform(visual, root, local, out bounds);
        }

        return false;
    }

    /// <summary>Every anchor id currently resolvable under <paramref name="root"/> — used by the tests that
    /// assert a script's anchors all still exist.</summary>
    public static IReadOnlyList<string> DeclaredAnchors(Visual root) =>
        root.GetVisualDescendants()
            .OfType<Control>()
            .Select(AutomationProperties.GetAutomationId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList()!;

    static Control? TryFindControl(Visual root, string anchorId) =>
        root.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => AutomationProperties.GetAutomationId(c) == anchorId);

    static bool TryTransform(Visual from, Visual to, Rect local, out Rect bounds)
    {
        bounds = default;
        // Null when the visual isn't currently attached/realised — treat as "not resolvable".
        if (from.TransformToVisual(to) is not { } transform) return false;

        var rect = local.TransformToAABB(transform);
        if (rect.Width <= 0 || rect.Height <= 0) return false;

        bounds = rect;
        return true;
    }
}
