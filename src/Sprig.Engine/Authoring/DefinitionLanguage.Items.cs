using System.Text.Json;
using Sprig.Engine.Compose;
using Sprig.Engine.Definitions;

namespace Sprig.Engine.Authoring;

public sealed partial class DefinitionLanguage
{
    // ---- what each part of the format holds, and what its keys mean ----

    static readonly Dictionary<string, (string Key, string Doc)[]> KeysByShape = new(StringComparer.Ordinal)
    {
        ["repo:"] =
        [
            ("schema", "Which version of the format the file is written in. Sprig reads every version it has shipped."),
            ("repo", "The repo's name, as orchestrations and the app show it."),
            ("copy-into-worktrees", "Files git doesn't have, such as a .env of secrets, copied from the repo's own checkout into each worktree."),
            ("compose", "Compose files the repo already has, by path, and the services in each that sprig runs on ports of its own."),
            ("containers", "Containers sprig runs for the repo, from an image or a preset."),
            ("inputs", "Values the repo can be given: by the workspace, an orchestration, or another repo's output of the same name."),
            ("apps", "The repo's apps and jobs, by name."),
            ("files", "Every file sprig writes values into, by path from the repo's root."),
            ("outputs", "Values the repo gives to other repos in an orchestration."),
            ("groups", "Slices of the apps to start on their own; starting one starts what its apps wait for too."),
        ],
        ["orchestration:"] =
        [
            ("schema", "Which version of the format the file is written in. Sprig reads every version it has shipped."),
            ("orchestration", "The orchestration's name, as the app shows it."),
            ("repos", "The repos it runs together, by a name of your choosing: each with path: (its folder from this file) or use: (its ID on the Repos list)."),
            ("containers", "Containers the orchestration runs for all its repos, from an image or a preset."),
            ("inputs", "A value for every repo's input of that name: literal, or a reference to one of its containers or a repo's output."),
            ("groups", "Slices to start on their own: repos, or repo/app for one app of a repo."),
            ("links", "Pages for the whole workspace, by label: a url, or a reference to one."),
        ],
        ["apps/*"] =
        [
            ("run", "How it runs: dotnet (dotnet run), node (the package's dev script, with npm or pnpm) or command."),
            ("path", "The app's folder, from the repo's root. Empty or . for the root."),
            ("command", "The command, exactly as you'd type it. Required for run: command; for the others it replaces the usual one. No references: values reach the app through files."),
            ("ports", "Ports sprig reserves for each workspace: any, a port, or a list such as [8000, 8110-8115]. The app learns its port from a file it reads."),
            ("health", "A path, such as /health, on its http port that answers with success once it's ready."),
            ("ready-log", "A regular expression; it's ready once a line of its output matches."),
            ("restart", "never (the default), on-failure or always: start it again when it exits on its own."),
            ("once", "true for a job: it runs to completion each time the apps start, and what waits for it waits for success."),
            ("retry", "For a job: how many times to try again after a failure."),
            ("wait-for", "Apps, jobs, containers and compose services to wait for before starting it."),
            ("links", "Pages worth opening, by label: a path on its http port, or a full url."),
            ("role", "web, api or worker, where sprig can't tell."),
            ("values", "Values of its own, referenced as ${app.name}."),
        ],
        ["containers/*"] =
        [
            ("preset", "postgres, redis, rabbitmq or mailpit: fills in the image, ports, health and role. What's written beside it is added or wins."),
            ("image", "The image to run, such as redis:7."),
            ("ports", "Ports, each with the port inside the container: tcp: { container: 6379 }."),
            ("env", "Environment variables set inside the container."),
            ("volumes", "Volumes, by name, to the path inside the container. Each workspace has its own."),
            ("health", "A command run inside the container that exits 0 once it's ready."),
            ("role", "What it is (database, cache, queue…), for how sprig draws it."),
            ("values", "Values of its own, referenced as ${container.name}."),
        ],
        ["compose/*"] =
        [
            ("services", "The services sprig runs, each on ports of its own per workspace. Services not named here are left out."),
            ("set", "Any other value in the file, by a dotted path such as services.db.container_name."),
        ],
        ["compose/*/services/*"] =
        [
            ("ports", "Ports, each with the port inside the container: tcp: { container: 5432 }."),
            ("health", "A command run inside the service that exits 0 once it's ready, replacing the file's own check."),
            ("role", "What it is (database, cache, queue…), for how sprig draws it."),
            ("values", "Values of its own, referenced as ${service.name}."),
        ],
        ["ports/*"] =
        [
            ("container", "The port inside the container."),
            ("host", "Where on this machine it may be: any, a port, or a list such as [8000, 8110-8115]."),
            ("scheme", "How its url starts, such as http. A port with no scheme has no url."),
        ],
        ["inputs/*"] =
        [
            ("type", "url, connection-string, host-port, secret or text (the default). A secret is kept out of everything sprig shows."),
            ("default", "What to use when nothing else gives a value. Without one, sprig asks when a workspace is made."),
            ("role", "What the value points at (queue, identity…), for how sprig draws it."),
        ],
        ["outputs/*"] =
        [
            ("type", "url, connection-string, host-port, secret or text (the default)."),
            ("value", "The value, usually a reference such as ${catalog-api.url}."),
        ],
        ["files/*"] =
        [
            ("read-by", "The apps that read the file."),
            ("values", "What sprig writes into it. In a JSON file, write them nested as the file has them."),
            ("format", "Only when the name doesn't say: dotenv, json or launch-settings."),
            ("profile", "For a launch-settings file: the profile's name, sprig by default."),
        ],
        ["repos/*"] =
        [
            ("path", "The repo's folder, from this file."),
            ("use", "The repo's ID on the Repos list: its folder and definition come from there."),
        ],
    };

