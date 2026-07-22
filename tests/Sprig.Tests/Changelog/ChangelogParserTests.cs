using Sprig.Core.Changelog;

namespace Sprig.Tests.Changelog;

public class ChangelogParserTests
{
    const string Sample = """
        # Changelog

        ---

        ## [Unreleased]

        - a pending thing

        ---

        ## [v0.2.0] - 2026-07-22

        - second release feature
        - another one

        ---

        ## [v0.1.0] - 2026-07-01

        - first release

        ---
        """;

    [Fact]
    public void Parse_returns_sections_newest_first_with_versions()
    {
        var sections = ChangelogParser.Parse(Sample);

        Assert.Equal(3, sections.Count);
        Assert.Null(sections[0].Version);                 // Unreleased
        Assert.Equal("v0.2.0", sections[1].Display);
        Assert.Equal(new Version(0, 2, 0), sections[1].Version);
        Assert.Equal(new Version(0, 1, 0), sections[2].Version);
    }

    [Fact]
    public void Parse_trims_trailing_rule_and_blank_lines_from_block()
    {
        var sections = ChangelogParser.Parse(Sample);

        var v010 = sections[^1];
        Assert.Equal("## [v0.1.0] - 2026-07-01", v010.Block[0]);
        Assert.DoesNotContain("---", v010.Block);
        Assert.NotEqual("", v010.Block[^1].Trim()); // no trailing blank
    }

    [Fact]
    public void UnseenSince_returns_only_versions_between_last_seen_and_current()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, "0.1.0", "0.2.0");

        Assert.Single(unseen);
        Assert.Equal("v0.2.0", unseen[0].Display);
    }

    [Fact]
    public void UnseenSince_excludes_unreleased_and_versions_above_current()
    {
        // current is behind v0.2.0 → v0.2.0 is filtered out, Unreleased always excluded
        var unseen = ChangelogParser.UnseenSince(Sample, "0.1.0", "0.1.5");

        Assert.Empty(unseen);
    }

    [Fact]
    public void UnseenSince_is_empty_on_fresh_install_with_no_last_seen()
    {
        Assert.Empty(ChangelogParser.UnseenSince(Sample, null, "0.2.0"));
        Assert.Empty(ChangelogParser.UnseenSince(Sample, "", "0.2.0"));
    }

    [Fact]
    public void UnseenSince_is_empty_when_up_to_date()
    {
        Assert.Empty(ChangelogParser.UnseenSince(Sample, "0.2.0", "0.2.0"));
    }
}
