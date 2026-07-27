using System.Text.RegularExpressions;
using Sprig.App.Coach;
using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>
/// The guardrail that makes coachmarks maintainable: the anchors a script points at are checked against the
/// views that declare them.
///
/// Coachmarks are normally brittle because a step anchors to a control, and renaming or removing that control
/// breaks the step <i>silently</i> — no compile error, no test failure, just a callout pointing at nothing.
/// These tests remove the "silently". Scanning source rather than a live visual tree is deliberate: the
/// failure mode is someone editing XAML, and this catches it with no UI harness. The complementary runtime
/// check — that a declared anchor is actually realised and resolvable — is done by the headless renderer,
/// which fails when a spike anchor can't be resolved.
/// </summary>
public class CoachAnchorTests
{
    [Fact]
    public void Every_declared_chrome_anchor_exists_in_the_views()
    {
        var declared = AnchorIdsInViews();

        foreach (var anchor in Anchors.Chrome)
            Assert.True(declared.Contains(anchor),
                $"anchor '{anchor}' is in Anchors.Chrome but no view declares " +
                $"AutomationProperties.AutomationId=\"{anchor}\" — a coachmark for it would point at nothing");
    }

    [Fact]
    public void The_views_declare_no_anchor_that_is_not_in_the_vocabulary()
    {
        // Catches the other direction: a typo'd or orphaned id left behind in XAML after a script changed.
        foreach (var anchor in AnchorIdsInViews())
            Assert.True(Anchors.Chrome.Contains(anchor),
                $"view declares anchor '{anchor}', which is not listed in Anchors.Chrome — add it there or " +
                "remove the attribute, so the vocabulary stays the single source of truth");
    }

    [Fact]
    public void The_spike_script_only_points_at_known_anchors()
    {
        var chrome = AnchorIdsInViews();

        foreach (var mark in CoachSpikeScript.Marks(new Navigator()))
        {
            // Drawn anchors (canvas contents) aren't in XAML — they're resolved through IAnchorSource, and
            // their prefixes are owned by WiringCanvas.TryGetAnchor.
            var isDrawn = mark.Anchor.StartsWith("stack.port:") || mark.Anchor.StartsWith("stack.node:")
                       || mark.Anchor.StartsWith("stack.pin:") || mark.Anchor == Anchors.StackAutoWire;

            Assert.True(isDrawn || chrome.Contains(mark.Anchor),
                $"spike mark points at '{mark.Anchor}', which is neither declared in a view nor a drawn anchor");
        }
    }

    [Fact]
    public void Anchor_ids_are_unique_per_view_declaration()
    {
        // Resolution takes the first match in the visual tree, so a duplicated id would highlight whichever
        // happened to be found first — a confusing bug to chase.
        var all = AllAnchorMatches();
        var duplicated = all.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(duplicated.Count == 0,
            $"anchor id(s) declared more than once: {string.Join(", ", duplicated)}");
    }

    static HashSet<string> AnchorIdsInViews() => AllAnchorMatches().ToHashSet(StringComparer.Ordinal);

    static List<string> AllAnchorMatches()
    {
        var pattern = new Regex(@"AutomationProperties\.AutomationId\s*=\s*""([^""]+)""", RegexOptions.Compiled);

        return Directory
            .EnumerateFiles(AppRoot(), "*.axaml", SearchOption.AllDirectories)
            .SelectMany(file => pattern.Matches(File.ReadAllText(file)))
            .Select(m => m.Groups[1].Value)
            // Skip binding expressions (repo.row:<name> and the like): those are dynamic anchors built from
            // row data, validated by their Anchors.* helper, not literal vocabulary entries.
            .Where(id => !id.StartsWith('{'))
            .ToList();
    }

    /// <summary>Locate <c>src/Sprig.App</c> by walking up from the test binaries to the repo root.</summary>
    static string AppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "sprig.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);   // the tests only ever run from inside the repo
        var app = Path.Combine(dir!.FullName, "src", "Sprig.App");
        Assert.True(Directory.Exists(app), $"expected the app sources at {app}");
        return app;
    }
}
