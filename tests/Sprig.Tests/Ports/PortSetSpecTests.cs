using Sprig.Core.Ports;

namespace Sprig.Tests.Ports;

/// <summary>Covers the compact port-set spec parser + renderer.</summary>
public class PortSetSpecTests
{
    [Theory]
    [InlineData("8100", new[] { 8100 })]
    [InlineData("8100,8101,8200", new[] { 8100, 8101, 8200 })]
    [InlineData("8100-8103", new[] { 8100, 8101, 8102, 8103 })]
    [InlineData("8100-8103,8200", new[] { 8100, 8101, 8102, 8103, 8200 })]
    [InlineData(" 8100 - 8102 , 8200 ", new[] { 8100, 8101, 8102, 8200 })]
    [InlineData("8101,8100,8100", new[] { 8100, 8101 })] // deduped + sorted
    public void Parses_valid_specs(string spec, int[] expected)
        => Assert.Equal(expected.OrderBy(x => x), PortSetSpec.Parse(spec).OrderBy(x => x));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]        // below MinPort
    [InlineData("70000")]    // above MaxPort
    [InlineData("8103-8100")] // range reversed
    [InlineData("8100-")]     // missing end
    [InlineData("-8100")]     // missing start / signed
    public void Rejects_invalid_specs(string spec)
        => Assert.Throws<FormatException>(() => PortSetSpec.Parse(spec));

    [Theory]
    [InlineData(new[] { 8100, 8101, 8102, 8103 }, "8100-8103")]
    [InlineData(new[] { 8100, 8200 }, "8100,8200")]
    [InlineData(new[] { 8100, 8101, 8103 }, "8100-8101,8103")]
    [InlineData(new[] { 8100 }, "8100")]
    public void Describe_collapses_runs_into_ranges(int[] ports, string expected)
        => Assert.Equal(expected, PortSetSpec.Describe(new HashSet<int>(ports)));
}