    static string Shape(IReadOnlyList<string> path)
    {
        var shape = new List<string>();
        for (var i = 0; i < path.Count; i++)
        {
            var named = i > 0 && path[i - 1] is "apps" or "containers" or "inputs" or "outputs" or "files" or "repos" or "groups" or "services" or "ports" or "links" or "volumes" or "env"
                        || i > 0 && path[i - 1] == "values" && i >= 2
                        || i == 1 && path[0] == "compose";
            shape.Add(named ? "*" : path[i]);
        }
        var text = string.Join('/', shape);
        // Ports read the same wherever they're declared.
        var ports = text.LastIndexOf("ports/*", StringComparison.Ordinal);
        return ports >= 0 && ports + 7 == text.Length ? "ports/*" : text;
    }

    static string? KeyDocs(bool orchestration, IReadOnlyList<string> path, string key)
    {
        var shape = path.Count == 0 ? orchestration ? "orchestration:" : "repo:" : Shape(path);
        return KeysByShape.TryGetValue(shape, out var keys) ? keys.FirstOrDefault(k => k.Key == key).Doc : null;
    }

    // ---- keys ----

    IEnumerable<EditorCompletion> KeyItems(Outline outline, IReadOnlyList<string> path, string word, string text)
    {
        if (path.Count == 0)
        {
            string[] kinds = text.Contains("repo:", StringComparison.Ordinal) ? ["repo:"]
                : outline.IsOrchestration ? ["orchestration:"] : ["repo:", "orchestration:"];
            return kinds.SelectMany(k => KeysByShape[k]).Select(k => new EditorCompletion(k.Key, "key", k.Doc));
        }

        var shape = Shape(path);
        if (KeysByShape.TryGetValue(shape, out var keys)) return keys.Select(k => new EditorCompletion(k.Key, "key", k.Doc));

        return shape switch
        {
            "compose" => ComposeFiles().Select(f => new EditorCompletion(f, "compose file", "A compose file in the repo.")),
            "compose/*/services" => ComposeServices(path[1])
                .Where(s => !outline.Resources.ContainsKey(s))
                .Select(s => new EditorCompletion(s, "service", $"A service in {path[1]}.")),
            "files" => CandidateFiles(outline),
            "apps/*/ports" => [new("http", "port", "An http port: it has a url, and health can ask it."), new("https", "port", "An https port: it has an https url.")],
            _ when shape.StartsWith("files/*/values", StringComparison.Ordinal)
                => ExistingKeys(path[1], path.Skip(3).ToArray())
                    .Select(k => new EditorCompletion(k, "in the file", "Already in the file. Sprig's value takes over while a workspace is up, and yours is put back when it goes.")),
            _ => [],
        };
    }

