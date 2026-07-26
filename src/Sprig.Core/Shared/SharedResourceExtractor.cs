using System.Text;
using System.Text.RegularExpressions;
using Sprig.Core.Compose;
using Sprig.Core.Config;
using Sprig.Core.Planning;
using YamlDotNet.RepresentationModel;

namespace Sprig.Core.Shared;

/// <summary>One override extraction chose, and — the part that matters — why it chose that layer.</summary>
public sealed record ExtractionChoice(PlanLayer Layer, string Target, string Value, string Why);

/// <summary>
/// What extraction proposes: a resource, the compose fragment to stand it up, the overrides it would
/// inject, and anything it wasn't willing to decide on its own. Nothing is written until the caller says so.
/// </summary>
public sealed record ExtractionProposal(
    SharedResourceDefinition Resource,
    string ComposeFragment,
    string ComposeFragmentFileName,
    IReadOnlyList<ExtractionChoice> Choices,
    IReadOnlyList<string> Warnings)
{
    /// <summary>The host port the fragment publishes on. Leased from the port ledger before saving.</summary>
    public int HostPort { get; init; }
}

/// <summary>
/// Lifts a service out of a repo's compose file into a shared resource.
///
/// <para>Authoring override rules by hand is the fastest way to make a good feature go unused, so this is
/// the primary entry point: pick a service, and sprig reads it, recognises the image, and works out
/// <b>which layer to inject at</b> for every value the repo will need back.</para>
///
/// <para>The rule it follows is "prefer the highest layer that can express the change": override a stack
/// binding where the repo already declares a suitable input, and only reach down into an env template
/// where it doesn't. It reports which it picked and why, because that choice is the one thing about this
/// feature a reader will need explained.</para>
/// </summary>
public static partial class SharedResourceExtractor
{
    /// <summary>
    /// The services in one of a repo's compose files, with whether sprig knows how to pool each one — what
    /// a picker needs to offer a choice rather than a text box.
    /// </summary>
    /// <param name="Poolable">
    /// False for a service built from source: that's the app itself, and pooling your own app across
    /// workspaces would defeat the isolation the workspace exists for.
    /// </param>
    public sealed record ComposeService(string Name, string? Image, bool HasPreset, bool Poolable);

    /// <summary>List the services in a repo's compose file. Empty if the file is missing or unparsable.</summary>
    public static IReadOnlyList<ComposeService> Services(string repoRoot, string composeFile)
    {
        var path = Path.Combine(repoRoot, composeFile);
        if (!File.Exists(path)) return [];

        YamlMappingNode services;
        try
        {
            if (Map(Parse(File.ReadAllText(path)), "services") is not { } found) return [];
            services = found;
        }
        catch (SharedResourceException) { return []; }

        var result = new List<ComposeService>();
        foreach (var (key, value) in services.Children)
        {
            if (key is not YamlScalarNode { Value: { } name } || value is not YamlMappingNode node) continue;
            var image = Scalar(node, "image");
            var built = Value(node, "build") is not null;
            result.Add(new ComposeService(name, image, SharedResourcePreset.For(image) is not null, !built));
        }
        return result;
    }

