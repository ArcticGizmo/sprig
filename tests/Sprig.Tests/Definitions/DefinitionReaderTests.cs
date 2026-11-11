using Sprig.Engine.Definitions;
using Sprig.Model;

namespace Sprig.Tests.Definitions;

public class DefinitionReaderTests
{
    static ReadResult Read(string yaml) => DefinitionReader.Read(yaml, "sprig.yaml");

    static RepoModel Repo(string yaml)
    {
        var result = Read(yaml);
        Assert.True(result.Ok, string.Join("\n", result.Problems.Select(p => $"{p.Location?.Line}: {p.Message}")));
        return Assert.IsType<RepoModel>(result.Model);
    }

    static (int Line, string Message) Problem(string yaml)
    {
        var result = Read(yaml);
        Assert.False(result.Ok);
        var problem = Assert.Single(result.Problems);
        return (problem.Location!.Line, problem.Message);
    }

    [Fact]
    public void References_become_the_planners_templates()
    {
        var repo = Repo("""
            schema: 1
            repo: shop
            containers:
              db: { preset: postgres }
              mail: { preset: mailpit }
            inputs:
              home-url: { type: url, default: http://localhost:8080 }
            apps:
              api:
                run: dotnet
                path: src/Api
                ports: { http: any, admin: { host: 5081, scheme: https } }
                values:
                  swagger: ${api.url}/swagger
            files:
              src/Api/.env:
                read-by: [api]
                values:
                  PORT: ${api.port}
                  ADMIN: ${api.admin.url}
                  DB: Port=${db.port};User=${db.user}
                  SMTP: ${mail.smtp-url}
                  HOME: ${inputs.home-url}/
                  DOCS: ${api.swagger}
                  NAME: shop-${workspace.name}
                  LITERAL: '$${not-a-reference} {braces}'
            """);

        var env = repo.Apps.Single().Env.ToDictionary(e => e.Key, e => e.Value);
        Assert.Equal("{api.http.port}", env["PORT"]);
        Assert.Equal("{api.admin.url}", env["ADMIN"]);
        Assert.Equal("Port={db.tcp.port};User=sprig", env["DB"]);
        Assert.Equal("smtp://localhost:{mail.smtp.port}", env["SMTP"]);
        Assert.Equal("{setting.home-url}/", env["HOME"]);
        Assert.Equal("{api.http.url}/swagger", env["DOCS"]);
        Assert.Equal("shop-{workspace.name}", env["NAME"]);
        Assert.Equal("${{not-a-reference}} {{braces}}", env["LITERAL"]);
        Assert.Equal(".env", repo.Apps.Single().Env[0].File);
    }

    [Fact]
    public void Ports_take_any_a_number_or_a_list_of_numbers_and_ranges()
    {
        var app = Repo("""
            schema: 1
            repo: shop
            apps:
              web:
                run: node
                ports:
                  http: [8000, 8110-8115, 9000]
                  admin: 5080
                  debug: any
            """).Apps.Single();

        Assert.Equal(
            [("http", "8000,8110-8115,9000", "http"), ("admin", "5080", null), ("debug", null, null)],
            app.Endpoints.Select(e => (e.Name, e.Pin, e.Scheme)).ToArray());
    }

    [Fact]
    public void A_shared_file_is_written_for_each_app_that_reads_it_relative_to_that_app()
    {
        var repo = Repo("""
            schema: 1
            repo: shop
            apps:
              a: { run: dotnet, path: backend/src/Services/A, ports: { http: any } }
              b: { run: dotnet, path: backend/src/Services/B, ports: { http: any } }
            files:
              backend/src/.env:
                read-by: [a, b]
                values:
                  A_Port: ${a.port}
                  B_Port: ${b.port}
            """);

        foreach (var app in repo.Apps)
        {
            Assert.All(app.Env, e => Assert.Equal(("../../.env", "dotenv"), (e.File, e.Kind)));
            Assert.Equal(["A_Port", "B_Port"], app.Env.Select(e => e.Key));
        }
    }

