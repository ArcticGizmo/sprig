using System.Collections.Generic;
using System.Threading.Tasks;
using Sprig.App.ViewModels;

namespace Sprig.App.Coach;

/// <summary>
/// The guided tour, as a coachmark script. It walks the one-directional model over the fully-built sample —
/// a repo declares, a stack supplies, a workspace materialises — and hands the user back to their own repos.
///
/// Formerly a separate top-of-window narration strip; now it's coachmarks like everything else, so each step
/// dims the page and rings exactly what it's talking about. Opening and closing steps are whole-page (no
/// anchor) beats; the middle steps spotlight the detail panel for repos, stacks, and the workspace.
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
            "Two repos, one stack wiring them together, and a workspace running from it. Everything you're about to see is real — the same engine your own repos will use.")
        { Prepare = () => { nav.GoHome(); return Task.CompletedTask; } },

        new(Anchors.RepoDetail,
            "A repo only declares what it needs",
            "sample-api asks for a port and a database port. It never says which numbers — that isn't its decision. A repo is a pure consumer, which is why it stays portable.")
        { Side = CoachSide.Left, Prepare = () => { nav.ShowFirstRepo(); return Task.CompletedTask; } },

        new(Anchors.StackDetail,
            "The stack owns the ports and supplies every value",
            "Three named ports, and each repo's inputs bound to them. Look at sample-web's apiUrl: it's built from api_port — the same port sample-api runs on. One value, two consumers, and neither repo knows about the other.")
        { Side = CoachSide.Left, Prepare = () => { nav.ShowFirstStack(); return Task.CompletedTask; } },

        new(Anchors.WorkspaceDetail,
            "Real numbers, written into real files",
            "Each repo got its own worktree on a sprig/ branch, and the ports were allocated for this workspace alone. Those resolved values are already in each worktree's .env and in a generated compose file — open a worktree to see them.")
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