    /// <summary>Propose a shared resource from <paramref name="service"/> in one of the repo's compose files.</summary>
    /// <exception cref="SharedResourceException">The file or service isn't there.</exception>
    /// <param name="hostPort">
    /// The host port the shared container publishes on. Leave null for a dry run — the caller leases a real
    /// one from the port ledger before saving. <b>Not</b> the service's conventional port: a machine that
    /// already runs postgres has 5432 taken by something sprig can't see, and one shared container binding
    /// a well-known port is exactly the collision this tool exists to avoid.
    /// </param>
    public static ExtractionProposal Propose(SprigRepoConfig repo, string repoRoot, string composeFile,
        string service, string? name = null, int capacity = 5, int? hostPort = null)
    {
        var declared = repo.Compose.FirstOrDefault(c => SamePath(c.File, composeFile))
            ?? throw new SharedResourceException(
                $"repo '{repo.Name}' doesn't declare the compose file '{composeFile}'");

        var path = Path.Combine(repoRoot, declared.File);
        if (!File.Exists(path))
            throw new SharedResourceException($"compose file not found: {path}");

        var doc = Parse(File.ReadAllText(path));
        var services = Map(doc, "services")
            ?? throw new SharedResourceException($"'{declared.File}' has no services");
        var node = Value(services, service) as YamlMappingNode
            ?? throw new SharedResourceException($"'{declared.File}' has no service '{service}'");

        var image = Scalar(node, "image");
        var preset = SharedResourcePreset.For(image);
        var resourceName = name ?? (image is { Length: > 0 } ? SharedResourcePreset.NameFor(image) : service);
        var published = hostPort ?? preset?.DefaultPort ?? 0;

        var choices = new List<ExtractionChoice>();
        var warnings = new List<string>();

        var portInput = PortInput(declared, service);
        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (portInput is not null && preset is not null)
        {
            inputs[portInput] = "${sprig.shared.port}";
            choices.Add(new ExtractionChoice(PlanLayer.Stack, PlanTargets.Input(portInput),
                "${sprig.shared.port}",
                $"'{repo.Name}' already declares the input '{portInput}' for this service's port — the " +
                "cleanest place to override, and it survives the repo renaming things internally."));
        }
        else if (preset is not null)
        {
            warnings.Add(
                $"couldn't tell which input feeds {service}'s host port — no compose override in " +
                $"'{declared.File}' points at services.{service}.ports. Add the input override by hand.");
        }

        var env = NamespaceOverrides(repo, portInput, preset, choices, warnings);

        var resource = new SharedResourceDefinition
        {
            Name = resourceName,
            Capacity = capacity,
            Compose = $"{resourceName}.compose.yml",
            ExecService = preset is null ? null : service,
            Values = Values(preset, published, node),
            Attach = preset?.Attach ?? [],
            Detach = preset?.Detach ?? [],
            Injects =
            [
                new ResourceInjection
                {
                    Repo = repo.Name,
                    Inputs = inputs,
                    Env = env,
                    Suppress = [new InjectedSuppress { File = declared.File, Services = [service] }],
                },
            ],
        };

        choices.Add(new ExtractionChoice(PlanLayer.Repo,
            PlanTargets.ComposeService(declared.File, service),
            $"suppressed — provided by {resourceName}",
            $"'{resourceName}' was lifted out of this service, so starting it per workspace would run the " +
            "same thing twice."));

        if (preset is null)
            warnings.Add(
                $"no preset for image '{image ?? "(none)"}' — sprig can publish its host and port, but you " +
                "'ll need to write the attach/detach commands that carve out each workspace's namespace.");

        return new ExtractionProposal(resource, Fragment(node, doc, service, preset, published),
            $"{resourceName}.compose.yml", choices, warnings) { HostPort = published };
    }

    /// <summary>
    /// The preset's values, with the real host port and the credentials the container will actually
    /// initialise with. The lifted service keeps whatever environment the repo gave it, so asserting a
    /// username the image never creates yields a resource whose own attach command can't log in.
    /// </summary>
    static IReadOnlyDictionary<string, string> Values(SharedResourcePreset? preset, int hostPort,
        YamlMappingNode service)
    {
        if (preset is null) return new Dictionary<string, string> { ["host"] = "localhost" };

        var values = new Dictionary<string, string>(preset.Values, StringComparer.Ordinal);
        if (values.ContainsKey("port")) values["port"] = hostPort.ToString();

        var environment = Environment(service);
        foreach (var (key, candidates) in preset.CredentialsFrom)
            foreach (var candidate in candidates)
                if (environment.TryGetValue(candidate, out var actual) && actual.Length > 0)
                {
                    values[key] = actual;
                    break;
                }

        return values;
    }

