using YamlDotNet.RepresentationModel;

namespace Sprig.Core.Compose;

/// <summary>
/// Removes the services a shared resource provides from a generated compose document — and, just as
/// importantly, the references that would dangle without them.
///
/// <para>Deleting <c>services.postgres</c> on its own produces a file docker refuses to bring up, because
/// something still declares <c>depends_on: [postgres]</c>. So pruning is a package: drop the service, drop
/// it from every <c>depends_on</c> (list and map forms alike), and drop the volumes and networks nothing
/// references any more. Doing less would trade a duplicated container for a broken one.</para>
///
/// <para>With nothing to suppress this is a no-op, so a repo that doesn't pool anything gets byte-identical
/// output to before the feature existed.</para>
/// </summary>
internal static class ComposePruner
{
    /// <summary>Prune <paramref name="suppress"/> out of the document; returns how many services survive.</summary>
    /// <exception cref="ComposeException">The file has no <c>services</c>, or no service by that name.</exception>
    public static int Prune(YamlNode root, IReadOnlyList<string>? suppress, string fileLabel)
    {
        if (root is not YamlMappingNode doc || FindMap(doc, "services") is not { } services)
        {
            // No services section at all. Nothing to suppress against, and nothing to count.
            if (suppress is { Count: > 0 })
                throw new ComposeException(
                    $"compose file '{fileLabel}' has no services, so there is nothing to suppress in it");
            return 1;   // not our business to call this file empty; leave it as the caller found it
        }

        if (suppress is not { Count: > 0 }) return services.Children.Count;

        // What the file referenced before we touched it. Anything still referenced afterwards stays, and
        // — just as importantly — anything that was *already* unreferenced stays too. Suppression removes
        // what it orphaned; it is not a licence to tidy up the rest of somebody's compose file.
        var volumesBefore = Collect(services, CollectVolumes);
        var networksBefore = Collect(services, CollectNetworks);

        foreach (var name in suppress)
        {
            var key = FindKey(services, name)
                ?? throw new ComposeException(
                    $"compose file '{fileLabel}' has no service '{name}' to suppress — it may have been " +
                    "renamed or removed since the shared resource was created");
            services.Children.Remove(key);
        }

        var removed = new HashSet<string>(suppress, StringComparer.Ordinal);
        foreach (var service in Services(services))
            PruneDependsOn(service, removed);

        DropOrphaned(doc, "volumes", volumesBefore, Collect(services, CollectVolumes));
        DropOrphaned(doc, "networks", networksBefore, Collect(services, CollectNetworks));

        return services.Children.Count;
    }

    /// <summary>Drop suppressed names from a service's <c>depends_on</c>, and the key itself if it empties.</summary>
    static void PruneDependsOn(YamlMappingNode service, HashSet<string> removed)
    {
        if (FindKey(service, "depends_on") is not { } key) return;

        switch (service.Children[key])
        {
            // depends_on: [postgres, redis]
            case YamlSequenceNode seq:
                foreach (var entry in seq.Children.OfType<YamlScalarNode>()
                             .Where(s => s.Value is { } v && removed.Contains(v)).ToList())
                    seq.Children.Remove(entry);
                if (seq.Children.Count == 0) service.Children.Remove(key);
                break;

            // depends_on: { postgres: { condition: service_healthy } }
            case YamlMappingNode map:
                foreach (var entry in map.Children.Keys.OfType<YamlScalarNode>()
                             .Where(s => s.Value is { } v && removed.Contains(v)).ToList())
                    map.Children.Remove(entry);
                if (map.Children.Count == 0) service.Children.Remove(key);
                break;
        }
    }

    /// <summary>
    /// Drop top-level entries in <paramref name="section"/> that suppression orphaned — referenced
    /// <paramref name="before"/> but not <paramref name="after"/>. Entries that were unreferenced all
    /// along are left exactly where the repo put them.
    /// </summary>
    static void DropOrphaned(YamlMappingNode doc, string section,
        HashSet<string> before, HashSet<string> after)
    {
        if (FindKey(doc, section) is not { } key || doc.Children[key] is not YamlMappingNode declared) return;

        foreach (var entry in declared.Children.Keys.OfType<YamlScalarNode>()
                     .Where(k => k.Value is { } v && before.Contains(v) && !after.Contains(v)).ToList())
            declared.Children.Remove(entry);

        if (declared.Children.Count == 0) doc.Children.Remove(key);
    }

    static HashSet<string> Collect(YamlMappingNode services, Action<YamlMappingNode, HashSet<string>> collect)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in Services(services))
            collect(service, used);
        return used;
    }

    static void CollectVolumes(YamlMappingNode service, HashSet<string> into)
    {
        if (FindKey(service, "volumes") is not { } key
            || service.Children[key] is not YamlSequenceNode seq) return;

        foreach (var entry in seq.Children)
        {
            switch (entry)
            {
                // Short form "pgdata:/var/lib/postgresql/data". Bind mounts land here too, but their
                // left-hand side is a path, which won't collide with a declared volume name in practice.
                case YamlScalarNode s when s.Value is { Length: > 0 } v:
                    into.Add(v.Split(':')[0]);
                    break;

                // Long form { type: volume, source: pgdata, target: ... }
                case YamlMappingNode m when FindKey(m, "source") is { } sk
                                            && m.Children[sk] is YamlScalarNode { Value: { } name }:
                    into.Add(name);
                    break;
            }
        }
    }

    static void CollectNetworks(YamlMappingNode service, HashSet<string> into)
    {
        if (FindKey(service, "networks") is not { } key) return;

        switch (service.Children[key])
        {
            case YamlSequenceNode seq:
                foreach (var s in seq.Children.OfType<YamlScalarNode>())
                    if (s.Value is { } v) into.Add(v);
                break;
            case YamlMappingNode map:
                foreach (var k in map.Children.Keys.OfType<YamlScalarNode>())
                    if (k.Value is { } v) into.Add(v);
                break;
        }
    }

    static IEnumerable<YamlMappingNode> Services(YamlMappingNode services)
        => services.Children.Values.OfType<YamlMappingNode>();

    static YamlMappingNode? FindMap(YamlMappingNode map, string key)
        => FindKey(map, key) is { } k && map.Children[k] is YamlMappingNode value ? value : null;

    static YamlScalarNode? FindKey(YamlMappingNode map, string key)
        => map.Children.Keys.OfType<YamlScalarNode>().FirstOrDefault(k => k.Value == key);
}
