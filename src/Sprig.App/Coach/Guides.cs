using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Sprig.App.ViewModels;
using Sprig.Core.Demo;

namespace Sprig.App.Coach;

/// <summary>
/// One guide: a small, self-contained lesson that teaches a single concept by hand-holding the user through
/// doing it, in the throwaway demo sandbox where nothing is at stake.
///
/// A guide names the sandbox <see cref="Stage"/> it starts from — the state just <i>before</i> the thing it
/// teaches, so the user performs that step themselves — and builds its steps from the live navigator and
/// services, so each step's wait predicate and "show me" act on the real sample.
/// </summary>
/// <param name="Id">Stable id, used to record completion in settings.</param>
/// <param name="Title">Short lesson name for the Learn list.</param>
/// <param name="Subtitle">One line on what it teaches.</param>
/// <param name="Duration">Rough time, e.g. "2 min".</param>
/// <param name="Stage">The sandbox state the guide starts from.</param>
/// <param name="Build">Builds the steps against the live navigator + services.</param>
public sealed record Guide(
    string Id,
    string Title,
    string Subtitle,
    string Duration,
    SampleStage Stage,
    System.Func<Navigator, AppServices, IReadOnlyList<CoachMark>> Build);

/// <summary>
/// The guide catalog — the ladder of lessons, each introducing one concept. Ordered easiest-first. Only the
/// first is authored so far (the vertical slice); the rest are placeholders in the plan, not here.
/// </summary>
public static class Guides
{
    public const string RegisterRepoId = "register-repo";

    public static IReadOnlyList<Guide> All { get; } =
    [
        new(RegisterRepoId,
            "Register your first repo",
            "Tell sprig about a repo, and see what it declares.",
            "2 min",
            SampleStage.RepoOnDisk,
            RegisterRepoSteps),
    ];

    /// <summary>Guide 1: point sprig at a repo on disk, then read what that repo declares.</summary>
    static IReadOnlyList<CoachMark> RegisterRepoSteps(Navigator nav, AppServices services)
    {
        var apiPath = Path.Combine(services.Sample.SampleReposDir, SampleFixtures.ApiRepo);
        bool ApiRegistered() => services.Repos.Get(SampleFixtures.ApiRepo) is not null;

        return
        [
            // Waiting: the user actually registers the repo. Prime the modal so their manual route is a
            // single Confirm, and hand them the same result via "Show me" if they get stuck.
            new(Anchors.ReposAdd,
                "Everything starts with a repo",
                "This is the Repos page — where you tell sprig about the code you want to isolate. Nothing's registered yet. Click Add repo (the sample-api folder is filled in for you) and confirm — or use Show me.")
            {
                Side = CoachSide.Below,
                Prepare = () => { nav.PrimeAddRepo(apiPath); return Task.CompletedTask; },
                Completed = ApiRegistered,
                ShowMe = () => nav.RegisterRepo(apiPath),
            },

            // Explanation: registration dropped us into the editor. Point at what the repo declares.
            new(Anchors.RepoInputs,
                "A repo declares what it needs",
                "sprig read this repo's committed .sprig.json. These are its inputs — values it needs but never decides for itself, like which port to run on. A stack supplies them, so the repo stays portable.")
            {
                Side = CoachSide.Right,
                Prepare = () => { nav.EditRepo(SampleFixtures.ApiRepo); return Task.CompletedTask; },
            },

            new(Anchors.RepoInputs,
                "That's a registered repo",
                "It declares what it consumes and nothing more — no ports, no URLs, no mention of the other repos. Deciding those values, and wiring repos together, is a stack's job. That's the next guide.")
            {
                Side = CoachSide.Right,
                Prepare = () => { nav.EditRepo(SampleFixtures.ApiRepo); return Task.CompletedTask; },
            },
        ];
    }
}
