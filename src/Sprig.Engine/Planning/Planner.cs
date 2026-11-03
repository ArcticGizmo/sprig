using Sprig.Engine.Settings;
using Sprig.Model;

namespace Sprig.Engine.Planning;

/// <param name="Repos">Each repo's model, keyed by the path the trellis names it by.</param>
/// <param name="PortsInUse">Ports to avoid because something already listens on them.</param>
public sealed record PlanInput(
    TrellisModel Trellis,
    IReadOnlyDictionary<string, RepoModel> Repos,
    string Sprout,
    SprigSettings Settings,
    IReadOnlySet<int> PortsInUse);

/// <summary>
/// Joins a trellis and its repos into a <see cref="Plan"/>. Names are private to the repo (or trellis) that declares
/// them, so two repos can each have a <c>pg</c>; the only way between repos is an import answered by an export or a
/// trellis binding. Problems are collected rather than thrown, so one plan reports everything that's wrong.
/// </summary>
public static class Planner
{
    public static Plan Build(PlanInput input) => new Run(input).Build();

    sealed record Endpoint(EndpointModel Model, int? Port);

    sealed record Resource(string Name, string Kind, SourceLocation Location, IReadOnlyDictionary<string, Endpoint> Endpoints);

    sealed class Scope(string owner, string? repoPath, RepoModel? repo)
    {
        public string Owner { get; } = owner;
        public string? RepoPath { get; } = repoPath;
        public RepoModel? Repo { get; } = repo;
        public Dictionary<string, Resource> Resources { get; } = new(StringComparer.Ordinal);
    }

    sealed class Run(PlanInput input)
    {
        readonly List<PlanProblem> _problems = [];
        readonly List<PlannedPort> _ports = [];
        readonly List<Scope> _scopes = [];
        Scope _trellis = null!;

        public Plan Build()
        {
            CollectScopes();
            AllocatePorts();

            var apps = new List<PlannedApp>();
            var containers = new List<PlannedContainer>();
            var compose = new List<PlannedCompose>();

            foreach (var scope in _scopes)
            {
                foreach (var c in Containers(scope)) containers.Add(PlanContainer(scope, c));
                if (scope.Repo is not { } repo) continue;
                foreach (var app in repo.Apps) apps.Add(PlanApp(scope, app));
                foreach (var file in repo.ComposeFiles) compose.Add(PlanCompose(scope, file));
            }

            CheckUnusedBindings();
            var groups = input.Trellis.Groups.Select(PlanGroup).ToArray();

            return new Plan(
                input.Trellis.Name, input.Sprout, input.Trellis.PortPolicy, input.Trellis.Worktrees?.From,
                _ports, apps, containers, compose, groups, _problems);
        }

        // ---- scopes and names ----

        void CollectScopes()
        {
            _trellis = new Scope(input.Trellis.Name, null, null);
            var seenNames = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var reference in input.Trellis.Repos)
            {
                if (_scopes.Any(s => s.RepoPath == reference.Path))
                {
                    Problem($"the trellis names the repo '{reference.Path}' twice", reference.Location);
                    continue;
                }
                if (!input.Repos.TryGetValue(reference.Path, out var repo)) continue; // the loader reported why
                if (seenNames.TryGetValue(repo.Name, out var other))
                    Problem($"'{reference.Path}' and '{other}' both call themselves '{repo.Name}'; repo names must be unique in a trellis", repo.Location);
                seenNames[repo.Name] = reference.Path;

                var scope = new Scope(repo.Name, reference.Path, repo);
                foreach (var app in repo.Apps) AddResource(scope, app.Name, "app", app.Location, app.Endpoints);
                foreach (var c in repo.Containers) AddResource(scope, c.Name, "container", c.Location, c.Endpoints);
                foreach (var file in repo.ComposeFiles)
                    foreach (var service in file.Services) AddResource(scope, service.Name, "compose service", service.Location, service.Endpoints);
                _scopes.Add(scope);
            }

            foreach (var c in input.Trellis.Containers) AddResource(_trellis, c.Name, "container", c.Location, c.Endpoints);
            _scopes.Add(_trellis);
        }

