using System.Text.Json;
using Sprig.Core.Processes;

namespace Sprig.Core.Docker;

/// <summary>Default <see cref="IDockerService"/> shelling out to <c>docker compose</c>.</summary>
public sealed class DockerService(IProcessRunner runner) : IDockerService
{
    public bool IsAvailable()
    {
        try { return runner.Run("docker", ["compose", "version"]).Success; }
        catch (ProcessException) { return false; }
    }

    public void Up(string composeFile, string projectDirectory, string projectName)
        => runner.Run("docker", [.. Base(composeFile, projectDirectory, projectName), "up", "-d"], projectDirectory)
                 .EnsureSuccess();

    public void Down(string composeFile, string projectDirectory, string projectName, bool removeVolumes = false)
    {
        string[] tail = removeVolumes ? ["down", "-v"] : ["down"];
        runner.Run("docker", [.. Base(composeFile, projectDirectory, projectName), .. tail], projectDirectory)
              .EnsureSuccess();
    }

    public IReadOnlyList<ContainerStatus> Ps(string composeFile, string projectDirectory, string projectName)
    {
        var r = runner.Run("docker", [.. Base(composeFile, projectDirectory, projectName), "ps", "--format", "json"], projectDirectory);
        return r.Success ? ParsePs(r.StdOut) : [];
    }

    // The mandatory S2 prefix on every compose call.
    static string[] Base(string composeFile, string projectDirectory, string projectName)
        => ["compose", "-f", composeFile, "--project-directory", projectDirectory, "-p", projectName];

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