    /// <summary>A service's <c>environment</c>, in either the mapping or the <c>KEY=value</c> list form.</summary>
    static Dictionary<string, string> Environment(YamlMappingNode service)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (Value(service, "environment"))
        {
            case YamlMappingNode map:
                foreach (var (key, value) in map.Children)
                    if (key is YamlScalarNode { Value: { } k } && value is YamlScalarNode { Value: { } v })
                        environment[k] = v;
                break;
            case YamlSequenceNode seq:
                foreach (var entry in seq.Children.OfType<YamlScalarNode>())
                    if (entry.Value?.Split('=', 2) is [var k, var v]) environment[k] = v;
                break;
        }
        return environment;
    }

    /// <summary>
    /// The repo input that feeds this service's published port, read from the repo's own compose override
    /// rather than guessed from a name — the repo already said which value goes there.
    /// </summary>
    static string? PortInput(ComposeConfig declared, string service)
    {
        foreach (var over in declared.Overrides)
        {
            if (over.Path.Count < 4) continue;
            if (!string.Equals(over.Path[0], "services", StringComparison.Ordinal)) continue;
            if (!string.Equals(over.Path[1], service, StringComparison.Ordinal)) continue;
            if (!string.Equals(over.Path[2], "ports", StringComparison.Ordinal)) continue;

            var refs = InputRefPattern().Matches(over.Template)
                .Select(m => m.Groups[1].Value)
                .Where(r => !r.StartsWith("ports.", StringComparison.Ordinal) && r != "workspace")
                .ToList();
            if (refs.Count == 1) return refs[0];
        }
        return null;
    }

    /// <summary>
    /// Find the env keys that point at this service and pin a namespace, and propose a rewrite that takes
    /// the namespace from the shared resource instead. This is the step that stops four workspaces quietly
    /// sharing one database, so a key it can't rewrite confidently becomes a warning rather than a guess.
    /// </summary>
    static IReadOnlyList<InjectedEnv> NamespaceOverrides(SprigRepoConfig repo, string? portInput,
        SharedResourcePreset? preset, List<ExtractionChoice> choices, List<string> warnings)
    {
        if (portInput is null || preset is null) return [];

        var files = new List<InjectedEnv>();
        foreach (var file in repo.Env)
        {
            var set = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, template) in file.Set)
            {
                if (!template.Contains($"${{sprig.{portInput}}}", StringComparison.Ordinal)) continue;

                if (Rewrite(template) is { } rewritten)
                {
                    set[key] = rewritten;
                    choices.Add(new ExtractionChoice(PlanLayer.Repo, PlanTargets.EnvKey(file.File, key),
                        rewritten,
                        $"no declared input carries the database name — it's pinned inside this value, so " +
                        "the override reaches one layer deeper into the env template."));
                }
                else
                {
                    warnings.Add(
                        $"'{file.File} → {key}' points at this service but sprig can't see where the " +
                        "database name is. Every workspace on a shared server would use the same one — " +
                        $"edit that value to take it from ${{sprig.shared.database}} before enabling this.");
                }
            }
            if (set.Count > 0) files.Add(new InjectedEnv { File = file.File, Set = set });
        }
        return files;
    }

    /// <summary>
    /// Swap a pinned database name for the shared resource's, in the shapes sprig can recognise without
    /// guessing: an ADO.NET <c>Database=</c>/<c>Initial Catalog=</c> pair, or the path of a connection URL.
    /// Anything else returns null and is reported instead of rewritten.
    /// </summary>
    internal static string? Rewrite(string template)
    {
        // A MatchEvaluator, not a replacement string: '${sprig.shared.database}' looks exactly like a
        // named-group reference to .NET's substitution syntax, and quietly means something else.
        if (AdoDatabasePattern().IsMatch(template))
        {
            var rewritten = AdoDatabasePattern().Replace(template,
                m => $"{m.Groups["key"].Value}=${{sprig.shared.database}}");

            // The credentials come with it. The repo's were whatever its own container was set up with;
            // once it's talking to the shared one, that resource is the authority on how to log in.
            rewritten = AdoUserPattern().Replace(rewritten,
                m => $"{m.Groups["key"].Value}=${{sprig.shared.user}}");
            return AdoPasswordPattern().Replace(rewritten,
                m => $"{m.Groups["key"].Value}=${{sprig.shared.password}}");
        }

        var url = UrlDatabasePattern().Match(template);
        if (url.Success)
            return template[..url.Groups[1].Index] + "${sprig.shared.database}"
                   + template[(url.Groups[1].Index + url.Groups[1].Length)..];

        return null;
    }

    /// <summary>
    /// Build the standalone compose fragment. The service moves over as-is minus the parts that belonged to
    /// the repo — its <c>container_name</c> (it would collide), its <c>depends_on</c> (its dependencies
    /// stayed behind) — and its port is published at the preset's standard number, because a shared
    /// resource has one address rather than one per workspace — leased from sprig's port ledger, not the
    /// service's conventional number, because that one is often already taken by something else.
    /// </summary>
    static string Fragment(YamlMappingNode service, YamlMappingNode doc, string name,
        SharedResourcePreset? preset, int hostPort)
    {
        var lifted = new YamlMappingNode();
        foreach (var (key, value) in service.Children)
        {
            var k = (key as YamlScalarNode)?.Value;
            if (k is "container_name" or "depends_on" or "ports" or "networks") continue;
            lifted.Children.Add(key, value);
        }

        if (preset is not null)
            lifted.Children.Add(new YamlScalarNode("ports"),
                new YamlSequenceNode(new YamlScalarNode($"{hostPort}:{preset.DefaultPort}")));

        var services = new YamlMappingNode();
        services.Children.Add(new YamlScalarNode(name), lifted);

        var root = new YamlMappingNode();
        root.Children.Add(new YamlScalarNode("services"), services);

        // Carry over only the named volumes this service actually uses; they hold its data.
        var used = VolumesUsedBy(service);
        if (used.Count > 0 && Map(doc, "volumes") is { } declaredVolumes)
        {
            var volumes = new YamlMappingNode();
            foreach (var (key, value) in declaredVolumes.Children)
                if ((key as YamlScalarNode)?.Value is { } v && used.Contains(v))
                    volumes.Children.Add(new YamlScalarNode(v), value);
            if (volumes.Children.Count > 0)
                root.Children.Add(new YamlScalarNode("volumes"), volumes);
        }

        var stream = new YamlStream(new YamlDocument(root));
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        stream.Save(writer, assignAnchors: false);
        return sb.ToString();
    }

    static HashSet<string> VolumesUsedBy(YamlMappingNode service)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (Value(service, "volumes") is not YamlSequenceNode seq) return used;

        foreach (var entry in seq.Children)
        {
            if (entry is YamlScalarNode { Value: { Length: > 0 } text })
                used.Add(text.Split(':')[0]);
            else if (entry is YamlMappingNode map && Scalar(map, "source") is { } source)
                used.Add(source);
        }
        return used;
    }

    static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        try { stream.Load(reader); }
        catch (Exception ex) { throw new SharedResourceException($"could not parse compose YAML: {ex.Message}"); }

        return stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root
            ? root
            : throw new SharedResourceException("compose file is empty");
    }

    static YamlMappingNode? Map(YamlMappingNode node, string key) => Value(node, key) as YamlMappingNode;

    static YamlNode? Value(YamlMappingNode node, string key)
        => node.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == key) is { } found
            ? node.Children[found] : null;

    static string? Scalar(YamlMappingNode node, string key) => (Value(node, key) as YamlScalarNode)?.Value;

    static bool SamePath(string a, string b)
        => string.Equals(a.Replace('\\', '/').TrimStart('/'), b.Replace('\\', '/').TrimStart('/'),
            StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\$\{sprig\.([^}]+)\}")]
    private static partial Regex InputRefPattern();

    [GeneratedRegex(@"(?<key>Database|Initial Catalog)=(?<db>[^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AdoDatabasePattern();

    [GeneratedRegex(@"(?<key>Username|User Id|Uid)=(?<user>[^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AdoUserPattern();

    [GeneratedRegex(@"(?<key>Password|Pwd)=(?<pass>[^;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AdoPasswordPattern();

    // scheme://host[:port]/<database> — the final path segment, when it isn't already a template.
    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://[^/\s]+/([A-Za-z0-9_.-]+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlDatabasePattern();
}
