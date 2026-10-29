using System.Net;
using System.Net.Sockets;
using System.Text;
using Sprig.App.ViewModels;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Env;
using Sprig.Core.Git;
using Sprig.Core.Ports;
using Sprig.Core.Processes;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;

namespace Sprig.Tests.Workspaces;

/// <summary>Entry points: <c>open</c>/<c>probe</c> on a provided capability, validated, resolved from the
/// workspace record, probed on request, and carried through the app's repo editor.</summary>
public class WorkspaceUrlsTests
{
    const string Config = """
        { "schema":1, "name":"client", "modules":[ { "name":"spa", "path":"",
          "provides":[ { "capability":"client-spa", "ports":{ "port":true },
                         "shapes":{ "url":"http://localhost:${sprig.client-spa.port}" },
                         "open":"url", "probe":"${sprig.client-spa.url}/health" } ] } ] }
        """;

    static ValidationResult Validate(string json) => SprigConfigValidator.Validate(SprigConfigLoader.Parse(json));

    [Fact]
    public void Open_and_probe_validate()
        => Assert.True(Validate(Config).IsValid);

    [Fact]
    public void Open_must_name_an_output()
    {
        var result = Validate(Config.Replace("\"open\":\"url\"", "\"open\":\"nope\""));
        Assert.Contains(result.Issues, i => i.Path.EndsWith(".open") && i.Message.Contains("not one of"));
    }

    [Fact]
    public void Probe_may_only_reference_its_own_outputs()
    {
        var result = Validate(Config.Replace("${sprig.client-spa.url}/health", "${sprig.api.url}/health"));
        Assert.Contains(result.Issues, i => i.Path.EndsWith(".probe"));
    }

    [Fact]
    public void Urls_resolve_from_the_record_and_the_worktree_config()
    {
        using var s = new TempStore();
        var worktree = Path.Combine(s.Root, "client--ws1");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".sprig.json"), Config);
        var record = new InstanceRecord
        {
            Workspace = "ws1",
            Repos = [new InstanceRepo
            {
                Name = "client", SourcePath = worktree, WorktreePath = worktree,
                Modules = [new InstanceModule { Name = "spa", Values = new Dictionary<string, string>
                {
                    // As checkout records them: shapes already resolved against the allocated port.
                    ["client-spa.port"] = "8103",
                    ["client-spa.url"] = "http://localhost:8103",
                } }],
            }],
        };
        var svc = new WorkspaceService(new GitService(new ProcessRunner()), new FilePortStore(s.Paths), new InstanceStore(s.Paths),
            new EnvClobberService(), new ComposeGenerator(), new FakeDockerService(), s.Paths);

        var url = Assert.Single(svc.Urls(record));
        Assert.Equal("client-spa", url.Capability);
        Assert.Equal("http://localhost:8103", url.Url);
        Assert.Equal("http://localhost:8103/health", url.Probe); // probe template resolved through the module's scope
    }

    [Fact]
    public async Task Probe_reports_a_refused_connection_as_down()
    {
        var port = FreePort();
        Assert.Equal(ProbeState.Down, await WorkspaceService.ProbeAsync($"http://127.0.0.1:{port}/", TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Probe_reports_any_non_5xx_response_as_up()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            _ = await stream.ReadAsync(new byte[1024]); // the request itself is irrelevant; any response will do
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });

        Assert.Equal(ProbeState.Up, await WorkspaceService.ProbeAsync($"http://127.0.0.1:{port}/health", TimeSpan.FromSeconds(5)));
        await serve;
    }

    [Fact]
    public void The_repo_editor_keeps_open_and_probe_on_save()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "repo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".sprig.json"), Config);

        var cap = RepoEditViewModel.Load(dir).Build().EffectiveModules[0].Provides[0];
        Assert.Equal("url", cap.Open);
        Assert.Equal("${sprig.client-spa.url}/health", cap.Probe);
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
