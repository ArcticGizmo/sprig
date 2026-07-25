using Sprig.Core.Env;

namespace Sprig.Tests.Env;

public class EnvKeyReaderTests
{
    [Fact]
    public void Keys_reads_identifiers_before_equals_and_skips_the_rest()
    {
        const string content =
            "# a comment\n" +
            "PORT=3000\n" +
            "export DATABASE_URL=postgres://localhost\n" +
            "  INDENTED=1\n" +
            "NOT-AN-IDENT=x\n" +   // '-' isn't an identifier char → not a key line
            "=noname\n" +          // no name before '='
            "JUST_A_WORD\n" +      // no '=' → not a declaration
            "PORT=4000\n";         // duplicate → first-seen wins

        var keys = EnvKeyReader.Keys(content);

        Assert.Equal(new[] { "PORT", "DATABASE_URL", "INDENTED" }, keys);
    }

    [Fact]
    public void Keys_is_empty_for_blank_content()
        => Assert.Empty(EnvKeyReader.Keys(""));

    [Fact]
    public void KeysForFile_unions_the_file_with_its_template_companions()
    {
        using var s = new TempStore();
        Directory.CreateDirectory(s.Root);
        File.WriteAllText(Path.Combine(s.Root, ".env.local"), "PORT=1\nSECRET=shh\n");
        // the committed template outlines a variable the gitignored file doesn't currently set
        File.WriteAllText(Path.Combine(s.Root, ".env.template"), "PORT=\nAPI_KEY=\n");

        var keys = EnvKeyReader.KeysForFile(s.Root, ".env.local");

        Assert.Equal(new[] { "PORT", "SECRET", "API_KEY" }, keys); // file first, template adds API_KEY, PORT deduped
    }

    [Fact]
    public void KeysForFile_reads_the_template_even_when_the_env_file_is_absent()
    {
        using var s = new TempStore();
        Directory.CreateDirectory(s.Root);
        File.WriteAllText(Path.Combine(s.Root, ".env.example"), "HOST=\nTOKEN=\n");

        // .env doesn't exist on disk (gitignored), but .env.example lists the available vars
        Assert.Equal(new[] { "HOST", "TOKEN" }, EnvKeyReader.KeysForFile(s.Root, ".env"));
    }

    [Fact]
    public void KeysForFile_is_empty_when_nothing_matches()
    {
        using var s = new TempStore();
        Directory.CreateDirectory(s.Root);
        Assert.Empty(EnvKeyReader.KeysForFile(s.Root, ".env"));
    }

    [Fact]
    public void Parse_captures_values_trimmed_and_unquoted_first_seen_wins()
    {
        const string content =
            "PORT=3000\n" +
            "export DATABASE_URL = postgres://localhost \n" +   // spaces around = and trailing space
            "QUOTED=\"a value\"\n" +
            "SINGLE='shh'\n" +
            "EMPTY=\n" +
            "PORT=4000\n";                                       // duplicate → first value wins

        var pairs = EnvKeyReader.Parse(content);

        Assert.Equal(
            new[]
            {
                new KeyValuePair<string, string>("PORT", "3000"),
                new KeyValuePair<string, string>("DATABASE_URL", "postgres://localhost"),
                new KeyValuePair<string, string>("QUOTED", "a value"),
                new KeyValuePair<string, string>("SINGLE", "shh"),
                new KeyValuePair<string, string>("EMPTY", ""),
            },
            pairs);
    }

    [Fact]
    public void ExamplesForFile_gathers_values_from_file_and_templates_skipping_empties()
    {
        using var s = new TempStore();
        Directory.CreateDirectory(s.Root);
        File.WriteAllText(Path.Combine(s.Root, ".env.local"), "PORT=1234\nSECRET=\n");
        File.WriteAllText(Path.Combine(s.Root, ".env.example"), "PORT=8080\nAPI_KEY=your-key-here\n");

        var examples = EnvKeyReader.ExamplesForFile(s.Root, ".env.local");

        // PORT has an example from each source (file first), API_KEY only from the example file
        Assert.Equal(
            new[] { new EnvExample(".env.local", "1234"), new EnvExample(".env.example", "8080") },
            examples["PORT"]);
        Assert.Equal(new[] { new EnvExample(".env.example", "your-key-here") }, examples["API_KEY"]);
        // SECRET was declared but had no value → no example, so it isn't in the map
        Assert.False(examples.ContainsKey("SECRET"));
    }
}
