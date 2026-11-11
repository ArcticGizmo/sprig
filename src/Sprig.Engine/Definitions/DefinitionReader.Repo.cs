using Sprig.Model;
using YamlDotNet.RepresentationModel;

namespace Sprig.Engine.Definitions;

public static partial class DefinitionReader
{
    /// <summary>Value types as the file writes them, to the model's names for them.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ValueTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["text"] = "text",
        ["url"] = "url",
        ["connection-string"] = "connectionstring",
        ["host-port"] = "hostport",
        ["secret"] = "secret",
    };

    sealed class RepoReader(FileReader file)
    {
        readonly Scope _scope = new(file) { Inputs = new HashSet<string>(StringComparer.Ordinal) };

        sealed class AppDraft(string name, Resource resource, Fields fields, YamlNode key)
        {
            public string Name { get; } = name;
            public Resource Resource { get; } = resource;
            public Fields Fields { get; } = fields;
            public YamlNode Key { get; } = key;
            public string Kind { get; set; } = "app";
            public string Path { get; set; } = "";
            public bool Job { get; set; }
            public IReadOnlyList<EndpointModel> Endpoints { get; set; } = [];
            public List<EnvModel> Env { get; } = [];
        }

        public RepoModel? Read(YamlMappingNode root)
        {
            var top = file.Fields(root, "a repo file", DefinitionKeys.Names(DefinitionKeys.Repo))!;
            var name = file.Text(top["repo"], "the repo's name");
            if (name is null) return file.Fail<RepoModel>(root, "the repo needs a name: 'repo: <name>'");

            var compose = ReadCompose(top["compose"]);
            var (containers, finishContainers) = ReadContainers(file, _scope, top["containers"]);
            var apps = DeclareApps(top["apps"]);
            var inputs = DeclareInputs(top["inputs"]);

            _scope.CheckValues();
            finishContainers();
            var composeFiles = compose.Select(c => c.Finish()).ToArray();
            var settings = inputs.Select(i => i()).OfType<SettingModel>().ToArray();
            ReadFiles(top["files"], apps);
            var appModels = apps.Select(BuildApp).OfType<AppModel>().ToArray();
            var exports = ReadOutputs(top["outputs"]);
            var groups = ReadGroups(top["groups"], apps);
            var copies = file.List(top["copy-into-worktrees"], "copy-into-worktrees")
                .Select(c => file.RelativePath(c.Node, "a file to copy into worktrees") is { Length: > 0 } path ? new WorktreeCopyModel(path, file.At(c.Node)) : null)
                .OfType<WorktreeCopyModel>().ToArray();

            return new RepoModel(CurrentSchema, name, file.At(root), containers, appModels, composeFiles, settings, exports,
                groups.Length > 0 ? groups : null, copies.Length > 0 ? copies : null);
        }

        // ---- compose ----

        sealed record ComposeDraft(Func<ComposeFileModel> Finish);

        List<ComposeDraft> ReadCompose(YamlNode? node)
        {
            var drafts = new List<ComposeDraft>();
            foreach (var (path, key, value) in file.Entries(node, "compose"))
            {
                if (file.RelativePath(key, "a compose file") is not { Length: > 0 } composeFile) continue;
                if (file.Fields(value, $"compose file '{path}'", DefinitionKeys.Names(DefinitionKeys.ComposeFile)) is not { } fields) continue;

                var services = new List<ComposeServiceModel>();
                foreach (var (service, serviceKey, serviceValue) in file.Entries(fields["services"], $"{path}'s services"))
                {
                    if (!file.CheckName(service, serviceKey, "a compose service")) continue;
                    var resource = new Resource(service, "compose service", serviceKey);
                    if (!_scope.Declare(resource)) continue;
                    if (file.Fields(serviceValue, $"service '{service}'", DefinitionKeys.Names(DefinitionKeys.ComposeService)) is not { } s) continue;
                    var endpoints = ReadPorts(file, s["ports"], resource, inContainer: true);
                    ReadValues(file, s["values"], resource);
                    services.Add(new ComposeServiceModel(service, file.At(serviceKey), endpoints,
                        file.Text(s["health"], $"service '{service}''s health"),
                        file.Choice(s["role"], $"service '{service}''s role", InfraRoles)));
                }

                var set = fields["set"];
                var at = file.At(key);
                drafts.Add(new ComposeDraft(() =>
                {
                    var overrides = new List<ComposeOverrideModel>();
                    foreach (var (overridePath, overrideKey, overrideValue) in file.Entries(set, $"{path}'s set"))
                        if (file.Value(overrideValue, overridePath) is { } raw && _scope.Template(raw, overrideValue) is { } template)
                            overrides.Add(new ComposeOverrideModel(overridePath, template, file.At(overrideKey)));
                    return new ComposeFileModel(composeFile, at, services, overrides);
                }));
            }
            return drafts;
        }

        // ---- apps ----

        List<AppDraft> DeclareApps(YamlNode? node)
        {
            var apps = new List<AppDraft>();
            foreach (var (name, key, value) in file.Entries(node, "apps"))
            {
                if (!file.CheckName(name, key, "an app")) continue;
                if (file.Fields(value, $"app '{name}'", DefinitionKeys.Names(DefinitionKeys.App)) is not { } fields) continue;
                var job = file.Flag(fields["once"], $"app '{name}''s once") == true;
                var resource = new Resource(name, job ? "job" : "app", key);
                if (!_scope.Declare(resource)) continue;

                var draft = new AppDraft(name, resource, fields, key) { Job = job };
                draft.Kind = file.Choice(fields["run"], $"app '{name}''s run", "dotnet", "node", "command") switch
                {
                    "dotnet" => "dotnet",
                    "node" => "node",
                    "command" => "app",
                    _ => "app",
                };
                if (fields["run"] is null) file.Error(key, $"app '{name}' needs 'run: dotnet', 'run: node' or 'run: command'");
                draft.Path = file.RelativePath(fields["path"], $"app '{name}''s path") is { Length: > 0 } appPath ? appPath : ".";

                if (job)
                {
                    foreach (var notForJobs in (string[])["ports", "health", "ready-log", "restart", "links"])
                        if (fields[notForJobs] is { } n) file.Error(n, $"'{notForJobs}' is for an app that keeps running; a job (once: true) has none");
                }
                else if (fields["retry"] is { } retry) file.Error(retry, "'retry' is for a job (once: true); an app that keeps running has 'restart'");

                draft.Endpoints = job ? [] : ReadPorts(file, fields["ports"], resource, inContainer: false);
                ReadValues(file, fields["values"], resource);
                apps.Add(draft);
            }
            return apps;
        }

        AppModel? BuildApp(AppDraft app)
        {
            var f = app.Fields;
            var command = file.Text(f["command"], $"app '{app.Name}''s command");
            if (command is not null && command.Contains("${", StringComparison.Ordinal))
                file.Error(f["command"]!, $"app '{app.Name}''s command has a reference in it, which typing the command yourself wouldn't fill in. " +
                    "Give the command as you'd type it, and the value in a file the app reads (files:)");
            if (command is null && f["run"] is YamlScalarNode { Value: "command" })
                file.Error(app.Key, $"app '{app.Name}' runs a command, so it needs 'command:'");

            var endpoints = app.Endpoints.ToList();
            if (file.Text(f["health"], $"app '{app.Name}''s health") is { } health)
            {
                var at = endpoints.FindIndex(e => e.Scheme is "http" or "https");
                if (!health.StartsWith('/')) file.Error(f["health"]!, $"app '{app.Name}''s health is a path on its http port, starting with /");
                else if (at < 0) file.Error(f["health"]!, $"app '{app.Name}' has a health path but no http port to ask it on: ports: {{ http: any }}");
                else endpoints[at] = endpoints[at] with { Health = health };
            }

            RestartModel? restart = null;
            if (app.Job)
            {
                if (file.Number(f["retry"], $"job '{app.Name}''s retry", 1, 100) is { } tries)
                    restart = new RestartModel("onFailure", tries, file.At(f["retry"]!));
            }
            else
            {
                restart = file.Choice(f["restart"], $"app '{app.Name}''s restart", "never", "on-failure", "always") switch
                {
                    "on-failure" => new RestartModel("onFailure", 5, file.At(f["restart"]!)),
                    "always" => new RestartModel("always", 5, file.At(f["restart"]!)),
                    _ => null,
                };
            }

            var waitFor = new List<string>();
            foreach (var (other, node) in file.List(f["wait-for"], $"app '{app.Name}''s wait-for"))
            {
                if (other == app.Name) file.Error(node, $"app '{app.Name}' can't wait for itself");
                else if (!_scope.Resources.ContainsKey(other)) file.Error(node, $"'{other}' isn't an app, job, container or compose service in this file");
                else waitFor.Add(other);
            }

            var links = new List<LinkModel>();
            foreach (var (label, key, value) in file.Entries(f["links"], $"app '{app.Name}''s links"))
            {
                if (file.Text(value, $"link '{label}'") is not { } raw) continue;
                if (raw.StartsWith('/'))
                {
                    if (endpoints.FirstOrDefault(e => e.Scheme is "http" or "https") is not { } web)
                    {
                        file.Error(value, $"link '{label}' is a path, but app '{app.Name}' has no http port; give a full url instead");
                        continue;
                    }
                    links.Add(new LinkModel(label, $"{{{app.Name}.{web.Name}.url}}{Literal(raw == "/" ? "" : raw)}", file.At(key)));
                }
                else if (_scope.Template(raw, value) is { } template) links.Add(new LinkModel(label, template, file.At(key)));
            }

            var readyLog = file.Text(f["ready-log"], $"app '{app.Name}''s ready-log");
            if (readyLog is not null)
            {
                try { _ = new System.Text.RegularExpressions.Regex(readyLog); }
                catch (ArgumentException e) { file.Error(f["ready-log"]!, $"app '{app.Name}''s ready-log isn't a regular expression: {e.Message}"); }
            }

            return new AppModel(app.Name, app.Kind, app.Path, file.At(app.Key), endpoints, app.Env,
                command is null ? null : Literal(command), waitFor, app.Job, readyLog, restart, links.Count > 0 ? links : null,
                file.Choice(f["role"], $"app '{app.Name}''s role", "web", "api", "worker"));
        }

        // ---- inputs and outputs ----

        List<Func<SettingModel?>> DeclareInputs(YamlNode? node)
        {
            var inputs = new List<Func<SettingModel?>>();
            foreach (var (name, key, value) in file.Entries(node, "inputs"))
            {
                if (!file.CheckName(name, key, "an input")) continue;
                if (!_scope.Inputs!.Add(name))
                {
                    file.Error(key, $"input '{name}' is declared twice");
                    continue;
                }
                if (file.Fields(value, $"input '{name}'", DefinitionKeys.Names(DefinitionKeys.Input)) is not { } fields) continue;
                var type = file.Choice(fields["type"], $"input '{name}''s type", [.. ValueTypes.Keys]) ?? "text";
                var role = file.Choice(fields["role"], $"input '{name}''s role", Roles);
                inputs.Add(() =>
                {
                    string? fallback = null;
                    if (fields["default"] is { } d && file.Value(d, $"input '{name}''s default") is { } raw && (fallback = _scope.Template(raw, d)) is null)
                        return null;
                    return new SettingModel(name, ValueTypes[type], file.At(key), fallback, role);
                });
            }
            return inputs;
        }

        ExportModel[] ReadOutputs(YamlNode? node)
        {
            var exports = new List<ExportModel>();
            foreach (var (name, key, value) in file.Entries(node, "outputs"))
            {
                if (!file.CheckName(name, key, "an output")) continue;
                if (file.Fields(value, $"output '{name}'", DefinitionKeys.Names(DefinitionKeys.Output)) is not { } fields) continue;
                var type = file.Choice(fields["type"], $"output '{name}''s type", [.. ValueTypes.Keys]) ?? "text";
                if (fields["value"] is not { } v || file.Value(v, $"output '{name}''s value") is not { } raw)
                {
                    file.Error(key, $"output '{name}' needs a value");
                    continue;
                }
                if (_scope.Template(raw, v) is { } template) exports.Add(new ExportModel(name, ValueTypes[type], template, file.At(key)));
            }
            return exports.ToArray();
        }

        // ---- files ----

        void ReadFiles(YamlNode? node, List<AppDraft> apps)
        {
            foreach (var (written, key, value) in file.Entries(node, "files"))
            {
                if (file.RelativePath(key, "a file") is not { Length: > 0 } path) continue;
                if (file.Fields(value, $"file '{written}'", DefinitionKeys.Names(DefinitionKeys.File)) is not { } fields) continue;

                var format = file.Choice(fields["format"], $"file '{written}''s format", "dotenv", "json", "launch-settings") ?? FormatOf(path);
                if (format is null)
                {
                    file.Error(key, $"sprig can't tell what kind of file '{written}' is from its name; say format: dotenv, json or launch-settings");
                    continue;
                }
                var profile = file.Text(fields["profile"], $"file '{written}''s profile");
                if (profile is not null && format != "launch-settings") file.Error(fields["profile"]!, "a profile is only for a launch-settings file");

                var readers = new List<AppDraft>();
                foreach (var (reader, readerNode) in file.List(fields["read-by"], $"file '{written}''s read-by"))
                {
                    if (apps.FirstOrDefault(a => a.Name == reader) is { } app) readers.Add(app);
                    else file.Error(readerNode, $"'{reader}' isn't an app in this file");
                }
                if (fields["read-by"] is null) file.Error(key, $"file '{written}' needs read-by: the apps that read it");

                var values = new List<(string Key, string Template, YamlNode Node)>();
                if (fields["values"] is null) file.Error(key, $"file '{written}' needs values: what sprig writes into it");
                else FileValues(fields["values"]!, format, "", values);

                var kind = format switch { "json" => "appSettings", "launch-settings" => "launchSettings", _ => "dotenv" };
                foreach (var app in readers)
                {
                    var relative = System.IO.Path.GetRelativePath(Root(app.Path), Root(path)).Replace('\\', '/');
                    foreach (var (k, template, at) in values)
                        app.Env.Add(new EnvModel(k, template, relative, file.At(at), kind, kind == "launchSettings" ? profile ?? "sprig" : null));
                }
            }
        }

        // Both paths are relative to the repo; a fixed root turns them into paths GetRelativePath can compare.
        static string Root(string path) => System.IO.Path.Combine(System.IO.Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "repo", path);

        static string? FormatOf(string path)
        {
            var name = System.IO.Path.GetFileName(path);
            if (name == ".env" || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".env", StringComparison.OrdinalIgnoreCase)) return "dotenv";
            if (name.Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase)) return "launch-settings";
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return "json";
            return null;
        }

        /// <summary>A file's values, flattened to <c>Section:Key</c> for JSON, where they're written nested as the file
        /// nests them.</summary>
        void FileValues(YamlNode node, string format, string prefix, List<(string, string, YamlNode)> values)
        {
            foreach (var (key, keyNode, value) in file.Entries(node, prefix.Length == 0 ? "values" : $"'{prefix}'"))
            {
                if (format == "json" && (key.Contains(':') || key.Contains("__", StringComparison.Ordinal)))
                {
                    file.Error(keyNode, $"in a JSON file, write '{key}' nested, as the file has it: {key.Replace("__", ":").Split(':')[0]}: {{ … }}");
                    continue;
                }
                var full = prefix.Length == 0 ? key : $"{prefix}:{key}";
                if (value is YamlMappingNode)
                {
                    if (format == "json") FileValues(value, format, full, values);
                    else file.Error(keyNode, ((YamlMappingNode)value).Style == YamlDotNet.Core.Events.MappingStyle.Flow
                        ? $"'{key}' became a set of lines because its value starts with '{{'. A value starting with '{{' needs quotes"
                        : $"a {format} file has flat keys only; '{key}' can't hold a section");
                    continue;
                }
                if (value is YamlSequenceNode)
                {
                    file.Error(value, $"'{full}' is a list; sprig writes single values only");
                    continue;
                }
                if (file.Value(value, $"'{full}'") is { } raw && _scope.Template(raw, value) is { } template) values.Add((full, template, keyNode));
            }
        }

        // ---- groups ----

        RepoGroupModel[] ReadGroups(YamlNode? node, List<AppDraft> apps)
        {
            var groups = new List<RepoGroupModel>();
            foreach (var (name, key, value) in file.Entries(node, "groups"))
            {
                if (!file.CheckName(name, key, "a group")) continue;
                var members = new List<string>();
                foreach (var (member, memberNode) in file.List(value, $"group '{name}'"))
                {
                    if (apps.Any(a => a.Name == member)) members.Add(member);
                    else file.Error(memberNode, $"'{member}' isn't an app in this file");
                }
                groups.Add(new RepoGroupModel(name, members, file.At(key)));
            }
            return groups.ToArray();
        }
    }
}
