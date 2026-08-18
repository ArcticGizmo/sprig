using System.Collections.Generic;
using System.Threading.Tasks;
using Sprig.App.ViewModels;

namespace Sprig.App.Coach;

/// <summary>
/// A small script that exists to prove the coachmark mechanism against the anchor cases the real walkthrough
/// has to handle — not to teach anything yet:
///
/// <list type="number">
/// <item><b>Plain chrome</b> — a button sitting in view, resolved straight from its AutomationId.</item>
/// <item><b>Inside a ScrollViewer</b> — a panel below the fold, which has to be scrolled into view before its
/// rectangle means anything.</item>
/// </list>
///
/// (A third case — a custom-drawn anchor resolved through <see cref="IAnchorSource"/> — returns with the map
/// canvas, once that surface publishes its own hit-test rects.)
/// </summary>
public static class CoachSpikeScript
{
    public static IReadOnlyList<CoachMark> Marks(Navigator nav) =>
    [
        new(Anchors.ReposAdd,
            "Case 1 — a plain control",
            "This button is anchored with one XAML attribute and nothing else. It was already on screen, so the only work was matching its automation id and transforming its bounds.")
        { Side = CoachSide.Below, Prepare = () => { nav.GoToRepos(); return Task.CompletedTask; } },

        new(Anchors.SettingsPortsInUse,
            "Case 2 — below the fold",
            "This panel starts outside the viewport. Measuring it before scrolling would give the wrong rectangle, so the resolver brings it into view and flushes layout first. The callout flipped side to stay on screen.")
        { Side = CoachSide.Above, Prepare = () => { nav.GoToSettings(); return Task.CompletedTask; } },
    ];
}
