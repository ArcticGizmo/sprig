namespace Sprig.Engine.Definitions;

/// <summary>
/// Every key the format has, where it goes, and what it's for. The reader checks a file's keys against these, the
/// editor offers and explains them, and the JSON Schema is generated from them, so the three can't disagree.
/// </summary>
public static class DefinitionKeys
{
    public sealed record Key(string Name, string Doc);

    public static readonly Key[] Repo =
    [
        new("schema", "Which version of the format the file is written in. Sprig reads every version it has shipped."),
        new("repo", "The repo's name, as orchestrations and the app show it."),
        new("copy-into-worktrees", "Files git doesn't have, such as a .env of secrets, copied from the repo's own checkout into each worktree."),
        new("compose", "Compose files the repo already has, by path, and the services in each that sprig runs on ports of its own."),
        new("containers", "Containers sprig runs for the repo, from an image or a preset."),
        new("inputs", "Values the repo can be given: by the workspace, an orchestration, or another repo's output of the same name."),
        new("apps", "The repo's apps and jobs, by name."),
        new("files", "Every file sprig writes values into, by path from the repo's root."),
        new("outputs", "Values the repo gives to other repos in an orchestration."),
        new("groups", "Slices of the apps to start on their own; starting one starts what its apps wait for too."),
    ];

    public static readonly Key[] Orchestration =
    [
        new("schema", "Which version of the format the file is written in. Sprig reads every version it has shipped."),
        new("orchestration", "The orchestration's name, as the app shows it."),
        new("repos", "The repos it runs together, by a name of your choosing: each with path: (its folder from this file) or use: (its ID on the Repos list)."),
        new("containers", "Containers the orchestration runs for all its repos, from an image or a preset."),
        new("inputs", "A value for every repo's input of that name: literal, or a reference to one of its containers or a repo's output."),
        new("groups", "Slices to start on their own: repos, or repo/app for one app of a repo."),
        new("links", "Pages for the whole workspace, by label: a url, or a reference to one."),
    ];

    public static readonly Key[] App =
    [
        new("run", "How it runs: dotnet (dotnet run), node (the package's dev script, with npm or pnpm) or command."),
        new("path", "The app's folder, from the repo's root. Empty or . for the root."),
        new("command", "The command, exactly as you'd type it. Required for run: command; for the others it replaces the usual one. No references: values reach the app through files."),
        new("ports", "Ports sprig reserves for each workspace: any, a port, or a list such as [8000, 8110-8115]. The app learns its port from a file it reads."),
        new("health", "A path, such as /health, on its first http port that answers with success once it's ready. For another port, give the port a health: of its own."),
        new("ready-log", "A regular expression; it's ready once a line of its output matches."),
        new("restart", "never (the default), on-failure or always: start it again when it exits on its own."),
        new("max-restarts", "With restart: how many times in a row to start it again before leaving it exited. 5 by default; a run that lasts a while starts the count again."),
        new("once", "true for a job: it runs to completion each time the apps start, and what waits for it waits for success."),
        new("retry", "For a job: how many times to try again after a failure."),
        new("wait-for", "Apps, jobs, containers and compose services to wait for before starting it."),
        new("links", "Pages worth opening, by label: a path on its http port, or a full url."),
        new("role", "web, api or worker, where sprig can't tell."),
        new("values", "Values of its own, referenced as ${app.name}."),
    ];

    public static readonly Key[] Container =
    [
        new("preset", "postgres, redis, rabbitmq or mailpit: fills in the image, ports, health and role. What's written beside it is added or wins."),
        new("image", "The image to run, such as redis:7."),
        new("ports", "Ports, each with the port inside the container: tcp: { container: 6379 }."),
        new("env", "Environment variables set inside the container."),
        new("volumes", "Volumes, by name, to the path inside the container. Each workspace has its own."),
        new("health", "A command run inside the container that exits 0 once it's ready."),
        new("role", "What it is (database, cache, queue…), for how sprig draws it."),
        new("values", "Values of its own, referenced as ${container.name}."),
    ];

    public static readonly Key[] ComposeFile =
    [
        new("services", "The services sprig runs, each on ports of its own per workspace. Services not named here are left out."),
        new("set", "Any other value in the file, by a dotted path such as services.db.container_name."),
    ];

    public static readonly Key[] ComposeService =
    [
        new("ports", "Ports, each with the port inside the container: tcp: { container: 5432 }."),
        new("health", "A command run inside the service that exits 0 once it's ready, replacing the file's own check."),
        new("role", "What it is (database, cache, queue…), for how sprig draws it."),
        new("values", "Values of its own, referenced as ${service.name}."),
    ];

    /// <summary>A port written as a map. An app's has no <c>container</c>.</summary>
    public static readonly Key[] Port =
    [
        new("container", "The port inside the container."),
        new("host", "Where on this machine it may be: any, a port, or a list such as [8000, 8110-8115]."),
        new("scheme", "How its url starts, such as http. A port with no scheme has no url."),
    ];

    public static readonly Key[] AppPort =
    [
        .. Port.Where(k => k.Name != "container"),
        new("health", "A path, such as /health, on this port that answers with success once the app is ready. Every port with one must answer."),
    ];

    public static readonly Key[] Input =
    [
        new("type", "url, connection-string, host-port, secret or text (the default). A secret is kept out of everything sprig shows."),
        new("default", "What to use when nothing else gives a value. Without one, sprig asks when a workspace is made."),
        new("role", "What the value points at (queue, identity…), for how sprig draws it."),
    ];

    public static readonly Key[] Output =
    [
        new("type", "url, connection-string, host-port, secret or text (the default)."),
        new("value", "The value, usually a reference such as ${catalog-api.url}."),
    ];

    public static readonly Key[] File =
    [
        new("read-by", "The apps that read the file."),
        new("values", "What sprig writes into it. In a JSON file, write them nested as the file has them."),
        new("format", "Only when the name doesn't say: dotenv, json or launch-settings."),
        new("profile", "For a launch-settings file: the profile's name, sprig by default."),
    ];

    public static readonly Key[] RepoRef =
    [
        new("path", "The repo's folder, from this file."),
        new("use", "The repo's ID on the Repos list: its folder and definition come from there."),
    ];

    public static string[] Names(Key[] keys) => [.. keys.Select(k => k.Name)];
}
