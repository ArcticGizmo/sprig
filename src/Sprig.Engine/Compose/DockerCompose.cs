using Sprig.Engine.Processes;

namespace Sprig.Engine.Compose;

/// <summary>One compose project: a generated file, run under its own project name so each sprout gets its own
/// containers, networks and volumes.</summary>
/// <param name="ProjectDirectory">Where relative paths in the file resolve, and where compose reads its <c>.env</c>.</param>
public sealed record ComposeProject(string Name, string File, string ProjectDirectory);

/// <summary><c>docker compose</c>, by shelling out.</summary>
public sealed class DockerCompose(IProcessRunner runner)
{
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Null when docker compose answers, else why it can't be used.</summary>
    public string? Unavailable()
    {
        try
        {
            var r = runner.Run("docker", ["compose", "version"]);
            if (!r.Success) return "docker compose isn't working: " + FirstLine(r.StdErr + r.StdOut);
            var info = runner.Run("docker", ["info", "--format", "{{.ServerVersion}}"]);
            return info.Success ? null : "docker is installed but its engine isn't running. Start Docker Desktop (or your engine) and try again.";
        }
        catch (ProcessException e)
        {
            return "docker isn't installed or isn't on PATH: " + e.Message;
        }
    }

    /// <summary>Start the project and wait until every service is running and, where it has a health check,
    /// healthy.</summary>
    public void Up(ComposeProject project, Action<string>? onOutput, CancellationToken cancellation)
        => Compose(project, ["up", "--detach", "--wait", "--wait-timeout", ((int)WaitTimeout.TotalSeconds).ToString(), "--remove-orphans"],
            onOutput, cancellation).EnsureSuccess();

    /// <param name="volumes">Also delete the project's volumes (its databases).</param>
    public void Down(ComposeProject project, bool volumes, Action<string>? onOutput = null, CancellationToken cancellation = default)
    {
        string[] args = volumes ? ["down", "--volumes", "--remove-orphans"] : ["down", "--remove-orphans"];
        Compose(project, args, onOutput, cancellation).EnsureSuccess();
    }

    /// <summary>How many of the project's containers are running.</summary>
    public int Running(string projectName)
    {
        var r = runner.Run("docker", ["ps", "--quiet", "--filter", $"label=com.docker.compose.project={projectName}"]);
        return r.Success ? r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length : 0;
    }

    ProcessResult Compose(ComposeProject project, string[] args, Action<string>? onOutput, CancellationToken cancellation)
    {
        // Without the file, down still works from the project's labels (the file may be gone after a failed up).
        string[] target = File.Exists(project.File)
            ? ["-p", project.Name, "-f", project.File, "--project-directory", project.ProjectDirectory]
            : ["-p", project.Name];
        return runner.Run("docker", ["compose", .. target, .. args], Directory.Exists(project.ProjectDirectory) ? project.ProjectDirectory : null,
            cancellation, onOutput);
    }

    static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
}