        void AddResource(Scope scope, string name, string kind, SourceLocation location, IReadOnlyList<EndpointModel> endpoints)
        {
            if (scope.Resources.TryGetValue(name, out var existing))
            {
                Problem($"'{name}' is declared twice in {scope.Owner} (also on line {existing.Location.Line})", location);
                return;
            }
            var map = new Dictionary<string, Endpoint>(StringComparer.Ordinal);
            foreach (var e in endpoints)
            {
                if (!map.TryAdd(e.Name, new Endpoint(e, null)))
                    Problem($"'{name}' has two endpoints named '{e.Name}'", e.Location);
            }
            scope.Resources[name] = new Resource(name, kind, location, map);
        }

        // ---- ports ----

        /// <summary>Pinned endpoints go first, so an unpinned one can't take the only port a pin allows.</summary>
        void AllocatePorts()
        {
            var allocator = new PortAllocator(input.Settings, input.PortsInUse);
            var all = _scopes.SelectMany(s => s.Resources.Values.SelectMany(r => r.Endpoints.Values.Select(e => (s, r, e)))).ToList();

            foreach (var (scope, resource, endpoint) in all.OrderBy(x => x.e.Model.Pin is null ? 1 : 0))
            {
                int? port = null;
                if (allocator.TryAllocate(endpoint.Model.Pin, out var allocated, out var error)) port = allocated;
                else Problem($"{resource.Name}.{endpoint.Model.Name}: {error}", endpoint.Model.Location);

                ((Dictionary<string, Endpoint>)resource.Endpoints)[endpoint.Model.Name] = endpoint with { Port = port };
                _ports.Add(new PlannedPort(scope.Owner, resource.Name, endpoint.Model.Name, port, endpoint.Model.Pin, endpoint.Model.TargetPort));
            }

            // Report in declaration order rather than allocation order.
            var order = all.Select((x, i) => (key: (x.s.Owner, x.r.Name, x.e.Model.Name), i)).ToDictionary(x => x.key, x => x.i);
            _ports.Sort((a, b) => order[(a.Owner, a.Resource, a.Endpoint)].CompareTo(order[(b.Owner, b.Resource, b.Endpoint)]));
        }

        // ---- planning each thing ----

        PlannedApp PlanApp(Scope scope, AppModel app)
        {
            foreach (var wait in app.WaitFor.Where(w => !scope.Resources.ContainsKey(w)))
                Problem($"'{app.Name}' waits for '{wait}', which {scope.Owner} doesn't declare", app.Location);

            return new PlannedApp(
                scope.Owner, app.Name, app.Kind, app.Path,
                app.Env.Select(e => new PlannedEnv(e.Key, Resolve(scope, e.Value, e.Location), e.File, e.Location)).ToArray(),
                app.Run is null ? null : Resolve(scope, app.Run, app.Location),
                app.WaitFor);
        }

        PlannedContainer PlanContainer(Scope scope, ContainerModel c)
        {
            var resource = scope.Resources[c.Name];
            var ports = c.Endpoints.Select(e => $"{resource.Endpoints[e.Name].Port?.ToString() ?? "?"}:{e.TargetPort}").ToArray();
            var volumes = c.Volumes.Select(v => $"{v.Name} → {v.Target}").ToArray();
            return new PlannedContainer(scope.Owner, c.Name, c.Image, ports,
                c.Env.Select(e => new PlannedEnv(e.Key, Resolve(scope, e.Value, e.Location), null, e.Location)).ToArray(), volumes);
        }

        PlannedCompose PlanCompose(Scope scope, ComposeFileModel file)
        {
            var overrides = new List<PlannedOverride>();
            foreach (var service in file.Services)
            {
                if (service.Endpoints.Count == 0) continue;
                var resource = scope.Resources[service.Name];
                var mappings = service.Endpoints.Select(e => $"{resource.Endpoints[e.Name].Port?.ToString() ?? "?"}:{e.TargetPort}");
                var value = string.Join(", ", mappings);
                overrides.Add(new PlannedOverride($"services.{service.Name}.ports",
                    new PlannedValue(value, value, [$"ports for {service.Name}, replacing the file's own"])));
            }
            foreach (var o in file.Overrides)
                overrides.Add(new PlannedOverride(o.Path, Resolve(scope, o.Value, o.Location)));
            return new PlannedCompose(scope.Owner, file.File, overrides);
        }