    // ---- values ----

    IEnumerable<EditorCompletion> ValueItems(Outline outline, IReadOnlyList<string> path, string word, bool inList)
    {
        if (path.Count == 0) return [];
        var key = path[^1];
        var shape = Shape(path);
        var owner = path.Count >= 2 ? path[1] : null;

        return (shape, key) switch
        {
            ("schema", _) => [new($"{DefinitionReader.CurrentSchema}", "schema", "The format this sprig writes.")],
            ("apps/*/run", _) =>
            [
                new("dotnet", "runner", "dotnet run, from the app's path."),
                new("node", "runner", "The package's dev script, with npm or pnpm; packages are installed first when they're missing or stale."),
                new("command", "runner", "Any command, given with command:."),
            ],
            ("apps/*/restart", _) =>
            [
                new("never", "restart", "Leave it exited."),
                new("on-failure", "restart", "Start it again after it exits with an error."),
                new("always", "restart", "Start it again whenever it exits on its own."),
            ],
            ("apps/*/once", _) => [new("true", "job", "Run it to completion each time the apps start."), new("false", "app", "Keep it running.")],
            ("apps/*/role", _) => Choices(["web", "api", "worker"], "role"),
            (_, "role") when path[0] is "containers" or "compose" => Choices(DefinitionReader.InfraRoles, "role"),
            ("inputs/*/role", _) => Choices(DefinitionReader.Roles, "role"),
            ("inputs/*/type" or "outputs/*/type", _) => Choices(DefinitionReader.ValueTypes.Keys, "type"),
            ("files/*/format", _) => Choices(["dotenv", "json", "launch-settings"], "format"),
            ("containers/*/preset", _) => DefinitionReader.Presets.Select(p => new EditorCompletion(p.Key, "preset", p.Value.Image)),
            ("apps/*/path", _) => Folders(word),
            ("repos/*/path", _) => Folders(word),
            ("repos/*/use", _) => (context.RepoIds?.Invoke() ?? []).Select(id => new EditorCompletion(id, "repo", "On the Repos list.")),
            ("apps/*/wait-for", _) => outline.Resources.Values.Where(r => r.Name != owner).Select(r => new EditorCompletion(r.Name, r.Kind)),
            ("files/*/read-by", _) => outline.Resources.Values.Where(r => r.Path is not null).Select(r => new EditorCompletion(r.Name, r.Kind)),
            ("groups/*", _) when outline.IsOrchestration => outline.Repos.Keys.Select(r => new EditorCompletion(r, "repo")),
            ("groups/*", _) => outline.Resources.Values.Where(r => r.Path is not null).Select(r => new EditorCompletion(r.Name, r.Kind)),
            ("ports/*", _) when path[0] == "apps" => [new("any", "port", "Any free port, different for each workspace.")],
            ("apps/*/health", _) => [new("/health", "path"), new("/healthz", "path"), new("/healthcheck", "path")],
            _ => [],
        };
    }

    static IEnumerable<EditorCompletion> Choices(IEnumerable<string> choices, string kind) => choices.Select(c => new EditorCompletion(c, kind));

    // ---- references ----

