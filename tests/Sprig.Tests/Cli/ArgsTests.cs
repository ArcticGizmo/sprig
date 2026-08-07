using Sprig.Cli;

namespace Sprig.Tests.Cli;

/// <summary>Unit tests for the CLI's small arg helper — the parsing seams the review hardened.</summary>
public class ArgsTests
{
    [Fact]
    public void ExpandEquals_splits_double_dash_flag_value_on_the_first_equals()
    {
        // The value keeps any later '=' (e.g. a binding expression) — only the first splits.
        var r = Args.ExpandEquals(["--start=8000", "pos", "--bind=a:b=c"]);
        Assert.Equal(["--start", "8000", "pos", "--bind", "a:b=c"], r);
    }

    [Fact]
    public void ExpandEquals_leaves_plain_tokens_and_bare_terminator_untouched()
        => Assert.Equal(["value=x", "--", "-"], Args.ExpandEquals(["value=x", "--", "-"]));

    [Fact]
    public void TakeFirstPositional_removes_only_the_first_positional_by_index()
    {
        // Regression: the old `args.Where(a => a != sub)` stripped EVERY copy, so a repo/stack whose
        // name matched its subcommand vanished. Index removal keeps the second "add".
        var (sub, rest) = Args.TakeFirstPositional(["add", "add"]);
        Assert.Equal("add", sub);
        Assert.Equal(["add"], rest);
    }

    [Fact]
    public void TakeFirstPositional_returns_the_leading_subcommand_and_keeps_the_options()
    {
        // How the repo/stack/settings dispatchers use it: the subcommand leads, options follow.
        var (sub, rest) = Args.TakeFirstPositional(["create", "--repos", "a,b"]);
        Assert.Equal("create", sub);
        Assert.Equal(["--repos", "a,b"], rest);
    }

    [Fact]
    public void TakeOption_reads_a_value_then_removes_the_pair()
    {
        string[] args = ["--name", "web", "path"];
        Assert.Equal("web", Args.TakeOption(ref args, "--name"));
        Assert.Equal(["path"], args);
    }

    [Fact]
    public void TakeOption_ignores_a_flag_after_the_terminator()
    {
        string[] args = ["--", "--stack", "x"];
        Assert.Null(Args.TakeOption(ref args, "--stack"));
    }

    [Fact]
    public void TakeFlag_ignores_a_flag_after_the_terminator()
    {
        string[] args = ["--", "--force"];
        Assert.False(Args.TakeFlag(ref args, "--force"));
    }

    [Fact]
    public void FirstPositional_returns_a_dash_prefixed_value_after_the_terminator()
        => Assert.Equal("-weird", Args.FirstPositional(["--", "-weird"]));

    [Fact]
    public void RejectUnknown_throws_on_a_stray_flag()
        => Assert.Throws<ArgumentException>(() => Args.RejectUnknown(["pos", "--bogus"], "cmd"));

    [Fact]
    public void RejectUnknown_allows_positionals_and_tokens_after_the_terminator()
        => Args.RejectUnknown(["pos", "--", "-weird"], "cmd"); // must not throw
}