        PlannedGroup PlanGroup(GroupModel group)
        {
            var apps = new List<string>();
            foreach (var member in group.Members)
            {
                var scope = _scopes.FirstOrDefault(s => s.RepoPath == member.Repo);
                if (scope?.Repo is not { } repo)
                {
                    Problem($"group '{group.Name}' names '{member.Repo}', which isn't a repo in this trellis", group.Location);
                    continue;
                }
                foreach (var name in member.Apps ?? repo.Apps.Select(a => a.Name).ToArray())
                {
                    if (repo.Apps.Any(a => a.Name == name)) apps.Add($"{repo.Name}/{name}");
                    else Problem($"group '{group.Name}' names '{name}', which {repo.Name} doesn't declare as an app", group.Location);
                }
            }
            return new PlannedGroup(group.Name, apps);
        }

        void CheckUnusedBindings()
        {
            var imported = _scopes.Where(s => s.Repo is not null).SelectMany(s => s.Repo!.Imports.Select(i => i.Name)).ToHashSet();
            foreach (var binding in input.Trellis.Bindings.Where(b => !imported.Contains(b.Import)))
                Warn($"the trellis binds '{binding.Import}', but no repo imports it", binding.Location);
        }

        // ---- resolution ----

        PlannedValue Resolve(Scope scope, string template, SourceLocation location)
        {
            IReadOnlyList<TemplatePart> parts;
            try { parts = TemplateText.Parse(template); }
            catch (FormatException e) { Problem(e.Message, location); return new PlannedValue(template, null, []); }

            var value = new System.Text.StringBuilder();
            var via = new List<string>();
            var ok = true;
            foreach (var part in parts)
            {
                if (part is TextPart text) { value.Append(text.Text); continue; }
                var resolved = ResolveRef(scope, ((RefPart)part).Path, location, []);
                if (resolved is null) { ok = false; continue; }
                value.Append(resolved.Value.value);
                via.AddRange(resolved.Value.via);
            }
            return new PlannedValue(template, ok ? value.ToString() : null, via);
        }

        (string value, IReadOnlyList<string> via)? ResolveRef(Scope scope, string path, SourceLocation location, HashSet<string> visiting)
        {
            var key = scope.Owner + "|" + path;
            if (!visiting.Add(key))
            {
                Problem($"'{path}' refers back to itself through an import or export", location);
                return null;
            }

            try
            {
                if (path == "sprout.name") return (input.Sprout, ["sprout name"]);
                if (path.StartsWith("import.", StringComparison.Ordinal)) return ResolveImport(scope, path["import.".Length..], location, visiting);
                if (path.StartsWith("export.", StringComparison.Ordinal)) return ResolveNamedExport(scope, path["export.".Length..], location, visiting);
                return ResolveEndpoint(scope, path, location);
            }
            finally
            {
                visiting.Remove(key);
            }
        }

        (string, IReadOnlyList<string>)? ResolveEndpoint(Scope scope, string path, SourceLocation location)
        {
            var bits = path.Split('.');
            if (bits.Length != 3 || bits[2] is not ("port" or "url"))
            {
                Problem($"'{{{path}}}' isn't something sprig can fill in", location);
                return null;
            }
            var (resourceName, endpointName, what) = (bits[0], bits[1], bits[2]);
            if (!scope.Resources.TryGetValue(resourceName, out var resource))
            {
                Problem($"'{resourceName}' isn't declared in {scope.Owner}" +
                        (scope.Repo is not null ? "; to use something from another repo, import it" : ""), location);
                return null;
            }
            if (!resource.Endpoints.TryGetValue(endpointName, out var endpoint))
            {
                Problem($"'{resourceName}' has no endpoint named '{endpointName}'", location);
                return null;
            }
            if (endpoint.Port is not { } port) return null; // allocation already reported why

            var label = $"{resourceName}.{endpointName} port {port}";
            if (what == "port") return (port.ToString(), [label]);
            if (endpoint.Model.Scheme is not { } scheme)
            {
                Problem($"'{resourceName}.{endpointName}' has no url (give the endpoint a scheme)", location);
                return null;
            }
            return ($"{scheme}://localhost:{port}", [label]);
        }