    [Fact]
    public void Json_values_are_written_nested_and_kept_as_sections()
    {
        var env = Repo("""
            schema: 1
            repo: shop
            apps:
              api: { run: dotnet, path: src/Api, ports: { http: any } }
            files:
              src/Api/appsettings.Local.json:
                read-by: [api]
                values:
                  Urls: ${api.url}
                  ConnectionStrings:
                    Db: Host=localhost
                    Cache:
                      Redis: localhost:6379
              src/Api/Properties/launchSettings.json:
                read-by: [api]
                values:
                  Logging__Level: Debug
            """).Apps.Single().Env;

        Assert.Equal(
            [("Urls", "appSettings", null), ("ConnectionStrings:Db", "appSettings", null), ("ConnectionStrings:Cache:Redis", "appSettings", null), ("Logging__Level", "launchSettings", "sprig")],
            env.Select(e => (e.Key, e.Kind!, e.Profile)).ToArray());
        Assert.Equal("Properties/launchSettings.json", env[^1].File);
    }

    [Theory]
    [InlineData("""
        schema: 1
        repo: shop
        apps:
          api: { run: dotnet, path: src/Api }
        files:
          src/Api/appsettings.json:
            read-by: [api]
            values:
              ConnectionStrings__Db: x
        """, 9, "write 'ConnectionStrings__Db' nested")]
    [InlineData("""
        schema: 1
        repo: shop
        apps:
          web: { run: node }
        files:
          .env.local:
            read-by: [web]
            values:
              Section:
                Key: x
        """, 9, "flat keys only")]
    [InlineData("""
        schema: 1
        repo: shop
        apps:
          web: { run: node }
        files:
          config/local.vars:
            read-by: [web]
            values: { A: b }
        """, 6, "say format: dotenv, json or launch-settings")]
    [InlineData("""
        schema: 1
        repo: shop
        apps:
          web:
            run: node
            ports: { http: any }
            command: npm run dev -- --port ${web.port}
        """, 7, "has a reference in it")]
    [InlineData("""
        schema: 1
        repo: shop
        apps:
          web:
            run: node
            prots: { http: any }
        """, 6, "has no 'prots'")]
    [InlineData("""
        schema: 1
        repo: shop
        apps:
          web: { run: node, ports: { http: any } }
        files:
          .env:
            read-by: [web]
            values:
              API: ${api.url}
        """, 9, "'api' isn't declared in this file; to use something from another repo, make it an input")]
    [InlineData("""
        schema: 1
        repo: shop
        containers:
          cache: { image: redis:7, ports: { tcp: { container: 6379 } } }
        apps:
          web: { run: node }
        files:
          .env:
            read-by: [web]
            values:
              CACHE: ${cache.url}
        """, 11, "has no url: give it a scheme")]
    [InlineData("""
        schema: 1
        repo: shop
        apps:
          web: { run: node, ports: { http: any } }
        files:
          .env:
            read-by: [web]
            values:
              URL: {web.url}
        """, 9, "A value starting with '{' needs quotes")]
    [InlineData("""
        schema: 1
        repo: shop
        apps:
          web:
            run: node
            values:
              a: ${web.b}
              b: ${web.a}
        """, 8, "refers back to itself")]
    [InlineData("""
        schema: 1
        repo: shop
        compose:
          docker-compose.yml:
            services:
              db:
                ports: { tcp: 5432 }
        """, 7, "needs the port inside the container")]
    [InlineData("""
        schema: 1
        repo: shop
        apps:
          web: { run: node, ports: { http: [8000, 90000] } }
        """, 4, "'90000' isn't a port")]
    [InlineData("""
        schema: 1
        repo: shop
        apps:
          migrate: { run: dotnet, once: true, restart: always }
        """, 4, "'restart' is for an app that keeps running")]
    public void A_mistake_is_reported_on_its_line(string yaml, int line, string message)
    {
        var (at, text) = Problem(yaml);
        Assert.Contains(message, text);
        Assert.Equal(line, at);
    }

    [Fact]
    public void A_reference_inside_braces_says_to_quote_it()
    {
        var (line, message) = Problem("""
            schema: 1
            repo: shop
            apps:
              web: { run: node, ports: { http: any } }
            outputs:
              web-url: { type: url, value: ${web.url} }
            """);

        Assert.Equal(6, line);
        Assert.Contains("A value with ${…} inside { } or [ ] needs quotes", message);
        Assert.True(Read("""
            schema: 1
            repo: shop
            apps:
              web: { run: node, ports: { http: any } }
            outputs:
              web-url: { type: url, value: '${web.url}' }
            """).Ok);
    }

    [Fact]
    public void A_file_without_a_schema_or_from_a_newer_sprig_is_refused()
    {
        Assert.Contains("has no schema", Problem("repo: shop").Message);
        var (_, newer) = Problem($"schema: {DefinitionReader.CurrentSchema + 1}\nrepo: shop");
        Assert.Contains("update sprig", newer);
    }

