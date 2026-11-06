using Sprig;
using Sprig.Model;

namespace Sprig.Tests.Sdk;

public class ExprTests
{
    static RepoModel Describe(Action<RepoBuilder> body)
    {
        var repo = Repo.Describe("r");
        body(repo);
        return repo.BuildRepo();
    }

    [Fact]
    public void Interpolated_refs_stay_placeholders_and_literal_braces_are_escaped()
    {
        var model = Describe(repo =>
        {
            var pg = repo.Container("pg", "postgres:17").Endpoint("tcp", 5432);
            repo.DotNet("api", "src/Api").Http()
                .Env("Db", $"Host=localhost;Port={pg.Port}")
                .Env("Json", "{\"a\":1}");
        });

        var env = model.Apps[0].Env;
        Assert.Equal("Host=localhost;Port={pg.tcp.port}", env[0].Value);
        Assert.Equal("{{\"a\":1}}", env[1].Value);
    }

    [Fact]
    public void Settings_and_exports_carry_their_type_default_and_the_line_they_were_written_on()
    {
        var model = Describe(repo =>
        {
            var api = repo.DotNet("api", "src/Api").Http();
            var mail = repo.Mailpit("mail");
            repo.Setting<Url>("mail-smtp", mail.SmtpUrl);
            repo.Setting<Url>("catalog-url", "http://localhost:5100");
            repo.Setting<Secret>("payments-key");
            repo.Export<Url>("api-url", api.Url);
        });

        Assert.Equal(new SettingModel("mail-smtp", "url", model.Settings[0].Location, "smtp://localhost:{mail.smtp.port}"), model.Settings[0]);
        Assert.Equal("http://localhost:5100", model.Settings[1].Default);
        Assert.Equal(("secret", null), (model.Settings[2].Type, model.Settings[2].Default));
        Assert.True(model.Settings[0].Location.Line > 0);
        Assert.Equal("{api.http.url}", model.Exports[0].Value);
        Assert.True(model.Exports[0].Location.Line > 0);
        Assert.EndsWith("ExprTests.cs", model.Exports[0].Location.File);
    }

    [Fact]
    public void Numbers_in_a_template_are_written_invariantly()
    {
        var model = Describe(repo => repo.DotNet("api", ".").Http().Env("Ratio", $"{1.5}x"));

        Assert.Equal("1.5x", model.Apps[0].Env[0].Value);
    }

    [Fact]
    public void Using_a_port_before_declaring_an_endpoint_explains_what_to_do()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Describe(repo => _ = repo.DotNet("api", ".").Port));

        Assert.Contains("call .Http()", ex.Message);
    }
}
