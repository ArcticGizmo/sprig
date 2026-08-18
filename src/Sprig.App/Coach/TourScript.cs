using System.Collections.Generic;
using System.Threading.Tasks;
using Sprig.App.ViewModels;
using Sprig.Core.Demo;

namespace Sprig.App.Coach;

/// <summary>
/// The guided tour, as a coachmark script. It walks the model over the fully-built sample — a repo declares
/// what it provides and needs, a map composes repos, a workspace materialises a slice — and hands the user
/// back to their own repos.
///
/// Formerly a separate top-of-window narration strip; now it's coachmarks like everything else, so each step
/// dims the page and rings exactly what it's talking about. Opening and closing steps are whole-page (no
/// anchor) beats; the middle steps spotlight the detail panel for repos, the map, and the workspace.
/// </summary>
public static class TourScript
{
    /// <param name="nav">Navigation + the "start infra" action.</param>
    /// <param name="includeInfra">Add the optional "start the containers" step — only when a Docker daemon is
    /// actually up, since it needs one and pulls an image.</param>
    public static IReadOnlyList<CoachMark> Marks(Navigator nav, bool includeInfra) =>
    [
        new(null,
            "This is one working sprig, built for you",
            "Two self-describing repos, one map composing them, and a workspace running from it. Everything you're about to see is real — the same engine your own repos will use.")
        { Prepare = () => { nav.GoHome(); return Task.CompletedTask; } },

        // Orient first: spotlight the actual repo in the list, so "a repo" is a thing on screen before the
        // next step explains what it declares.
        new(Anchors.RepoRow(SampleFixtures.ApiRepo),
            "Start with the repos",
            "Two repos are registered here. This one, sample-api, is the backend.")
        { Side = CoachSide.Right, Prepare = () => { nav.ShowFirstRepo(); return Task.CompletedTask; } },

        new(Anchors.RepoDetail,
            "A repo describes itself",
            "sample-api provides an api capability (its port, and a url built from it) and a db port. sample-web needs api. Each repo owns what it provides — which is why it stays portable, and composes with others by capability name.")
        { Side = CoachSide.Left, Prepare = () => { nav.ShowFirstRepo(); return Task.CompletedTask; } },

        // Point at where you'd go next, before actually going there.
        new(Anchors.Nav("Maps"),
            "Compose the repos here",
            "A map lists the repos in play. The wiring is derived: sample-web's need for api is matched to sample-api's provide — automatically, by capability name. The next stop is where that composition lives.")
        { Side = CoachSide.Right },

        new(null,
            "The map composes them — no manual wiring",
            "sample composes sample-api and sample-web. sample-web references ${sprig.api.url}, and the map resolves it from sample-api's provided url. One capability, matched by name, and neither repo hard-codes the other.")
        { Prepare = () => { nav.ShowFirstMap(); return Task.CompletedTask; } },

        new(Anchors.Nav("Workspaces"),
            "Then bring it to life here",
            "When you create a workspace, sprig builds an isolated slice of the map — a worktree and branch per repo, its own allocated ports, its own docker infra — that you can actually run.")
        { Side = CoachSide.Right },

        new(Anchors.WorkspaceDetail,
            "Real numbers, written into real files",
            "Each repo got its own worktree on a sprig branch, and the ports were allocated for this workspace alone. Those resolved values are already in each worktree's .env and in a generated compose file — open a worktree to see them.")
        { Side = CoachSide.Left, Prepare = () => { nav.ShowFirstWorkspace(); return Task.CompletedTask; } },

        // Optional, and only when Docker is up: it needs a daemon and pulls an image. Everything else stands
        // alone without it — compose *generation* is pure file I/O, and that's the lesson.
        .. includeInfra
            ?
            [
                new CoachMark(Anchors.WorkspaceDocker,
                    "Its database gets a port of its own too",
                    "sample-api's compose file declares one Postgres on 5432. sprig generated an isolated copy for this workspace with the container name and host port rewritten, so another workspace can run the same database at the same time. Starting it now proves the point — the first run pulls a small image.")
                {
                    Side = CoachSide.Left,
                    Prepare = () => { nav.ShowFirstWorkspace(); return Task.CompletedTask; },
                    Perform = () => nav.StartFirstWorkspaceInfra(),
                },
            ]
            : (CoachMark[])[],

        new(null,
            "Now point sprig at something of yours",
            "Your repo needs one committed file — a .sprig.json declaring what it consumes — and sprig writes it for you when you add the repo. Leave the tour whenever you like; the sample is deleted, and nothing of yours was touched.")
        { Prepare = () => { nav.GoHome(); return Task.CompletedTask; } },
    ];
}