        (string, IReadOnlyList<string>)? ResolveImport(Scope scope, string name, SourceLocation location, HashSet<string> visiting)
        {
            if (scope.Repo is null)
            {
                Problem($"the trellis can't import '{name}'; bind it to a value instead", location);
                return null;
            }
            var declared = scope.Repo.Imports.FirstOrDefault(i => i.Name == name);
            var at = declared?.Location ?? location;

            var binding = input.Trellis.Bindings.FirstOrDefault(b => b.Import == name);
            if (binding is not null)
            {
                var bound = Resolve(_trellis, binding.Value, binding.Location);
                if (bound.Value is null) return null;
                return (bound.Value, [$"import {name} ← trellis binding", .. bound.Via]);
            }

            var exporters = _scopes
                .Where(s => s.Repo is not null && s != scope)
                .SelectMany(s => s.Repo!.Exports.Where(e => e.Name == name).Select(e => (scope: s, export: e)))
                .ToList();

            if (exporters.Count == 0)
            {
                Problem($"{scope.Owner} imports '{name}', but no repo in the trellis exports it and the trellis doesn't bind it", at);
                return null;
            }
            if (exporters.Count > 1)
            {
                var names = string.Join(" and ", exporters.Select(x => x.scope.Owner));
                Problem($"{names} all export '{name}'; pick one in the trellis with trellis.Bind(\"{name}\", <repo>.Export(\"{name}\"))", at);
                return null;
            }

            var (owner, export) = exporters[0];
            if (declared is not null && declared.Type != export.Type)
            {
                Problem($"{scope.Owner} imports '{name}' as {declared.Type}, but {owner.Owner} exports it as {export.Type}", at);
                return null;
            }
            var value = ResolveTemplate(owner, export.Value, export.Location, visiting);
            if (value is null) return null;
            return (value.Value.value, [$"import {name} ← {owner.Owner}", .. value.Value.via]);
        }

        (string, IReadOnlyList<string>)? ResolveNamedExport(Scope scope, string rest, SourceLocation location, HashSet<string> visiting)
        {
            var dot = rest.LastIndexOf('.');
            var (repoPath, name) = dot > 0 ? (rest[..dot], rest[(dot + 1)..]) : (rest, "");
            var owner = _scopes.FirstOrDefault(s => s.RepoPath == repoPath);
            if (owner?.Repo is null)
            {
                Problem($"'{repoPath}' isn't a repo in this trellis", location);
                return null;
            }
            var export = owner.Repo.Exports.FirstOrDefault(e => e.Name == name);
            if (export is null)
            {
                Problem($"{owner.Owner} doesn't export '{name}'", location);
                return null;
            }
            var value = ResolveTemplate(owner, export.Value, export.Location, visiting);
            if (value is null) return null;
            return (value.Value.value, [$"{owner.Owner} export {name}", .. value.Value.via]);
        }

        (string value, IReadOnlyList<string> via)? ResolveTemplate(Scope scope, string template, SourceLocation location, HashSet<string> visiting)
        {
            IReadOnlyList<TemplatePart> parts;
            try { parts = TemplateText.Parse(template); }
            catch (FormatException e) { Problem(e.Message, location); return null; }

            var value = new System.Text.StringBuilder();
            var via = new List<string>();
            foreach (var part in parts)
            {
                if (part is TextPart text) { value.Append(text.Text); continue; }
                var resolved = ResolveRef(scope, ((RefPart)part).Path, location, visiting);
                if (resolved is null) return null;
                value.Append(resolved.Value.value);
                via.AddRange(resolved.Value.via);
            }
            return (value.ToString(), via);
        }

        IEnumerable<ContainerModel> Containers(Scope scope)
            => scope.Repo?.Containers ?? (scope == _trellis ? input.Trellis.Containers : []);

        void Problem(string message, SourceLocation? location) => _problems.Add(new PlanProblem(ProblemSeverity.Error, message, location));

        void Warn(string message, SourceLocation? location) => _problems.Add(new PlanProblem(ProblemSeverity.Warning, message, location));
    }
}