    [Fact]
    public void Values_are_read_as_written()
    {
        var env = Repo("""
            schema: 1
            repo: shop
            apps:
              web: { run: node }
            files:
              .env:
                read-by: [web]
                values:
                  ENABLED: on
                  VERSION: 1.10
                  OCTAL: 0123
                  EMPTY:
            """).Apps.Single().Env.ToDictionary(e => e.Key, e => e.Value);

        Assert.Equal(("on", "1.10", "0123", ""), (env["ENABLED"], env["VERSION"], env["OCTAL"], env["EMPTY"]));
    }

    [Fact]
    public void A_preset_is_filled_in_and_what_is_written_beside_it_wins()
    {
        var mail = Repo("""
            schema: 1
            repo: shop
            containers:
              mail:
                preset: mailpit
                image: axllent/mailpit:latest
                ports:
                  ui: 8025
                env: { MP_MAX_MESSAGES: '50' }
            """).Containers.Single();

        Assert.Equal("axllent/mailpit:latest", mail.Image);
        Assert.Equal([("smtp", 1025, null, null), ("ui", 8025, "8025", "http")], mail.Endpoints.Select(e => (e.Name, e.TargetPort, e.Pin, e.Scheme)));
        Assert.Equal("/mailpit readyz", mail.Health);
        Assert.Equal("mail", mail.Role);
        Assert.Equal(("MP_MAX_MESSAGES", "50"), (mail.Env.Single().Key, mail.Env.Single().Value));
    }

    [Fact]
    public void Jobs_apps_and_their_options_reach_the_model()
    {
        var apps = Repo("""
            schema: 1
            repo: shop
            compose:
              docker-compose.yml:
                services:
                  db: { ports: { tcp: { container: 5432 } }, health: pg_isready }
            apps:
              migrate: { run: dotnet, path: tools/Migrate, once: true, retry: 3, wait-for: [db] }
              api:
                run: dotnet
                path: src/Api
                ports: { http: any }
                health: /health
                ready-log: Now listening
                restart: on-failure
                wait-for: [db, migrate]
                links: { Swagger: /swagger, Home: / }
                role: api
              tool:
                run: command
                command: python serve.py {port}
            groups:
              api: [api]
            """).Apps.ToDictionary(a => a.Name);

        Assert.True(apps["migrate"].Job);
        Assert.Equal(("onFailure", 3), (apps["migrate"].Restart!.Policy, apps["migrate"].Restart!.Max));
        var api = apps["api"];
        Assert.Equal("/health", api.Endpoints.Single().Health);
        Assert.Equal(("onFailure", 5), (api.Restart!.Policy, api.Restart.Max));
        Assert.Equal(["db", "migrate"], api.WaitFor);
        Assert.Equal([("Swagger", "{api.http.url}/swagger"), ("Home", "{api.http.url}")], api.Links!.Select(l => (l.Label, l.Url)));
        Assert.Equal(("Now listening", "api"), (api.ReadyLog, api.Role));
        Assert.Equal(("app", "python serve.py {{port}}"), (apps["tool"].Kind, apps["tool"].Run));
    }

    [Fact]
    public void An_orchestration_names_its_repos_and_gives_their_inputs_values()
    {
        var result = DefinitionReader.Read("""
            schema: 1
            orchestration: storefront
            repos:
              services: { path: ../shop-services }
              web: { use: shop-web }
            containers:
              mail: { preset: mailpit }
            inputs:
              mail-smtp: ${mail.smtp-url}
              catalog-url: ${services.outputs.catalog-url}
            groups:
              web: [web, services/catalog-api, services/orders-api]
            links:
              Inbox: ${mail.ui.url}
            """, "storefront.orchestration.yaml");

        var model = Assert.IsType<OrchestrationModel>(result.Model);
        Assert.Equal([("../shop-services", null), ("shop-web", "shop-web")], model.Repos.Select(r => (r.Path, r.Use)));
        Assert.Equal("{export.../shop-services.catalog-url}", model.Bindings.Single(b => b.Setting == "catalog-url").Value);
        var group = model.Groups.Single();
        Assert.Equal([("shop-web", (string[]?)null), ("../shop-services", ["catalog-api", "orders-api"])],
            group.Members.Select(m => (m.Repo, m.Apps?.ToArray())));
        Assert.Equal("{mail.ui.url}", model.Links!.Single().Url);
    }
}
