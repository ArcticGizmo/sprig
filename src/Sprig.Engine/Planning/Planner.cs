using Sprig.Engine.Settings;
using Sprig.Model;

namespace Sprig.Engine.Planning;

/// <param name="Repos">Each repo's model, keyed by the path the orchestration names it by.</param>
/// <param name="PortsInUse">Ports to avoid: something listens on them, or another workspace holds them.</param>
/// <param name="Leased">Ports this workspace already holds, by <see cref="PlannedPort.Key"/>. Each is kept while its pin
/// still allows it and nothing else has it.</param>
/// <param name="ComposeFiles">What's in each repo compose file, by <see cref="ComposeFileInfo.Key"/>. Without it the
/// plan can't say which services a workspace leaves out.</param>
/// <param name="Random">Set for the random port policy: unpinned ports start from a random point in the range.</param>
/// <param name="PortPolicy">The workspace's port policy, recorded in the plan.</param>
/// <param name="WorktreesFrom">Where the workspace's worktrees branched from, recorded in the plan; null when it runs its
/// repos in place, or for a plan that isn't a workspace's.</param>
/// <param name="Values">Values set on the workspace, by setting name. They win over everything else, for every repo
/// with a setting of that name.</param>
public sealed record PlanInput(
    OrchestrationModel Orchestration,
    IReadOnlyDictionary<string, RepoModel> Repos,
    string Workspace,
    SprigSettings Settings,
    IReadOnlySet<int> PortsInUse,
    IReadOnlyDictionary<string, int>? Leased = null,
    IReadOnlyDictionary<string, ComposeFileInfo>? ComposeFiles = null,
    Random? Random = null,
    string PortPolicy = "stable",
    string? WorktreesFrom = null,
    IReadOnlyDictionary<string, string>? Values = null);

/// <summary>The services in a repo's compose file.</summary>
/// <param name="FixedNames">Services with a <c>container_name</c>, which would clash between workspaces.</param>
/// <param name="Profiled">Services that only start under a compose profile, so they don't run by default anyway.</param>
public sealed record ComposeFileInfo(IReadOnlyList<string> Services, IReadOnlyList<string> FixedNames, IReadOnlyList<string> Profiled)
{
    public static string Key(string repoPath, string file) => $"{repoPath}|{file}";
}

