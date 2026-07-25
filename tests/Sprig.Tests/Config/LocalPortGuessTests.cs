using Sprig.Core.Config;

namespace Sprig.Tests.Config;

public class LocalPortGuessTests
{
    static readonly string[] OnePort = ["dbPort"];

    [Theory]
    [InlineData("http://localhost:5000", "http://localhost:${sprig.dbPort}")]
    [InlineData("https://127.0.0.1:8080/api", "https://127.0.0.1:${sprig.dbPort}/api")]
    [InlineData("host.docker.internal:5432", "host.docker.internal:${sprig.dbPort}")]
    [InlineData("Host=localhost;Port=5432;Database=x", "Host=localhost;Port=${sprig.dbPort};Database=x")]
    [InlineData("Server=127.0.0.1;Port=3306;Uid=root", "Server=127.0.0.1;Port=${sprig.dbPort};Uid=root")]
    public void Rewrites_a_local_port_to_the_input_token(string value, string expected)
    {
        Assert.Equal(expected, LocalPortGuess.Rewrite(value, OnePort));
    }

    [Theory]
    [InlineData("http://api.example.com:5000")]        // external host — leave alone
    [InlineData("postgres://db.internal:5432/app")]    // not a local host
    [InlineData("just some text")]                     // no port at all
    [InlineData("")]
    public void Leaves_non_local_or_portless_values_alone(string value)
    {
        Assert.Null(LocalPortGuess.Rewrite(value, OnePort));
    }

    [Fact]
    public void Prefers_a_port_named_input_when_several_inputs_exist()
    {
        var result = LocalPortGuess.Rewrite("http://localhost:5000", ["apiUrl", "dbPort", "frontend"]);
        Assert.Equal("http://localhost:${sprig.dbPort}", result);
    }

    [Fact]
    public void Uses_the_sole_input_when_there_is_exactly_one()
    {
        var result = LocalPortGuess.Rewrite("http://localhost:5000", ["frontend"]);
        Assert.Equal("http://localhost:${sprig.frontend}", result);
    }

    [Fact]
    public void Declines_to_guess_when_the_input_is_ambiguous()
    {
        // Several inputs, none port-named → no safe choice, so no suggestion.
        Assert.Null(LocalPortGuess.Rewrite("http://localhost:5000", ["apiUrl", "frontend"]));
    }

    [Fact]
    public void Ignores_the_workspace_variable_when_choosing_an_input()
    {
        var result = LocalPortGuess.Rewrite("http://localhost:5000", ["workspace", "frontend"]);
        Assert.Equal("http://localhost:${sprig.frontend}", result);
    }
}
