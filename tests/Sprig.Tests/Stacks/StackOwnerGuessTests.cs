using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class StackOwnerGuessTests
{
    static IReadOnlyDictionary<string, string> None => new Dictionary<string, string>();

    [Fact]
    public void Matches_a_repo_named_as_the_ports_leading_token()
    {
        var g = StackOwnerGuess.Guess(["api", "web"], ["api_port"], None);
        Assert.Equal("api", g["api_port"]);
    }

    [Theory]
    [InlineData("api_port")]
    [InlineData("api-port")]
    [InlineData("apiPort")]
    [InlineData("api")]
    public void Tokenises_common_port_name_shapes(string port)
    {
        var g = StackOwnerGuess.Guess(["api"], [port], None);
        Assert.Equal("api", g[port]);
    }

    [Fact]
    public void Prefers_the_longer_more_specific_repo_name()
    {
        // Both "auth" and "authservice" could match authservice_port; the longer, more specific one wins.
        var g = StackOwnerGuess.Guess(["auth", "authservice"], ["authservice_port"], None);
        Assert.Equal("authservice", g["authservice_port"]);
    }

    [Fact]
    public void Leaves_ambiguous_ports_unproposed()
    {
        // web and api both appear as equal-length tokens — too ambiguous to guess.
        var g = StackOwnerGuess.Guess(["web", "api"], ["web_api_port"], None);
        Assert.False(g.ContainsKey("web_api_port"));
    }

    [Fact]
    public void Never_overrides_an_existing_owner()
    {
        var existing = new Dictionary<string, string> { ["api_port"] = "web" };
        var g = StackOwnerGuess.Guess(["api", "web"], ["api_port"], existing);
        Assert.False(g.ContainsKey("api_port")); // already owned → left alone
    }

    [Fact]
    public void Proposes_nothing_when_no_name_matches()
    {
        var g = StackOwnerGuess.Guess(["api", "web"], ["db_port"], None);
        Assert.Empty(g);
    }

    [Fact]
    public void Fills_several_blanks_in_one_pass()
    {
        var g = StackOwnerGuess.Guess(
            ["api", "web", "postgres"],
            ["api_port", "postgres_port", "mystery_port"],
            None);

        Assert.Equal("api", g["api_port"]);
        Assert.Equal("postgres", g["postgres_port"]);
        Assert.False(g.ContainsKey("mystery_port"));
    }
}