/// <summary>
/// Joins an orchestration and its repos into a <see cref="Plan"/>. Names are private to the repo (or orchestration)
/// that declares them, so two repos can each have a <c>pg</c>; the only way between repos is a setting given by an
/// export or an orchestration binding. Problems are collected rather than thrown, so one plan reports everything
/// that's wrong.
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

        /// <summary>Containers the repo declares that this plan leaves out.</summary>
        public HashSet<string> LeftOut { get; init; } = [];
    }

    sealed class Run(PlanInput input)
    {
        readonly List<PlanProblem> _problems = [];
        readonly List<PlannedAsk> _asks = [];
        readonly List<PlannedPort> _ports = [];
        readonly List<Scope> _scopes = [];
        Scope _orchestration = null!;

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
            var groups = PlanGroups();
            var secrets = _scopes.Where(s => s.Repo is not null).SelectMany(s => s.Repo!.Settings)
                .Where(s => s.Type == "secret").Select(s => s.Name).Distinct(StringComparer.Ordinal).ToArray();
            CheckUndeclaredSecrets(apps, secrets);

            return new Plan(
                input.Orchestration.Name, input.Workspace, input.PortPolicy, input.WorktreesFrom,
                _ports, Order(apps), containers, compose, groups, _problems, PlanLinks(_orchestration, input.Orchestration.Links),
                _asks.Count > 0 ? _asks : null, SettingRoles(), secrets.Length > 0 ? secrets : null);
        }

        /// <summary>A value under a name that says it's secret, which no <c>Setting&lt;Secret&gt;</c> gives: sprig masks
        /// it by its name, but only a declared secret is masked wherever it's used, so the author is told. Only apps'
        /// values: a container's are mostly the preset's own local passwords (POSTGRES_PASSWORD), nothing to declare.</summary>
        void CheckUndeclaredSecrets(IEnumerable<PlannedApp> apps, IReadOnlyCollection<string> secrets)
        {
            foreach (var env in apps.SelectMany(a => a.Env))
            {
                if (!SecretValues.LooksSecret(env.Key) || env.Value.Value is not { Length: > 0 } value || SecretValues.IsPlain(value)) continue;
                var settings = (env.Value.Sources ?? []).Select(s => s.Setting).OfType<string>().ToArray();
                if (settings.Any(secrets.Contains)) continue;
                var from = settings.Length > 0 ? $"setting {settings[0]}, which isn't a Setting<Secret>" : "a value written in the definition";
                _problems.Add(new PlanProblem(ProblemSeverity.Warning,
                    $"{env.Key} looks secret but comes from {from}. Give it from a Setting<Secret> so sprig keeps it out of everything it shows.",
                    env.Location));
            }
        }

        /// <summary>Each setting a repo gave a role, by name; the first repo to say wins where two differ.</summary>
        IReadOnlyDictionary<string, string>? SettingRoles()
        {
            var roles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var setting in _scopes.Where(s => s.Repo is not null).SelectMany(s => s.Repo!.Settings).Where(s => s.Role is not null))
                roles.TryAdd(setting.Name, setting.Role!);
            return roles.Count > 0 ? roles : null;
        }

        // ---- scopes and names ----

        void CollectScopes()
        {
            _orchestration = new Scope(input.Orchestration.Name, null, null);
            var seenNames = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var reference in input.Orchestration.Repos)
            {
                if (_scopes.Any(s => s.RepoPath == reference.Path))
                {
                    Problem($"the orchestration names the repo '{reference.Path}' twice", reference.Location);
                    continue;
                }
                if (!input.Repos.TryGetValue(reference.Path, out var repo)) continue; // the loader reported why
                if (seenNames.TryGetValue(repo.Name, out var other))
                    Problem($"'{reference.Path}' and '{other}' both call themselves '{repo.Name}'; repo names must be unique in an orchestration", repo.Location);
                seenNames[repo.Name] = reference.Path;

                var scope = new Scope(repo.Name, reference.Path, repo) { LeftOut = UnneededFallbackInfra(reference.Path, repo) };
                foreach (var app in repo.Apps) AddResource(scope, app.Name, app.Job ? "job" : "app", app.Location, app.Endpoints);
                foreach (var c in repo.Containers.Where(c => !scope.LeftOut.Contains(c.Name)))
                    AddResource(scope, c.Name, "container", c.Location, c.Endpoints);
                foreach (var file in repo.ComposeFiles)
                    foreach (var service in file.Services) AddResource(scope, service.Name, "compose service", service.Location, service.Endpoints);
                _scopes.Add(scope);
            }

            foreach (var c in input.Orchestration.Containers) AddResource(_orchestration, c.Name, "container", c.Location, c.Endpoints);
            _scopes.Add(_orchestration);
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
            var allocator = new PortAllocator(input.Settings, input.PortsInUse, input.Random);
            var all = _scopes.SelectMany(s => s.Resources.Values.SelectMany(r => r.Endpoints.Values.Select(e => (s, r, e)))).ToList();

            // Kept leases are claimed before anything new, so a fresh allocation can't take a port this workspace holds.
            var kept = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (scope, resource, endpoint) in all)
            {
                var key = PlannedPort.LeaseKey(scope.Owner, resource.Name, endpoint.Model.Name);
                if (input.Leased?.TryGetValue(key, out var leased) == true && allocator.TryKeep(endpoint.Model.Pin, leased))
                    kept[key] = leased;
            }

            foreach (var (scope, resource, endpoint) in all.OrderBy(x => x.e.Model.Pin is null ? 1 : 0))
            {
                int? port = null;
                var key = PlannedPort.LeaseKey(scope.Owner, resource.Name, endpoint.Model.Name);
                if (kept.TryGetValue(key, out var leased)) port = leased;
                else if (allocator.TryAllocate(endpoint.Model.Pin, out var allocated, out var error)) port = allocated;
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

            // A value only ever reaches an app through a file it reads, so running it by hand or from another tool gets
            // the same values sprig's run does.
            foreach (var e in app.Env)
            {
                if (e.File is null)
                    Problem($"'{app.Name}' would get {e.Key} on its process, which running it yourself wouldn't. Write it into a file the app reads: " +
                        (app.Kind == "dotnet" ? ".AppSettings(\"appsettings.Local.json\", …) or .LaunchSettings(…)" : ".EnvFile(\".env.local\", …)"), e.Location);
                else if (e.Kind is not ("dotenv" or "appSettings" or "launchSettings"))
                    Problem($"'{app.Name}' writes {e.Key} into {e.File} as an unknown kind of file '{e.Kind}'", e.Location);
                else if (e.Kind == "launchSettings" && app.Kind != "dotnet")
                    Warn($"'{app.Name}' isn't a .NET app, so nothing reads the launch profile sprig writes {e.Key} into", e.Location);
            }
            if (app.Run is { } command && RunValues(command) is { Length: > 0 } carried)
                Problem($"'{app.Name}' has {string.Join(", ", carried.Select(c => $"{{{c}}}"))} in its Run command, which typing the command yourself wouldn't fill in. " +
                    "Give the command as you'd type it, and the value in a file the app reads, e.g. .EnvFile(\".env.local\", e => e.Set(\"PORT\", app.Port))", app.Location);

            _referenced = [];
            var env = app.Env.Select(e => new PlannedEnv(e.Key, Resolve(scope, e.Value, e.Location), e.File, e.Location, e.Kind, e.Profile)).ToArray();
            var run = app.Run is null ? null : Resolve(scope, app.Run, app.Location);
            var key = $"{scope.Owner}/{app.Name}";
            _referencedBy[key] = _referenced;
            _referenced = null;
            _locations[key] = app.Location;

            var waitsFor = app.WaitFor
                .Where(w => scope.Resources.TryGetValue(w, out var r) && r.Kind is "app" or "job")
                .Select(w => $"{scope.Owner}/{w}")
                .ToArray();
            return new PlannedApp(scope.Owner, scope.RepoPath!, app.Name, app.Kind, app.Path, env, run, app.WaitFor,
                app.Job, waitsFor, PlanHealth(app), PlanReadyLog(app), PlanRestart(app), PlanLinks(scope, app.Links), app.Role);
        }

        /// <summary>The references in a Run command template; a command that doesn't parse is left to resolving to report.</summary>
        static string[] RunValues(string command)
        {
            try { return TemplateText.Parse(command).OfType<RefPart>().Select(r => r.Path).ToArray(); }
            catch (FormatException) { return []; }
        }

        /// <summary>Resolved after the app's values, so linking to another app's page never makes it wait for that app.
        /// A link that isn't a web address is left out with a warning, rather than failing the plan over it.</summary>
        IReadOnlyList<PlannedLink>? PlanLinks(Scope scope, IReadOnlyList<LinkModel>? links)
        {
            if (links is null or { Count: 0 }) return null;
            var planned = new List<PlannedLink>();
            foreach (var link in links)
            {
                if (string.IsNullOrWhiteSpace(link.Label))
                {
                    Warn("a quick link has no label", link.Location);
                    continue;
                }
                if (Resolve(scope, link.Url, link.Location).Value is not { } url) continue; // resolving reported why
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    Warn($"the quick link '{link.Label}' goes to '{ValueMask.Apply(url)}', which isn't an http(s) address, so it's left out", link.Location);
                else planned.Add(new PlannedLink(link.Label, url));
            }
            return planned;
        }

        IReadOnlyList<PlannedHealth> PlanHealth(AppModel app)
        {
            var health = new List<PlannedHealth>();
            foreach (var endpoint in app.Endpoints.Where(e => e.Health is not null))
            {
                if (!endpoint.Health!.StartsWith('/') || !Uri.TryCreate("http://localhost" + endpoint.Health, UriKind.Absolute, out _))
                    Problem($"'{app.Name}' has a health path '{endpoint.Health}' on {endpoint.Name}; it must be a path starting with /, e.g. /health", endpoint.Location);
                else if (endpoint.Scheme != "http")
                    Problem($"'{app.Name}' has a health path on {endpoint.Name}, which isn't an HTTP endpoint", endpoint.Location);
                else health.Add(new PlannedHealth(endpoint.Name, endpoint.Health));
            }
            return health;
        }

        string? PlanReadyLog(AppModel app)
        {
            if (app.ReadyLog is not { } pattern) return null;
            try
            {
                _ = new System.Text.RegularExpressions.Regex(pattern);
                return pattern;
            }
            catch (ArgumentException e)
            {
                Problem($"'{app.Name}' waits for a log line matching '{pattern}', which isn't a valid regular expression: {e.Message}", app.Location);
                return null;
            }
        }

        RestartModel? PlanRestart(AppModel app)
        {
            if (app.Restart is not { } restart) return null;
            if (restart.Policy is not ("onFailure" or "always"))
                Problem($"'{app.Name}' has an unknown restart policy '{restart.Policy}'", restart.Location);
            else if (restart.Max < 1)
                Problem($"'{app.Name}' restarts at most {restart.Max} times; give it at least 1, or leave restarting out", restart.Location);
            else return restart;
            return null;
        }

        // Apps whose endpoints the app being planned reaches through its values; null outside PlanApp.
        HashSet<string>? _referenced;
        readonly Dictionary<string, HashSet<string>> _referencedBy = new(StringComparer.Ordinal);
        readonly Dictionary<string, SourceLocation> _locations = new(StringComparer.Ordinal);

        /// <summary>
        /// Each app's <see cref="PlannedApp.DependsOn"/>: what it waits for (a loop there is an error), then the apps its
        /// values point at, each added only when it doesn't close a loop. Two services that call each other then start
        /// in declaration order rather than waiting on each other forever.
        /// </summary>
        IReadOnlyList<PlannedApp> Order(List<PlannedApp> apps)
        {
            var edges = apps.ToDictionary(a => a.Key, a => a.DependsOn!.ToList(), StringComparer.Ordinal);
            if (FindLoop(edges) is { } loop)
                Problem($"{string.Join(" → ", loop)} wait for each other, so none of them could start", _locations[loop[0]]);
            else
            {
                foreach (var app in apps)
                    foreach (var target in _referencedBy[app.Key].Where(t => t != app.Key && edges.ContainsKey(t)).Order(StringComparer.Ordinal))
                        if (!edges[app.Key].Contains(target) && !Reaches(edges, target, app.Key)) edges[app.Key].Add(target);
            }
            return apps.Select(a => a with { DependsOn = edges[a.Key] }).ToArray();
        }

        static bool Reaches(Dictionary<string, List<string>> edges, string from, string to)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>([from]);
            while (stack.TryPop(out var node))
            {
                if (node == to) return true;
                if (!seen.Add(node)) continue;
                foreach (var next in edges.GetValueOrDefault(node) ?? []) stack.Push(next);
            }
            return false;
        }

        static List<string>? FindLoop(Dictionary<string, List<string>> edges)
        {
            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            var path = new List<string>();
            foreach (var start in edges.Keys)
                if (Visit(start) is { } loop) return loop;
            return null;

            List<string>? Visit(string node)
            {
                if (state.GetValueOrDefault(node) == 2) return null;
                if (state.GetValueOrDefault(node) == 1) return [.. path.SkipWhile(p => p != node), node];
                state[node] = 1;
                path.Add(node);
                foreach (var next in edges.GetValueOrDefault(node) ?? [])
                    if (Visit(next) is { } loop) return loop;
                path.RemoveAt(path.Count - 1);
                state[node] = 2;
                return null;
            }
        }

        PlannedContainer PlanContainer(Scope scope, ContainerModel c)
        {
            var mappings = Mappings(scope.Resources[c.Name], c.Endpoints);
            return new PlannedContainer(scope.Owner, c.Name, c.Image,
                mappings.Select(m => m.ToString()).ToArray(),
                c.Env.Select(e => new PlannedEnv(e.Key, Resolve(scope, e.Value, e.Location), null, e.Location)).ToArray(),
                c.Volumes.Select(v => $"{v.Name} → {v.Target}").ToArray(),
                mappings, c.Volumes, c.Health, c.Role);
        }

        PlannedCompose PlanCompose(Scope scope, ComposeFileModel file)
        {
            var overrides = new List<PlannedOverride>();
            var services = new List<PlannedComposeService>();
            foreach (var service in file.Services)
            {
                var mappings = Mappings(scope.Resources[service.Name], service.Endpoints);
                services.Add(new PlannedComposeService(service.Name, mappings, service.Health, service.Role));
                if (mappings.Count == 0) continue;
                var value = string.Join(", ", mappings);
                overrides.Add(new PlannedOverride($"services.{service.Name}.ports",
                    new PlannedValue(value, value, [$"ports for {service.Name}, replacing the file's own"]), Generated: true));
            }
            foreach (var o in file.Overrides)
                overrides.Add(new PlannedOverride(o.Path, Resolve(scope, o.Value, o.Location)));

            CheckComposeFile(scope, file, overrides);
            return new PlannedCompose(scope.Owner, scope.RepoPath!, file.File, overrides, services);
        }

        /// <summary>A workspace keeps only the services the repo names: the others would start on the file's fixed host
        /// ports and clash with the same service in another workspace.</summary>
        void CheckComposeFile(Scope scope, ComposeFileModel file, List<PlannedOverride> overrides)
        {
            if (input.ComposeFiles?.TryGetValue(ComposeFileInfo.Key(scope.RepoPath!, file.File), out var info) != true) return;

            foreach (var service in file.Services.Where(s => !info!.Services.Contains(s.Name)))
                Problem($"{file.File} has no service named '{service.Name}'", service.Location);

            var named = file.Services.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            var left = info!.Services.Where(s => !named.Contains(s) && !info.Profiled.Contains(s)).ToArray();
            if (left.Length > 0)
            {
                var (them, they) = left.Length == 1 ? ("it", "it") : ("them", "them");
                Warn($"{scope.Owner}'s {file.File} also has {string.Join(", ", left.Select(s => $"'{s}'"))}, which its sprig.cs doesn't name, " +
                     $"so workspaces leave {them} out. Name {they} with compose.Service(...) to keep {them}.", file.Location);
            }

            foreach (var name in info.FixedNames.Where(named.Contains))
            {
                var path = $"services.{name}.container_name";
                if (overrides.Any(o => o.Path == path)) continue;
                overrides.Add(new PlannedOverride(path,
                    new PlannedValue("", "(removed)", ["a fixed container name would clash between workspaces"]), Generated: true));
            }
        }

        static IReadOnlyList<PlannedMapping> Mappings(Resource resource, IReadOnlyList<EndpointModel> endpoints)
            => endpoints.Where(e => e.TargetPort is not null)
                .Select(e => new PlannedMapping(e.Name, resource.Endpoints[e.Name].Port, e.TargetPort!.Value))
                .ToArray();

        /// <summary>
        /// The repos' groups, those of the same name joined, so "client" starts every repo's client apps together; then
        /// the orchestration's, each replacing a repos' group of its name, since putting repos together is its job.
        /// </summary>
        IReadOnlyList<PlannedGroup> PlanGroups()
        {
            var groups = new List<(string Name, List<string> Apps)>();
            foreach (var scope in _scopes)
            {
                if (scope.Repo is not { Groups: { } repoGroups } repo) continue;
                foreach (var group in repoGroups)
                {
                    if (group.Name.Trim().Length == 0)
                    {
                        Problem($"{repo.Name} has a group with no name", group.Location);
                        continue;
                    }
                    if (groups.FindIndex(g => g.Name == group.Name) is var at && at < 0)
                    {
                        groups.Add((group.Name, []));
                        at = groups.Count - 1;
                    }
                    foreach (var name in group.Apps)
                    {
                        var key = $"{repo.Name}/{name}";
                        if (!repo.Apps.Any(a => a.Name == name))
                            Problem($"group '{group.Name}' names '{name}', which {repo.Name} doesn't declare as an app or job (infra always comes up with the workspace)", group.Location);
                        else if (!groups[at].Apps.Contains(key)) groups[at].Apps.Add(key);
                    }
                }
            }
            foreach (var group in input.Orchestration.Groups)
            {
                var planned = PlanGroup(group);
                var at = groups.FindIndex(g => g.Name == group.Name);
                if (at >= 0) groups[at] = (group.Name, [.. planned.Apps]);
                else groups.Add((group.Name, [.. planned.Apps]));
            }
            return groups.Select(g => new PlannedGroup(g.Name, g.Apps)).ToArray();
        }

        PlannedGroup PlanGroup(GroupModel group)
        {
            var apps = new List<string>();
            foreach (var member in group.Members)
            {
                var scope = _scopes.FirstOrDefault(s => s.RepoPath == member.Repo);
                if (scope?.Repo is not { } repo)
                {
                    Problem($"group '{group.Name}' names '{member.Repo}', which isn't a repo in this orchestration", group.Location);
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
            var settings = _scopes.Where(s => s.Repo is not null).SelectMany(s => s.Repo!.Settings.Select(i => i.Name)).ToHashSet();
            foreach (var binding in input.Orchestration.Bindings.Where(b => !settings.Contains(b.Setting)))
                Warn($"the orchestration binds '{binding.Setting}', but no repo has that setting", binding.Location);
        }

        // ---- resolution ----

        /// <summary>A reference filled in: its text, how it was reached for people to read, and what it reached.</summary>
        readonly record struct Resolved(string Value, IReadOnlyList<string> Via, IReadOnlyList<ValueSource> Sources);

        PlannedValue Resolve(Scope scope, string template, SourceLocation location)
        {
            IReadOnlyList<TemplatePart> parts;
            try { parts = TemplateText.Parse(template); }
            catch (FormatException e) { Problem(e.Message, location); return new PlannedValue(template, null, [], []); }

            var value = new System.Text.StringBuilder();
            var via = new List<string>();
            var sources = new List<ValueSource>();
            var ok = true;
            foreach (var part in parts)
            {
                if (part is TextPart text) { value.Append(text.Text); continue; }
                var path = ((RefPart)part).Path;
                var resolved = ResolveRef(scope, path, location, []);
                if (resolved is null)
                {
                    ok = false;
                    if (path.StartsWith(SettingPrefix, StringComparison.Ordinal) && Unfilled(scope, path[SettingPrefix.Length..]))
                        sources.Add(new ValueSource(null, null, null, path[SettingPrefix.Length..]));
                    continue;
                }
                value.Append(resolved.Value.Value);
                via.AddRange(resolved.Value.Via);
                sources.AddRange(resolved.Value.Sources);
            }
            return new PlannedValue(template, ok ? value.ToString() : null, via, sources.Distinct().ToArray());
        }

        const string SettingPrefix = "setting.";

        /// <summary>Nothing gives the setting a value: not the workspace, the orchestration, another repo's export or
        /// the repo's default.</summary>
        bool Unfilled(Scope scope, string name)
            => scope.Repo is not null
               && scope.Repo.Settings.FirstOrDefault(i => i.Name == name)?.Default is null
               && !FilledElsewhere(scope.RepoPath!, name);

        /// <summary>The workspace sets it, the orchestration binds it, or another repo exports it.</summary>
        bool FilledElsewhere(string repoPath, string name)
            => input.Values?.ContainsKey(name) == true
               || input.Orchestration.Bindings.Any(b => b.Setting == name)
               || input.Repos.Any(r => r.Key != repoPath && r.Value.Exports.Any(e => e.Name == name));

        /// <summary>
        /// Containers of <paramref name="repo"/> that only its defaults use, where something else gives every setting
        /// those defaults are for. A repo can bring its own mail catcher for when it runs alone without a workspace of an
        /// orchestration that brings one running two.
        /// </summary>
        HashSet<string> UnneededFallbackInfra(string repoPath, RepoModel repo)
        {
            var fallbacks = repo.Settings.Where(i => i.Default is not null).ToArray();
            if (fallbacks.Length == 0) return [];

            var usedElsewhere = new HashSet<string>(StringComparer.Ordinal);
            void Uses(string? template)
            {
                if (template is not null) usedElsewhere.UnionWith(ResourcesIn(template));
            }
            foreach (var app in repo.Apps)
            {
                foreach (var env in app.Env) Uses(env.Value);
                Uses(app.Run);
                foreach (var link in app.Links ?? []) Uses(link.Url);
                usedElsewhere.UnionWith(app.WaitFor);
            }
            foreach (var c in repo.Containers)
                foreach (var env in c.Env) Uses(env.Value);
            foreach (var export in repo.Exports) Uses(export.Value);
            foreach (var file in repo.ComposeFiles)
                foreach (var o in file.Overrides) Uses(o.Value);

            var needed = new HashSet<string>(StringComparer.Ordinal);
            var fallbackOnly = new HashSet<string>(StringComparer.Ordinal);
            foreach (var setting in fallbacks)
            {
                var named = ResourcesIn(setting.Default!).Where(r => repo.Containers.Any(c => c.Name == r) && !usedElsewhere.Contains(r));
                if (FilledElsewhere(repoPath, setting.Name)) fallbackOnly.UnionWith(named);
                else needed.UnionWith(named);
            }
            fallbackOnly.ExceptWith(needed);
            return fallbackOnly;
        }

        /// <summary>The resources a template names, like <c>mail</c> in <c>{mail.smtp.url}</c>.</summary>
        static IEnumerable<string> ResourcesIn(string template)
        {
            IReadOnlyList<TemplatePart> parts;
            try { parts = TemplateText.Parse(template); }
            catch (FormatException) { return []; }
            return parts.OfType<RefPart>()
                .Select(p => p.Path)
                .Where(p => !p.StartsWith(SettingPrefix, StringComparison.Ordinal) && !p.StartsWith("export.", StringComparison.Ordinal) && !p.StartsWith("workspace.", StringComparison.Ordinal))
                .Select(p => p.Split('.')[0]);
        }

        /// <summary>What crossed a setting is labelled with it, as the asking app names it. A literal value still came
        /// from somewhere: the repo, orchestration or workspace that supplied it.</summary>
        static IReadOnlyList<ValueSource> Crossed(IReadOnlyList<ValueSource> sources, string owner, string setting)
            => sources.Count == 0 ? [new ValueSource(owner, null, null, setting)] : sources.Select(s => s with { Setting = setting }).ToArray();

        Resolved? ResolveRef(Scope scope, string path, SourceLocation location, HashSet<string> visiting)
        {
            var key = scope.Owner + "|" + path;
            if (!visiting.Add(key))
            {
                Problem($"'{path}' refers back to itself through a setting or export", location);
                return null;
            }

            try
            {
                if (path == "workspace.name") return new Resolved(input.Workspace, ["workspace name"], []);
                if (path.StartsWith(SettingPrefix, StringComparison.Ordinal)) return ResolveSetting(scope, path[SettingPrefix.Length..], location, visiting);
                if (path.StartsWith("export.", StringComparison.Ordinal)) return ResolveNamedExport(scope, path["export.".Length..], location, visiting);
                return ResolveEndpoint(scope, path, location);
            }
            finally
            {
                visiting.Remove(key);
            }
        }

        Resolved? ResolveEndpoint(Scope scope, string path, SourceLocation location)
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
                        (scope.Repo is not null ? "; to use something from another repo, make it a setting" : ""), location);
                return null;
            }
            if (!resource.Endpoints.TryGetValue(endpointName, out var endpoint))
            {
                Problem($"'{resourceName}' has no endpoint named '{endpointName}'", location);
                return null;
            }
            if (endpoint.Port is not { } port) return null; // allocation already reported why
            if (resource.Kind == "app") _referenced?.Add($"{scope.Owner}/{resourceName}");

            var label = $"{resourceName}.{endpointName} port {port}";
            ValueSource[] reached = [new(scope.Owner, resourceName, endpointName, null)];
            if (what == "port") return new Resolved(port.ToString(), [label], reached);
            if (endpoint.Model.Scheme is not { } scheme)
            {
                Problem($"'{resourceName}.{endpointName}' has no url (give the endpoint a scheme)", location);
                return null;
            }
            return new Resolved($"{scheme}://localhost:{port}", [label], reached);
        }

        /// <summary>
        /// A setting's value, from the first of: the workspace, the orchestration's binding, the one other repo that
        /// exports the name, the repo's default. With none of them it's asked for, which isn't a problem: the workspace
        /// is given a value before it comes up.
        /// </summary>
        Resolved? ResolveSetting(Scope scope, string name, SourceLocation location, HashSet<string> visiting)
        {
            if (scope.Repo is null)
            {
                Problem($"the orchestration has no setting '{name}'; bind it to a value instead", location);
                return null;
            }
            var declared = scope.Repo.Settings.FirstOrDefault(i => i.Name == name);
            var at = declared?.Location ?? location;

            if (input.Values?.TryGetValue(name, out var set) == true)
                return new Resolved(set, [$"setting {name} ← this workspace"], [new ValueSource(ValueSource.Workspace, null, null, name)]);

            var binding = input.Orchestration.Bindings.FirstOrDefault(b => b.Setting == name);
            if (binding is not null)
            {
                var bound = Resolve(_orchestration, binding.Value, binding.Location);
                if (bound.Value is null) return null;
                return new Resolved(bound.Value, [$"setting {name} ← the orchestration", .. bound.Via], Crossed(bound.Sources ?? [], _orchestration.Owner, name));
            }

            var exporters = _scopes
                .Where(s => s.Repo is not null && s != scope)
                .SelectMany(s => s.Repo!.Exports.Where(e => e.Name == name).Select(e => (scope: s, export: e)))
                .ToList();

            if (exporters.Count == 0 && declared?.Default is { } fallback)
            {
                var own = ResolveTemplate(scope, fallback, at, visiting);
                if (own is null) return null;
                return new Resolved(own.Value.Value, [$"setting {name} ← {scope.Owner}'s default", .. own.Value.Via], Crossed(own.Value.Sources, scope.Owner, name));
            }
            if (exporters.Count == 0)
            {
                if (!_asks.Any(a => a.Owner == scope.Owner && a.Name == name))
                    _asks.Add(new PlannedAsk(scope.Owner, name, declared?.Type ?? "text", at));
                return null;
            }
            if (exporters.Count > 1)
            {
                var names = string.Join(" and ", exporters.Select(x => x.scope.Owner));
                Problem($"{names} all export '{name}'; pick one in the orchestration with orchestration.Bind(\"{name}\", <repo>.Export(\"{name}\"))", at);
                return null;
            }

            var (owner, export) = exporters[0];
            if (declared is not null && declared.Type != export.Type)
            {
                Problem($"{scope.Owner}'s setting '{name}' is a {declared.Type}, but {owner.Owner} exports it as {export.Type}", at);
                return null;
            }
            var value = ResolveTemplate(owner, export.Value, export.Location, visiting);
            if (value is null) return null;
            return new Resolved(value.Value.Value, [$"setting {name} ← {owner.Owner}", .. value.Value.Via], Crossed(value.Value.Sources, owner.Owner, name));
        }

        Resolved? ResolveNamedExport(Scope scope, string rest, SourceLocation location, HashSet<string> visiting)
        {
            var dot = rest.LastIndexOf('.');
            var (repoPath, name) = dot > 0 ? (rest[..dot], rest[(dot + 1)..]) : (rest, "");
            var owner = _scopes.FirstOrDefault(s => s.RepoPath == repoPath);
            if (owner?.Repo is null)
            {
                Problem($"'{repoPath}' isn't a repo in this orchestration", location);
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
            var sources = value.Value.Sources.Count > 0 ? value.Value.Sources : [new ValueSource(owner.Owner, null, null, null)];
            return new Resolved(value.Value.Value, [$"{owner.Owner} export {name}", .. value.Value.Via], sources);
        }

        Resolved? ResolveTemplate(Scope scope, string template, SourceLocation location, HashSet<string> visiting)
        {
            IReadOnlyList<TemplatePart> parts;
            try { parts = TemplateText.Parse(template); }
            catch (FormatException e) { Problem(e.Message, location); return null; }

            var value = new System.Text.StringBuilder();
            var via = new List<string>();
            var sources = new List<ValueSource>();
            foreach (var part in parts)
            {
                if (part is TextPart text) { value.Append(text.Text); continue; }
                var resolved = ResolveRef(scope, ((RefPart)part).Path, location, visiting);
                if (resolved is null) return null;
                value.Append(resolved.Value.Value);
                via.AddRange(resolved.Value.Via);
                sources.AddRange(resolved.Value.Sources);
            }
            return new Resolved(value.ToString(), via, sources);
        }

        IEnumerable<ContainerModel> Containers(Scope scope)
            => scope.Repo?.Containers.Where(c => !scope.LeftOut.Contains(c.Name)) ?? (scope == _orchestration ? input.Orchestration.Containers : []);

        void Problem(string message, SourceLocation? location) => _problems.Add(new PlanProblem(ProblemSeverity.Error, message, location));

        void Warn(string message, SourceLocation? location) => _problems.Add(new PlanProblem(ProblemSeverity.Warning, message, location));
    }
}
