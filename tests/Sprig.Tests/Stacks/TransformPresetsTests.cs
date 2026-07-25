using Sprig.Core.Stacks;

namespace Sprig.Tests.Stacks;

public class TransformPresetsTests
{
    [Fact]
    public void Generate_produces_the_expected_expression()
    {
        Assert.Equal("${sprig.ports.api_port}", TransformPresets.Generate(TransformPresets.Raw, "api_port"));
        Assert.Equal("http://localhost:${sprig.ports.api_port}", TransformPresets.Generate(TransformPresets.Url, "api_port"));
        Assert.Equal("https://localhost:${sprig.ports.api_port}", TransformPresets.Generate(TransformPresets.UrlHttps, "api_port"));
        Assert.Equal("localhost:${sprig.ports.api_port}", TransformPresets.Generate(TransformPresets.HostPort, "api_port"));
    }

    [Theory]
    [InlineData("${sprig.ports.api_port}", "raw", "api_port")]
    [InlineData("http://localhost:${sprig.ports.api_port}", "url", "api_port")]
    [InlineData("https://localhost:${sprig.ports.api_port}", "url-https", "api_port")]
    [InlineData("localhost:${sprig.ports.api_port}", "host-port", "api_port")]
    public void Recognize_round_trips_each_preset(string expr, string presetId, string port)
    {
        var (preset, recognisedPort) = TransformPresets.Recognize(expr);
        Assert.Equal(presetId, preset.Id);
        Assert.Equal(port, recognisedPort);
    }

    [Fact]
    public void Recognize_falls_back_to_custom_for_an_unknown_shape_but_keeps_the_single_port()
    {
        var (preset, port) = TransformPresets.Recognize("Host=localhost;Port=${sprig.ports.db};Db=x");
        Assert.Equal(TransformPresets.Custom, preset);
        Assert.Equal("db", port);
    }

    [Fact]
    public void Recognize_reports_no_port_for_literals_and_multi_port_expressions()
    {
        Assert.Equal((TransformPresets.Custom, (string?)null), TransformPresets.Recognize("http://localhost:4000"));
        Assert.Equal((TransformPresets.Custom, (string?)null), TransformPresets.Recognize(""));
        Assert.Equal((TransformPresets.Custom, (string?)null),
            TransformPresets.Recognize("${sprig.ports.a}:${sprig.ports.b}"));
    }

    [Fact]
    public void Generate_and_recognize_are_inverses_for_every_selectable_preset()
    {
        foreach (var preset in TransformPresets.All.Where(p => p != TransformPresets.Custom))
        {
            var expr = TransformPresets.Generate(preset, "some_port");
            var (round, port) = TransformPresets.Recognize(expr);
            Assert.Equal(preset, round);
            Assert.Equal("some_port", port);
        }
    }
}
