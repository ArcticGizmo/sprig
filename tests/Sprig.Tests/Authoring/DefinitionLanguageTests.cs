using Sprig.Engine.Authoring;

namespace Sprig.Tests.Authoring;

public sealed class DefinitionLanguageTests : IDisposable
{
    readonly TempStore _store = new();
    readonly string _repo;
    readonly DefinitionLanguage _language;

    public DefinitionLanguageTests()
    {
        _repo = Path.Combine(_store.Root, "shop");
        Write("docker-compose.yml", "services:\n  postgres:\n    image: postgres:17\n  redis:\n    image: redis:7\n");
        Write("src/Catalog.Api/Catalog.Api.csproj", "<Project />");
        Write("src/Orders.Api/Orders.Api.csproj", "<Project />");
        Write("src/Catalog.Api/appsettings.json", """{ "Urls": "http://localhost:5000", "ConnectionStrings": { "Db": "x", "Cache": "y" } }""");
        Write("web/.env", "# local\nPORT=3000\nexport API_URL=http://localhost:5000\n");
        _language = new DefinitionLanguage(new DefinitionContext(_repo, () => ["shop-web", "shop-services"]));
    }

    public void Dispose() => _store.Dispose();

    void Write(string relative, string text)
    {
        var path = Path.Combine(_repo, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // A raw literal takes the source file's line endings, which a Windows checkout makes CRLF; the edits below match "\n".
    static readonly string Definition = """
        schema: 1
        repo: shop
        compose:
          docker-compose.yml:
            services:
              postgres:
                ports: { tcp: { container: 5432 } }
        containers:
          mail: { preset: mailpit }
        inputs:
          home-url: { type: url, default: http://localhost:8080 }
        apps:
          catalog-api:
            run: dotnet
            path: src/Catalog.Api
            ports: { http: any, admin: 5081 }
            values:
              swagger: ${catalog-api.url}/swagger
          web:
            run: node
            path: web
            ports: { http: any }

        """.ReplaceLineEndings("\n");

    /// <summary>Completions at the <c>|</c> in <paramref name="text"/>.</summary>
    string[] At(string text)
    {
        var caret = text.IndexOf('|');
        return _language.Complete(text.Remove(caret, 1), caret)?.Items.Select(i => i.Label).ToArray() ?? [];
    }

    [Fact]
    public void References_complete_names_then_what_each_one_offers()
    {
        Assert.Equal(["postgres", "mail", "catalog-api", "web", "inputs", "workspace"], At(Definition + "files:\n  x.env:\n    values:\n      A: ${|"));
        Assert.Equal(["port", "url", "http", "admin", "swagger"], At(Definition + "files:\n  x.env:\n    values:\n      A: ${catalog-api.|"));
        Assert.Equal(["port", "url"], At(Definition + "files:\n  x.env:\n    values:\n      A: http://x/${catalog-api.http.|"));
        // A preset's first port has no scheme, so no .url; its value comes after its ports.
        Assert.Equal(["port", "smtp", "ui", "smtp-url"], At(Definition + "files:\n  x.env:\n    values:\n      A: ${mail.|"));
        Assert.Equal(["home-url"], At(Definition + "files:\n  x.env:\n    values:\n      A: ${inputs.|"));
    }

    [Fact]
    public void Keys_come_from_where_the_caret_is()
    {
        Assert.Contains("ready-log", At(Definition.Replace("    path: web\n", "    path: web\n    re|\n")));
        Assert.Contains("read-by", At(Definition + "files:\n  web/.env:\n    |"));
        Assert.Contains("container", At(Definition.Replace("{ tcp: { container: 5432 } }", "{ tcp: { |")));
        Assert.Contains("outputs", At(Definition + "o|"));
    }

    [Fact]
    public void A_file_with_CRLF_line_endings_completes_as_one_with_LF()
    {
        var crlf = Definition.ReplaceLineEndings("\r\n");
        Assert.Contains("ready-log", At(crlf.Replace("    path: web\r\n", "    path: web\r\n    re|\r\n")));
        Assert.Equal(["dotnet", "node", "command"], At(crlf.Replace("run: node", "run: |")));
        Assert.Equal(["port", "url", "http", "admin", "swagger"], At(crlf + "files:\r\n  x.env:\r\n    values:\r\n      A: ${catalog-api.|"));
    }

    [Fact]
    public void Values_offer_their_choices_and_what_the_repo_has()
    {
        Assert.Equal(["dotnet", "node", "command"], At(Definition.Replace("run: node", "run: |")));
        Assert.Equal(["src/Catalog.Api", "src/Orders.Api"], At(Definition.Replace("path: src/Catalog.Api", "path: src/|")));
        Assert.Equal(["postgres", "mail", "web"], At(Definition.Replace("    ports: { http: any, admin: 5081 }", "    wait-for: [|")));
        Assert.Equal(["mailpit", "postgres", "rabbitmq", "redis"], At(Definition.Replace("preset: mailpit", "preset: |")).Order());
    }

    [Fact]
    public void Compose_files_their_services_and_a_files_keys_come_from_the_repo()
    {
        Assert.Contains("docker-compose.yml", At("schema: 1\nrepo: shop\ncompose:\n  |"));
        Assert.Equal(["redis"], At(Definition.Replace("      postgres:\n", "      postgres:\n      |\n")));
        Assert.Equal(["PORT", "API_URL"], At(Definition + "files:\n  web/.env:\n    read-by: [web]\n    values:\n      |"));
        Assert.Equal(["Db", "Cache"], At(Definition + "files:\n  src/Catalog.Api/appsettings.json:\n    values:\n      ConnectionStrings:\n        |"));
        var files = At(Definition + "files:\n  |");
        Assert.Contains("web/.env", files);
        Assert.Contains("src/Catalog.Api/appsettings.Local.json", files);
        Assert.Contains("web/.env.local", files);
    }

    [Fact]
    public void An_orchestration_offers_the_repos_list_and_its_repos_outputs()
    {
        Write("shop-web/sprig.yaml", "schema: 1\nrepo: shop-web\napps:\n  web: { run: node, ports: { http: any } }\noutputs:\n  web-url:\n    type: url\n    value: ${web.url}\n");
        const string orchestration = "schema: 1\norchestration: store\nrepos:\n  web: { path: shop-web }\n  services: { use: shop-services }\n";

        Assert.Equal(["shop-web", "shop-services"], At("schema: 1\norchestration: store\nrepos:\n  web: { use: | }\n"));
        Assert.Equal(["web-url"], At(orchestration + "inputs:\n  a: ${web.outputs.|"));
        Assert.Equal(["web", "services", "workspace"], At(orchestration + "inputs:\n  a: ${|"));
    }

    [Fact]
    public void Problems_come_from_the_reader_on_their_lines()
    {
        var text = Definition.Replace("run: node", "run: nodejs");

        var problem = Assert.Single(_language.Diagnose(text, "sprig.yaml"));

        Assert.Equal("Error", problem.Severity);
        Assert.Equal("run: nodejs", text[problem.Start..problem.End].Trim());
    }

    [Fact]
    public void Hover_says_what_a_reference_is_and_what_a_key_means()
    {
        var text = Definition + "files:\n  web/.env:\n    read-by: [web]\n    values:\n      PORT: ${web.port}\n      HOME: ${inputs.home-url}\n";

        var port = _language.Hover(text, text.IndexOf("${web.port}", StringComparison.Ordinal) + 3)!;
        Assert.Equal("${web.port}", port.Title);
        Assert.Contains("sprig picks it", port.Docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("else its default http://localhost:8080", _language.Hover(text, text.IndexOf("${inputs.home-url}", StringComparison.Ordinal) + 4)!.Docs);
        Assert.Contains("apps that read the file", _language.Hover(text, text.IndexOf("read-by", StringComparison.Ordinal) + 1)!.Docs);
    }

    [Fact]
    public void Half_typed_text_still_completes_from_what_parses()
    {
        var broken = Definition + "files:\n  web/.env:\n    read-by: [web\n    values:\n      A: ${cat|";
        Assert.Equal(["catalog-api"], At(broken));
    }
}
