using Sprig.App.Controls;

namespace Sprig.Tests.App;

public class SprigTokenCompletionTests
{
    [Fact]
    public void Tokens_wraps_each_variable_name_and_skips_blanks()
    {
        var tokens = SprigTokenCompletion.Tokens(new[] { "workspace", "dbPort", "  " });

        Assert.Contains("${sprig.workspace}", tokens);
        Assert.Contains("${sprig.dbPort}", tokens);
        Assert.DoesNotContain(tokens, t => t.Contains("${sprig.}")); // blank names are skipped
    }

    [Theory]
    [InlineData("PORT is ${sprig.po", "${sprig.po")]
    [InlineData("${", "${")]
    [InlineData("http://localhost:${sprig.", "${sprig.")]
    public void TrailingFragment_returns_the_open_token(string text, string expected)
        => Assert.Equal(expected, SprigTokenCompletion.TrailingFragment(text));

    [Theory]
    [InlineData("plain text")]
    [InlineData("${sprig.dbPort}")] // already closed
    [InlineData("")]
    public void TrailingFragment_is_null_when_not_in_an_open_token(string text)
        => Assert.Null(SprigTokenCompletion.TrailingFragment(text));

    [Fact]
    public void Matches_filters_candidates_by_the_open_fragment()
    {
        Assert.True(SprigTokenCompletion.Matches("x ${sprig.wo", "${sprig.workspace}"));
        Assert.False(SprigTokenCompletion.Matches("x ${sprig.db", "${sprig.workspace}"));
        Assert.False(SprigTokenCompletion.Matches("plain", "${sprig.workspace}"));
    }

    [Theory]
    [InlineData("$", "$")]                       // a bare '$' is enough to start completing
    [InlineData("url=$", "$")]
    [InlineData("$vi", "$vi")]                    // '$' + text, no braces
    [InlineData("http://x:$ports.vi", "$ports.vi")]
    public void TrailingFragment_treats_a_bare_dollar_as_an_open_token(string text, string expected)
        => Assert.Equal(expected, SprigTokenCompletion.TrailingFragment(text));

    [Theory]
    [InlineData("price is $5.00 ")]              // trailing space → the '$5.00' run is closed off
    [InlineData("a $5:00")]                       // ':' isn't token-shaped
    public void TrailingFragment_ignores_a_non_token_dollar(string text)
        => Assert.Null(SprigTokenCompletion.TrailingFragment(text));

    [Fact]
    public void A_bare_dollar_offers_every_token()
    {
        Assert.True(SprigTokenCompletion.Matches("$", "${sprig.workspace}"));
        Assert.True(SprigTokenCompletion.Matches("url=$", "${sprig.ports.vite_url}"));
    }

    [Fact]
    public void Shorthand_matches_anything_to_the_right_of_a_dot()
    {
        // "$vite" should surface both the flat input and the dotted stack port.
        Assert.True(SprigTokenCompletion.Matches("$vite", "${sprig.vite}"));
        Assert.True(SprigTokenCompletion.Matches("$vite", "${sprig.ports.vite_url}"));

        // ...but not an unrelated token.
        Assert.False(SprigTokenCompletion.Matches("$vite", "${sprig.workspace}"));

        // a leading segment still matches (typing the namespace)
        Assert.True(SprigTokenCompletion.Matches("$ports", "${sprig.ports.vite_url}"));
        // and continuing through a dot keeps matching
        Assert.True(SprigTokenCompletion.Matches("$ports.vi", "${sprig.ports.vite_url}"));
        Assert.False(SprigTokenCompletion.Matches("$ports.vi", "${sprig.vite}"));
    }

    [Fact]
    public void Replace_splices_a_bare_dollar_shorthand()
    {
        // typing "$wo" then accepting ${sprig.workspace} replaces from the '$'
        var (result, caret) = SprigTokenCompletion.Replace("url=$wo", "url=$wo".Length, "${sprig.workspace}");
        Assert.Equal("url=${sprig.workspace}", result);
        Assert.Equal("url=${sprig.workspace}".Length, caret);
    }

    [Fact]
    public void Combine_splices_a_bare_dollar_shorthand()
        => Assert.Equal("PORT=${sprig.vite}", SprigTokenCompletion.Combine("PORT=$vi", "${sprig.vite}"));

    [Fact]
    public void Combine_splices_the_token_keeping_the_literal_prefix()
        => Assert.Equal("http://localhost:${sprig.dbPort}",
            SprigTokenCompletion.Combine("http://localhost:${sprig.db", "${sprig.dbPort}"));

    [Fact]
    public void Combine_is_a_no_op_without_an_open_fragment()
        => Assert.Equal("${sprig.workspace}",
            SprigTokenCompletion.Combine("${sprig.workspace}", "${sprig.dbPort}"));

    [Fact]
    public void UnknownReferences_flags_only_names_not_in_the_variable_list()
    {
        var vars = new[] { "workspace", "dbPort" };

        Assert.Empty(SprigTokenCompletion.UnknownReferences("${sprig.dbPort}:5432", vars));
        Assert.Empty(SprigTokenCompletion.UnknownReferences("name--${sprig.workspace}", vars));
        // whitespace inside the braces is trimmed, matching the validator
        Assert.Empty(SprigTokenCompletion.UnknownReferences("${sprig. dbPort }", vars));

        // an undeclared name is flagged; stack-level ports.* is NOT valid in a repo config
        Assert.Equal(new[] { "apiPort" }, SprigTokenCompletion.UnknownReferences("${sprig.apiPort}", vars));
        Assert.Equal(new[] { "ports.db" }, SprigTokenCompletion.UnknownReferences("${sprig.ports.db}:5432", vars));

        // a still-open (unclosed) token isn't a reference yet, so it's never flagged
        Assert.Empty(SprigTokenCompletion.UnknownReferences("${sprig.dbP", vars));
        // non-sprig ${...} passes through untouched
        Assert.Empty(SprigTokenCompletion.UnknownReferences("${OTHER}", vars));
    }

    [Fact]
    public void Replace_swaps_the_whole_token_when_the_caret_is_mid_token()
    {
        // caret inside an existing ${sprig.dbPort}; picking ${sprig.workspace} must replace the whole
        // token, not splice at the caret and leave the tail behind.
        const string text = "Host=localhost;Port=${sprig.dbPort};Database=x";
        var caret = "Host=localhost;Port=${sprig".Length; // right after "${sprig"

        var (result, newCaret) = SprigTokenCompletion.Replace(text, caret, "${sprig.workspace}");

        Assert.Equal("Host=localhost;Port=${sprig.workspace};Database=x", result);
        Assert.Equal("Host=localhost;Port=${sprig.workspace}".Length, newCaret);
    }

    [Fact]
    public void Replace_at_the_end_of_an_open_token_keeps_the_literal_prefix()
    {
        var (result, _) = SprigTokenCompletion.Replace("url=${sprig.wo", "url=${sprig.wo".Length, "${sprig.workspace}");
        Assert.Equal("url=${sprig.workspace}", result);
    }

    [Fact]
    public void Replace_is_a_no_op_outside_a_token()
        => Assert.Equal(("plain", 5), SprigTokenCompletion.Replace("plain", 5, "${sprig.workspace}"));

    [Fact]
    public void IsValid_is_the_inverse_of_having_unknown_references()
    {
        var vars = new[] { "workspace" };
        Assert.True(SprigTokenCompletion.IsValid("${sprig.workspace}", vars));
        Assert.False(SprigTokenCompletion.IsValid("${sprig.nope}", vars));
    }
}
