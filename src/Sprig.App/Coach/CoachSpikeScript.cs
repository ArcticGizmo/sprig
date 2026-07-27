using System.Collections.Generic;
using System.Threading.Tasks;
using Sprig.App.ViewModels;

namespace Sprig.App.Coach;

/// <summary>
/// A three-mark script that exists to prove the coachmark mechanism against the three anchor cases the real
/// walkthrough will have to handle — not to teach anything yet:
///
/// <list type="number">
/// <item><b>Plain chrome</b> — a button sitting in view, resolved straight from its AutomationId.</item>
/// <item><b>Inside a ScrollViewer</b> — a panel below the fold, which has to be scrolled into view before its
/// rectangle means anything.</item>
/// <item><b>Inside a custom-drawn control</b> — a port node on the wiring canvas, which isn't a control at
/// all and is resolved through <see cref="IAnchorSource"/>. This one also needs real app state first: the
/// stack builder open, with repos selected and wired.</item>
/// </list>
///
/// If all three land correctly the remaining work on a full walkthrough is script authoring, not mechanism.
/// </summary>
public static class CoachSpikeScript
{
    public static IReadOnlyList<CoachMark> Marks() =>
    [
        new(Anchors.ReposAdd,
            "Case 1 — a plain control",
            "This button is anchored with one XAML attribute and nothing else. It was already on screen, so the only work was matching its automation id and transforming its bounds.",
            nav => { nav.GoToRepos(); return Task.CompletedTask; })
        { Side = CoachSide.Below },

        new(Anchors.SettingsPortsInUse,
            "Case 2 — below the fold",
            "This panel starts outside the viewport. Measuring it before scrolling would give the wrong rectangle, so the resolver brings it into view and flushes layout first. The callout flipped side to stay on screen.",
            nav => { nav.GoToSettings(); return Task.CompletedTask; })
        { Side = CoachSide.Above },

        // A repo node rather than a port: node identity is whatever repos are selected, so it's deterministic
        // here, whereas port names come from auto-wire's naming convention. Ports resolve through the exact
        // same path (Anchors.StackPort) — this case is about the mechanism, not the key.
        new(Anchors.StackNode("sample-api"),
            "Case 3 — pixels, not controls",
            "This node is drawn by the wiring canvas, so there is no control to tag. The canvas publishes the same rectangle it already uses for hit-testing, keyed by repo name — so the highlight can never disagree with what you can click or drag.",
            nav => nav.OpenStackBuilderWired())
        { Side = CoachSide.Left },
    ];
}
