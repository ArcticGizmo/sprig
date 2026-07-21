using Sprig.Core.Compose;

namespace Sprig.Tests.Compose;

public class ComposeScannerTests
{
    private const string Sample =
        "name: myapp\n" +
        "\n" +
        "services:\n" +
        "  db:\n" +
        "    image: postgres:16\n" +
        "    container_name: myapp-db\n" +
        "    ports:\n" +
        "      - \"5432:5432\"\n" +
        "    volumes:\n" +
        "      - dbdata:/var/lib/postgresql/data\n" +
        "      - ./local:/seed\n" +
        "\n" +
        "  web:\n" +
        "    container_name: myapp-web\n" +
        "    ports:\n" +
        "      - \"3000:3000\"\n" +
        "\n" +
        "volumes:\n" +
        "  dbdata:\n";

    /// <summary>Every token's recorded span must slice back to its own text in the raw file.</summary>
    private static void AssertSpansAreConsistent(ComposeOutline outline)
    {
        foreach (var line in outline.Lines)
            foreach (var tok in line.Tokens)
                Assert.Equal(tok.Text, line.Text.Substring(tok.StartColumn, tok.Length));
    }

    private static ComposeToken ByPath(ComposeOutline outline, params string[] path)
        => outline.Tokens.Single(t => t.Path.SequenceEqual(path));

    [Fact]
    public void Tokenizes_every_scalar_value_with_a_path()
    {
        var outline = ComposeScanner.Scan(Sample);

        Assert.True(outline.Parsed);
        AssertSpansAreConsistent(outline);

        // A plain value (image) is templatable too, not just the recognised kinds.
        var image = ByPath(outline, "services", "db", "image");
        Assert.Equal(ComposeTokenKind.Value, image.Kind);
        Assert.Equal("postgres:16", image.Text);

        // Keys are never tokens; the top-level name is deliberately excluded.
        Assert.DoesNotContain(outline.Tokens, t => t.Path.SequenceEqual(new[] { "name" }));
        Assert.DoesNotContain(outline.Tokens, t => t.Text == "services" || t.Text == "db");
    }

    [Fact]
    public void Recognises_container_ports_and_named_volumes_for_smart_defaults()
    {
        var outline = ComposeScanner.Scan(Sample);

        var cn = ByPath(outline, "services", "db", "container_name");
        Assert.Equal(ComposeTokenKind.ContainerName, cn.Kind);
        Assert.Equal("db", cn.Service);
        Assert.Equal("myapp-db", cn.Text);

        var port = ByPath(outline, "services", "db", "ports", "0");
        Assert.Equal(ComposeTokenKind.PublishedPort, port.Kind);
        Assert.Equal(5432, port.TargetPort);
        Assert.Equal("\"5432:5432\"", port.Text);

        // Whole volume entry is the token (so replacing it sets the whole scalar); source is a hint.
        var vol = ByPath(outline, "services", "db", "volumes", "0");
        Assert.Equal(ComposeTokenKind.NamedVolume, vol.Kind);
        Assert.Equal("dbdata", vol.VolumeName);
        Assert.Equal("dbdata:/var/lib/postgresql/data", vol.Text);

        // The bind mount ./local is a plain templatable value, not a named volume.
        Assert.Equal(ComposeTokenKind.Value, ByPath(outline, "services", "db", "volumes", "1").Kind);
    }

    [Fact]
    public void Long_form_port_values_are_tokens_addressed_by_path()
    {
        const string compose =
            "services:\n" +
            "  api:\n" +
            "    ports:\n" +
            "      - target: 8080\n" +
            "        published: 9000\n";

        var outline = ComposeScanner.Scan(compose);
        AssertSpansAreConsistent(outline);

        var published = ByPath(outline, "services", "api", "ports", "0", "published");
        Assert.Equal("9000", published.Text);
    }

    [Fact]
    public void Unparseable_file_is_reported_with_lines_but_no_tokens()
    {
        var outline = ComposeScanner.Scan("services:\n  db:\n   - bad: : :\n\tmixed tabs\n");

        Assert.False(outline.Parsed);
        Assert.NotNull(outline.Error);
        Assert.Empty(outline.Tokens);
        Assert.NotEmpty(outline.Lines);
    }

    [Fact]
    public void Preserves_lines_and_handles_crlf()
    {
        var outline = ComposeScanner.Scan("services:\r\n  db:\r\n    container_name: app-db\r\n");
        AssertSpansAreConsistent(outline);

        var cn = ByPath(outline, "services", "db", "container_name");
        Assert.Equal("app-db", cn.Text);
    }
}