    IEnumerable<EditorCompletion> ReferenceItems(Outline outline, string reference)
    {
        var parts = reference.Split('.');
        if (parts.Length == 1)
        {
            var items = outline.Resources.Values.Select(r => new EditorCompletion(r.Name, r.Kind, DescribeReference(outline, r.Name + "." + (r.Ports.Count > 0 ? "port" : "")))).ToList();
            items.AddRange(outline.Repos.Keys.Select(r => new EditorCompletion(r, "repo", $"A repo's outputs: ${{{r}.outputs.name}}.")));
            if (!outline.IsOrchestration) items.Add(new("inputs", "inputs", "The repo's inputs: ${inputs.name}."));
            items.Add(new("workspace", "workspace", "The workspace's name: ${workspace.name}."));
            return items;
        }

        var first = parts[0];
        if (first == "inputs" && parts.Length == 2)
            return outline.Inputs.Select(i => new EditorCompletion(i.Name, i.Type, DescribeReference(outline, "inputs." + i.Name)));
        if (first == "workspace" && parts.Length == 2) return [new("name", "workspace", "The workspace's name, such as feat-x.")];
        if (outline.Repos.TryGetValue(first, out var repo))
            return parts.Length == 2 ? [new("outputs", "outputs", $"{first}'s outputs.")]
                : parts.Length == 3 && parts[1] == "outputs" ? RepoOutputs(repo.Path).Select(o => new EditorCompletion(o, "output", $"{first}'s output {o}."))
                : [];

        if (!outline.Resources.TryGetValue(first, out var resource)) return [];
        if (parts.Length == 2)
        {
            var items = new List<EditorCompletion>();
            if (resource.Ports.Count > 0)
            {
                items.Add(new("port", "port", DescribeReference(outline, first + ".port")));
                if (resource.Ports[0].Scheme is not null) items.Add(new("url", "url", DescribeReference(outline, first + ".url")));
                items.AddRange(resource.Ports.Select(p => new EditorCompletion(p.Name, "named port", $"Its port {p.Name}: ${{{first}.{p.Name}.port}}" + (p.Scheme is null ? "." : $" and ${{{first}.{p.Name}.url}}."))));
            }
            items.AddRange(resource.Values.Select(v => new EditorCompletion(v.Key, "value", DescribeReference(outline, $"{first}.{v.Key}"))));
            return items;
        }
        if (parts.Length == 3 && resource.Ports.FirstOrDefault(p => p.Name == parts[1]) is { } port)
            return port.Scheme is null
                ? [new("port", "port", DescribeReference(outline, $"{first}.{port.Name}.port"))]
                : [new("port", "port", DescribeReference(outline, $"{first}.{port.Name}.port")), new("url", "url", DescribeReference(outline, $"{first}.{port.Name}.url"))];
        return [];
    }

    static string? DescribeReference(Outline outline, string reference)
    {
        var parts = reference.Split('.');
        if (parts is ["workspace", "name"]) return "The workspace's name, such as feat-x: for names that must differ between workspaces.";
        if (parts is ["inputs", var inputName])
        {
            var input = outline.Inputs.FirstOrDefault(i => i.Name == inputName);
            if (input is null) return $"No input '{inputName}' is declared under inputs:.";
            return $"The input {inputName} ({input.Type}): the workspace's value, else an orchestration's, else another repo's output of that name"
                   + (input.Default is { } d ? $", else its default {d}." : ". With none of those, sprig asks for one.");
        }
        if (parts.Length == 3 && parts[1] == "outputs" && outline.Repos.ContainsKey(parts[0]))
            return $"{parts[0]}'s output {parts[2]}, as its sprig.yaml gives it.";
        if (!outline.Resources.TryGetValue(parts[0], out var resource)) return null;

        string Port(OutlinePort port, string what) => what == "url"
            ? $"The url of {resource.Kind} {resource.Name}'s port {port.Name}: {port.Scheme}://localhost:<port>, with a port sprig picks for each workspace."
            : $"The port on this machine that {resource.Kind} {resource.Name}'s port {port.Name} has in this workspace. Sprig picks it, so each workspace has its own.";

        return parts switch
        {
            [_, "port" or "url"] when resource.Ports.Count > 0 => Port(resource.Ports[0], parts[1]) + (resource.Ports.Count > 1 ? $" (its first port, {resource.Ports[0].Name})" : ""),
            [_, var value] when resource.Values.TryGetValue(value, out var raw) => $"{resource.Name}'s value {value}: {raw}",
            [_, var name, "port" or "url"] when resource.Ports.FirstOrDefault(p => p.Name == name) is { } port => Port(port, parts[2]),
            _ => null,
        };
    }

    // ---- what's in the repo ----

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", "dist", "build", "out", "artifacts", "packages", "coverage" };

