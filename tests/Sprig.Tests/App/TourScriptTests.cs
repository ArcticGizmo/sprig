using Sprig.App.Coach;
using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>
/// Cover for the tour now that it's a coachmark script (converted from the old top-of-window strip so it dims
/// the page and rings each target). The behaviour that matters: the shape of the script, the Docker gate on
/// the infra step, and that the opening/closing beats are intentional whole-page steps rather than broken
/// anchors.
/// </summary>
public class TourScriptTests
{
    [Fact]
    public void Without_docker_the_tour_is_six_steps()
    {
        var marks = TourScript.Marks(new Navigator(), includeInfra: false);
        Assert.Equal(6, marks.Count);
    }

    [Fact]
    public void With_docker_an_infra_step_is_inserted_before_the_handoff()
    {
        var marks = TourScript.Marks(new Navigator(), includeInfra: true);

        Assert.Equal(7, marks.Count);
        // The infra step is the only one that performs an action, and it is never the finale.
        var performing = marks.Select((m, i) => (m, i)).Where(x => x.m.Perform is not null).Select(x => x.i).ToList();
        Assert.Equal([marks.Count - 2], performing);
        Assert.Equal(Anchors.WorkspaceDocker, marks[^2].Anchor);
    }

    [Fact]
    public void The_opening_and_closing_beats_are_whole_page_steps()
    {
        var marks = TourScript.Marks(new Navigator(), includeInfra: true);

        // A whole-page step has no anchor — it dims everything and centres the callout, on purpose.
        Assert.Null(marks[0].Anchor);
        Assert.Null(marks[^1].Anchor);
    }

    [Fact]
    public void The_repos_section_spotlights_the_row_then_its_detail()
    {
        var marks = TourScript.Marks(new Navigator(), includeInfra: false);

        // First the actual repo row (orientation), then the panel that shows what it declares.
        Assert.Equal(Anchors.RepoRow("sample-api"), marks[1].Anchor);
        Assert.Equal(Anchors.RepoDetail, marks[2].Anchor);
    }

    [Fact]
    public void The_middle_steps_spotlight_the_detail_panels_in_pipeline_order()
    {
        var marks = TourScript.Marks(new Navigator(), includeInfra: false);

        Assert.Equal(Anchors.RepoDetail, marks[2].Anchor);
        Assert.Equal(Anchors.StackDetail, marks[3].Anchor);
        Assert.Equal(Anchors.WorkspaceDetail, marks[4].Anchor);
    }

    [Fact]
    public void Every_step_has_complete_copy()
    {
        foreach (var m in TourScript.Marks(new Navigator(), includeInfra: true))
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Heading));
            Assert.False(string.IsNullOrWhiteSpace(m.Body));
        }
    }

    [Fact]
    public void No_step_narrates_chrome()
    {
        // The copy describes concepts and values, never where a control is — so moving one can't invalidate a
        // step. Same guardrail the old narration had.
        string[] banned = ["click the", "press the", "button on", "top right", "bottom left", "the third", "tab above"];

        foreach (var m in TourScript.Marks(new Navigator(), includeInfra: true))
        {
            var text = $"{m.Heading} {m.Body}".ToLowerInvariant();
            foreach (var phrase in banned)
                Assert.DoesNotContain(phrase, text);
        }
    }

    [Fact]
    public void Anchored_steps_point_only_at_known_anchors()
    {
        foreach (var m in TourScript.Marks(new Navigator(), includeInfra: true))
            if (m.Anchor is { } anchor)
            {
                // A dynamic row anchor (repo.row:<name>) is validated by its Anchors helper, not the static
                // chrome list; everything else must be a declared chrome anchor.
                var isRow = anchor.StartsWith("repo.row:");
                Assert.True(isRow || Anchors.Chrome.Contains(anchor),
                    $"tour step points at '{anchor}', which is neither a declared chrome anchor nor a repo row");
            }
    }
}
