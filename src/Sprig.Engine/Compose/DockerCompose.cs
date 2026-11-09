using Sprig.Engine.Processes;

namespace Sprig.Engine.Compose;

/// <summary>One compose project: a generated file, run under its own project name so each workspace gets its own
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
    /// <exception cref="ComposeUpException">Says which service failed and how, with the end of its log, rather than
    /// the whole command and everything compose printed (that already went to <paramref name="onOutput"/>).</exception>
    public void Up(ComposeProject project, Action<string>? onOutput, CancellationToken cancellation)
    {
        var result = Compose(project, ["up", "--detach", "--wait", "--wait-timeout", ((int)WaitTimeout.TotalSeconds).ToString(), "--remove-orphans"],
            onOutput, cancellation);
        if (!result.Success) throw new ComposeUpException(Explain(project, result), result);
    }

    public const int LogLinesShown = 12;

    string Explain(ComposeProject project, ProcessResult result)
    {
        IReadOnlyList<ComposeService> services;
        try { services = Services(project.Name); }
        catch (ProcessException) { services = []; }
        var failed = services.Where(s => s.Failed).ToArray();
        if (failed.Length == 0)
        {
            // Nothing started badly, so compose itself refused: a port in use, an image that won't pull, a bad file.
            var reason = result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
                .LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase));
            return reason is null
                ? $"docker compose couldn't start {project.Name} (exit {result.ExitCode}). Its output is in the step above."
                : $"docker compose couldn't start {project.Name}: {reason}";
        }

        var text = new System.Text.StringBuilder();
        text.Append(string.Join(" ", failed.Select(s => s.Why + ".")));
        foreach (var service in failed)
        {
            var log = Logs(project.Name, service.Service);
            if (log.Count == 0) continue;
            text.Append($"\n\nThe end of {service.Service}'s log:\n");
            text.Append(string.Join("\n", log));
        }
        return text.ToString();
    }

    /// <summary>Each service's container and how it's doing, from <c>docker compose ps</c>.</summary>
    public IReadOnlyList<ComposeService> Services(string projectName)
    {
        var r = runner.Run("docker", ["compose", "-p", projectName, "ps", "--all", "--format", "json"]).EnsureSuccess();
        return ComposeService.Parse(r.StdOut);
    }

    IReadOnlyList<string> Logs(string projectName, string service)
    {
        try
        {
            var r = runner.Run("docker", ["compose", "-p", projectName, "logs", "--no-color", "--no-log-prefix", "--tail", LogLinesShown.ToString(), service]);
            return r.Success ? r.StdOut.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(LogLinesShown).ToArray() : [];
        }
        catch (ProcessException) { return []; }
    }

    /// <param name="volumes">Also delete the project's volumes (its databases).</param>
    public void Down(ComposeProject project, bool volumes, Action<string>? onOutput = null, CancellationToken cancellation = default)
    {
        string[] args = volumes ? ["down", "--volumes", "--remove-orphans"] : ["down", "--remove-orphans"];
        Compose(project, args, onOutput, cancellation).EnsureSuccess();
    }

    /// <summary>Start one service's container again, as <c>up</c> left it; it must have been created by an <c>up</c>.</summary>
    public void Start(ComposeProject project, string service) => Compose(project, ["start", service], null, default).EnsureSuccess();

    /// <summary>Stop one service's container, keeping it (and its data) for <see cref="Start"/>.</summary>
    public void Stop(ComposeProject project, string service) => Compose(project, ["stop", service], null, default).EnsureSuccess();

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