    IEnumerable<EditorCompletion> Folders(string typed)
    {
        if (context.Folder is not { } root) return [];
        var slash = typed.LastIndexOf('/');
        var parent = slash < 0 ? "" : typed[..(slash + 1)];
        var dir = Path.Combine(root, parent);
        try
        {
            return Directory.EnumerateDirectories(dir)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => !n.StartsWith('.') && !SkipDirs.Contains(n))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .Select(n => new EditorCompletion(parent + n, "folder"))
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    IEnumerable<string> ComposeFiles()
    {
        if (context.Folder is not { } root) return [];
        return Find(root, 3, name => (name.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase) || name.StartsWith("compose", StringComparison.OrdinalIgnoreCase)
                                      || name.Contains(".compose.", StringComparison.OrdinalIgnoreCase))
                                     && (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)));
    }

    IEnumerable<string> ComposeServices(string file)
    {
        if (context.Folder is not { } root) return [];
        try { return ComposeYaml.Inspect(File.ReadAllText(Path.Combine(root, file))).Services; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ComposeException or ArgumentException) { return []; }
    }

    /// <summary>Files an app would read: the ones already in the repo, and the usual local file for each app.</summary>
    IEnumerable<EditorCompletion> CandidateFiles(Outline outline)
    {
        var items = new List<EditorCompletion>();
        if (context.Folder is { } root)
            items.AddRange(Find(root, 5, name => name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
                                                || name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                                || name.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase))
                .Select(f => new EditorCompletion(f, "file", "In the repo now.")));
        foreach (var app in outline.Resources.Values.Where(r => r.Path is not null))
        {
            var dir = app.Path is "." or "" ? "" : app.Path!.TrimEnd('/') + "/";
            if (app.Kind.StartsWith("dotnet", StringComparison.Ordinal))
            {
                items.Add(new($"{dir}appsettings.Local.json", "new file", $"A local settings file for {app.Name}; Program.cs has to load it last."));
                items.Add(new($"{dir}Properties/launchSettings.json", "new file", $"A launch profile for {app.Name}, which dotnet run and IDEs use."));
            }
            else items.Add(new($"{dir}.env.local", "new file", $"A local env file for {app.Name}."));
        }
        return items;
    }

    /// <summary>The keys a file has now, under <paramref name="sections"/> for a JSON file.</summary>
    IEnumerable<string> ExistingKeys(string file, IReadOnlyList<string> sections)
    {
        if (context.Folder is not { } root) return [];
        var full = Path.Combine(root, file);
        try
        {
            if (!File.Exists(full)) return [];
            var name = Path.GetFileName(full);
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(full), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (name.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase))
                    return json.RootElement.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object
                        ? profiles.EnumerateObject()
                            .SelectMany(p => p.Value.TryGetProperty("environmentVariables", out var env) && env.ValueKind == JsonValueKind.Object ? env.EnumerateObject().Select(e => e.Name) : [])
                            .Distinct().ToArray()
                        : [];
                var at = json.RootElement;
                foreach (var section in sections)
                    if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(section, out at)) return [];
                return at.ValueKind == JsonValueKind.Object ? at.EnumerateObject().Select(p => p.Name).ToArray() : [];
            }
            return File.ReadLines(full)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
                .Select(l => (l.StartsWith("export ", StringComparison.Ordinal) ? l[7..] : l).Split('=', 2)[0].Trim())
                .Distinct()
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return []; }
    }

    /// <summary>The outputs a repo named by path declares in its own sprig.yaml.</summary>
    IEnumerable<string> RepoOutputs(string? repoPath)
    {
        if (context.Folder is not { } root || repoPath is null) return [];
        var file = Path.Combine(root, repoPath, StoredDefinitions.RepoFileName);
        try
        {
            return File.Exists(file) && DefinitionReader.Read(File.ReadAllText(file), file).Model is Sprig.Model.RepoModel model
                ? model.Exports.Select(e => e.Name).ToArray()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Files under <paramref name="root"/> whose names match, relative to it, shallowest first.</summary>
    static IEnumerable<string> Find(string root, int depth, Func<string, bool> match)
    {
        var found = new List<string>();
        var level = new List<string> { root };
        for (var d = 0; d <= depth && level.Count > 0 && found.Count < 100; d++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                try
                {
                    found.AddRange(Directory.EnumerateFiles(dir).Where(f => match(Path.GetFileName(f)))
                        .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.OrdinalIgnoreCase));
                    next.AddRange(Directory.EnumerateDirectories(dir).Where(x => Path.GetFileName(x) is var n && !n.StartsWith('.') && !SkipDirs.Contains(n)));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            level = next;
        }
        return found;
    }
}
