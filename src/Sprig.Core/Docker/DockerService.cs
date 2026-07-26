using System.Text.Json;
using Sprig.Core.Processes;

namespace Sprig.Core.Docker;

/// <summary>Default <see cref="IDockerService"/> shelling out to <c>docker compose</c>.</summary>
public sealed class DockerService(IProcessRunner runner) : IDockerService
{
    public bool IsAvailable()
    {
        // `compose version` only prints the plugin version — it never contacts the engine, so it
        // stays true even when Docker Desktop is stopped. That's why IsEngineRunning exists.
        try { return runner.Run("docker", ["compose", "version"]).Success; }
        catch (ProcessException) { return false; }
    }

    public bool IsEngineRunning()
    {
        // `compose ls` queries the engine for running projects, so it fails ("cannot connect to the
        // Docker daemon") when the engine is down — a real reachability probe, not just a CLI check.
        try { return runner.Run("docker", ["compose", "ls"]).Success; }
        catch (ProcessException) { return false; }
    }

    public void Up(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName)
        => runner.Run("docker", [.. Base(composeFiles, projectDirectory, projectName), "up", "-d"], projectDirectory)
                 .EnsureSuccess();

    public void Down(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName, bool removeVolumes = false)
    {
        string[] tail = removeVolumes ? ["down", "-v"] : ["down"];
        runner.Run("docker", [.. Base(composeFiles, projectDirectory, projectName), .. tail], projectDirectory)
              .EnsureSuccess();
    }

    public IReadOnlyList<ContainerStatus> Ps(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName)
    {
        var r = runner.Run("docker", [.. Base(composeFiles, projectDirectory, projectName), "ps", "--format", "json"], projectDirectory);
        return r.Success ? ParsePs(r.StdOut) : [];
    }

    // The mandatory S2 prefix on every compose call: one `-f` per generated file, then the project.
    static string[] Base(IReadOnlyList<string> composeFiles, string projectDirectory, string projectName)
        => ["compose", .. composeFiles.SelectMany(f => new[] { "-f", f }),
            "--project-directory", projectDirectory, "-p", projectName];

    // `docker compose ps --format json` emits either a JSON array or newline-delimited objects
    // depending on the compose version — handle both, tolerantly.
    internal static IReadOnlyList<ContainerStatus> ParsePs(string output)
    {
        var results = new List<ContainerStatus>();
        var trimmed = output.Trim();
        if (trimmed.Length == 0) return results;

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            if (trimmed[0] == '[')
            {
                foreach (var line in JsonSerializer.Deserialize<List<PsLine>>(trimmed, opts) ?? [])
                    AddLine(results, line);
            }
            else
            {
                foreach (var raw in trimmed.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0) continue;
                    var parsed = JsonSerializer.Deserialize<PsLine>(line, opts);
                    if (parsed is not null) AddLine(results, parsed);
                }
            }
        }
        catch (JsonException) { /* tolerate unexpected formats */ }

        return results;
    }

    static void AddLine(List<ContainerStatus> into, PsLine line)
    {
        if (!string.IsNullOrEmpty(line.Name))
            into.Add(new ContainerStatus(line.Name, line.State ?? line.Status ?? "unknown"));
    }

    sealed class PsLine
    {
        public string? Name { get; set; }
        public string? State { get; set; }
        public string? Status { get; set; }
    }
}
