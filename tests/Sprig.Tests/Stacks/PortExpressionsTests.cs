using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class PortExpressionsTests
{
    [Theory]
    [InlineData("${sprig.workspace}", true)]
    [InlineData("svc-${sprig.workspace}-x", true)]
    [InlineData("${sprig.ports.api}", false)]
    [InlineData("production", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ReferencesWorkspace_detects_the_workspace_token(string? expr, bool expected) =>
        Assert.Equal(expected, PortExpressions.ReferencesWorkspace(expr));

    [Theory]
    [InlineData("${sprig.ports.api}", true)]        // a bare port pass-through
    [InlineData("  ${sprig.ports.api}  ", true)]    // whitespace is trimmed
    [InlineData("${sprig.workspace}", true)]        // a bare workspace pass-through
    [InlineData("http://localhost:${sprig.ports.api}", false)] // wrapped → needs a transform
    [InlineData("${sprig.ports.a}:${sprig.ports.b}", false)]   // combined → needs a transform
    [InlineData("${sprig.ports.api}-${sprig.workspace}", false)] // mixed sources
    [InlineData("production", false)]               // a literal is not a source reference
    [InlineData("", false)]
    public void IsBareSourceReference_is_true_only_for_a_single_unwrapped_token(string expr, bool expected) =>
        Assert.Equal(expected, PortExpressions.IsBareSourceReference(expr));
}
