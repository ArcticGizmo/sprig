using System.Text.RegularExpressions;
using Sprig.Core.Settings;
using Sprig.Core.Store;

namespace Sprig.Core.Stacks;

/// <summary>Thrown when a stack can't be defined, found, or imported.</summary>
public sealed class StackException(string message) : Exception(message);

/// <summary>Persists <see cref="StackDefinition"/>s in the central store and handles export/import.</summary>
/// <remarks><paramref name="settings"/> is optional: when supplied, save-time validation also checks a
/// stack's pool ceiling against the configured port range (so an impossible <c>maxSlots</c> is rejected
/// at definition time); when null, that check is skipped.</remarks>
public sealed partial class StackStore(ISprigPaths paths, RepoRegistryStore registry, InstanceStore instances,
    ISettingsStore? settings = null)
{
    public void Save(StackDefinition stack)
    {
        Validate(stack);

        // A stack that live workspaces were built from is frozen: changing its wiring wouldn't touch
        // those already-materialised workspaces, so it would only mislead. Creating a new stack, or
        // re-saving one nothing depends on, is fine. (The desktop app also gates Edit on this.)
        if (File.Exists(FilePath(stack.Name)))
        {
            var users = instances.LoadAll().Count(i => i.Stack == stack.Name);
            if (users > 0)
                throw new StackException(
                    $"can't modify stack '{stack.Name}': {users} workspace{(users == 1 ? "" : "s")} " +
                    "were created from it — remove them first");
        }

        JsonFile.Write(FilePath(stack.Name), stack);
    }

    public StackDefinition? Get(string name)
    {
        var def = JsonFile.Read<StackDefinition>(FilePath(name));
        return def is null ? null : StackMigration.Normalize(def);
    }

    public IReadOnlyList<StackDefinition> List()
    {
        if (!Directory.Exists(paths.StacksDir)) return [];
        return Directory.EnumerateFiles(paths.StacksDir, "*.json")
            .Select(f => JsonFile.Read<StackDefinition>(f))
            .OfType<StackDefinition>()
            .Select(StackMigration.Normalize)
            .OrderBy(s => s.Name)
            .ToList();
    }

    public void Remove(string name)
    {
        var file = FilePath(name);
        if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>Copy a stack's JSON out for sharing; returns the destination path.</summary>
    public string Export(string name, string destPath)
    {
        var stack = Get(name) ?? throw new StackException($"unknown stack '{name}'");
        JsonFile.Write(destPath, stack);
        return destPath;
    }

    /// <summary>Read a stack JSON from a file, migrate + validate against the registry, and save it.</summary>
    public StackDefinition Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new StackException($"stack file not found: {sourcePath}");
        var raw = JsonFile.Read<StackDefinition>(sourcePath)
            ?? throw new StackException($"could not read stack from {sourcePath}");
        // Upgrade an older exported file to the current schema on the way in, so its shares are
        // explicit on disk from here on rather than re-derived every load.
        var stack = StackMigration.Normalize(raw);
        Save(stack);
        return stack;
    }

    void Validate(StackDefinition stack)
    {
        if (string.IsNullOrWhiteSpace(stack.Name) || !NamePattern().IsMatch(stack.Name))
            throw new StackException($"invalid stack name '{stack.Name}' (use letters, digits, '.', '-', '_')");
        if (stack.Repos.Count == 0)
            throw new StackException("a stack must reference at least one repo");
        if (stack.MaxSlots < 1)
            throw new StackException($"stack '{stack.Name}' needs a pool size of at least 1 (maxSlots was {stack.MaxSlots})");

        // Stacks reference repos by name, so an imported stack only saves once every repo it names is
        // registered on this machine. Report all the missing ones at once, so the fix is a single pass.
        var unknown = stack.Repos.Where(repo => registry.Get(repo) is null).ToList();
        if (unknown.Count > 0)
            throw new StackException(
                $"stack '{stack.Name}' references unregistered repo{(unknown.Count == 1 ? "" : "s")} " +
                $"{string.Join(", ", unknown.Select(r => $"'{r}'"))} — register {(unknown.Count == 1 ? "it" : "them")} first");

        // Stack-supplied setup can only target repos the stack actually includes.
        var stackRepos = new HashSet<string>(stack.Repos, StringComparer.Ordinal);
        var setupUnknown = stack.Setup.Keys.Where(r => !stackRepos.Contains(r)).ToList();
        if (setupUnknown.Count > 0)
            throw new StackException(
                $"stack '{stack.Name}' has setup for repo{(setupUnknown.Count == 1 ? "" : "s")} " +
                $"{string.Join(", ", setupUnknown.Select(r => $"'{r}'"))} that the stack doesn't include");

        ValidateShares(stack);
        ValidateOwners(stack);
        ValidateCapacity(stack);
    }

    /// <summary>
    /// Reject a pool ceiling the machine can't physically honour: a full pool needs
    /// <c>maxSlots × (ports per workspace)</c> distinct host ports, so if that exceeds the whole
    /// configured range there's no size at which the pool could run. A sanity gate at definition time
    /// (the range is shared across stacks, so this isn't a reservation) — skipped when no settings
    /// source was supplied, or when the stack owns no ports.
    /// </summary>
    void ValidateCapacity(StackDefinition stack)
    {
        if (settings is null || stack.Ports.Count == 0) return;

        var s = settings.Get();
        var restrictedInRange = s.RestrictedPorts.Count(p => p >= s.PortRangeStart && p < s.PortRangeEndExclusive);
        var capacity = s.PortRangeEndExclusive - s.PortRangeStart - restrictedInRange;
        var need = stack.MaxSlots * stack.Ports.Count;
        if (need > capacity)
            throw new StackException(
                $"stack '{stack.Name}' can't fit: a full pool of {stack.MaxSlots} needs " +
                $"{stack.MaxSlots} × {stack.Ports.Count} = {need} ports, but the configured range " +
                $"{s.PortRangeStart}-{s.PortRangeEndExclusive - 1} only offers {capacity}. " +
                "Lower maxSlots, reduce the stack's ports, or widen the range in settings.");
    }

    /// <summary>
    /// The explicit <see cref="StackDefinition.Shares"/> must stay consistent with the bindings that
    /// actually feed resolution: each shared port is declared, each consumer is a stack repo, and
    /// that consumer's binding references the shared port. This is the invariant that lets the rest
    /// of the app trust <c>Shares</c> without re-deriving it.
    /// </summary>
    static void ValidateShares(StackDefinition stack)
    {
        var ports = new HashSet<string>(stack.Ports, StringComparer.Ordinal);
        var repos = new HashSet<string>(stack.Repos, StringComparer.Ordinal);

        foreach (var share in stack.Shares)
        {
            if (!ports.Contains(share.Port))
                throw new StackException(
                    $"stack '{stack.Name}' shares port '{share.Port}', but no such port is declared");

            foreach (var c in share.Consumers)
            {
                if (!repos.Contains(c.Repo))
                    throw new StackException(
                        $"stack '{stack.Name}' shares port '{share.Port}' with repo '{c.Repo}', " +
                        "which the stack doesn't include");

                var expr = stack.Bindings.TryGetValue(c.Repo, out var b)
                    && b.TryGetValue(c.Input, out var e) ? e : null;
                if (expr is null || !PortExpressions.ReferencedPorts(expr).Contains(share.Port))
                    throw new StackException(
                        $"stack '{stack.Name}' shares port '{share.Port}' with {c.Repo}.{c.Input}, " +
                        $"but that input's binding doesn't reference ${{sprig.ports.{share.Port}}}");
            }
        }
    }

    /// <summary>
    /// The explicit <see cref="StackDefinition.Owners"/> overlay must name real things: each owned port
    /// is declared, its owning repo is in the stack, and no port is owned twice. Like
    /// <see cref="ValidateShares"/> this keeps the overlay trustworthy without re-deriving it — but note
    /// ownership is a pure view hint, so (unlike shares) it deliberately does <b>not</b> require the
    /// owner to bind the port: a repo can serve on a port it never itself consumes.
    /// </summary>
    static void ValidateOwners(StackDefinition stack)
    {
        var ports = new HashSet<string>(stack.Ports, StringComparer.Ordinal);
        var repos = new HashSet<string>(stack.Repos, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var owner in stack.Owners)
        {
            if (!ports.Contains(owner.Port))
                throw new StackException(
                    $"stack '{stack.Name}' assigns an owner to port '{owner.Port}', but no such port is declared");
            if (!repos.Contains(owner.Repo))
                throw new StackException(
                    $"stack '{stack.Name}' says repo '{owner.Repo}' owns port '{owner.Port}', " +
                    "which the stack doesn't include");
            if (!seen.Add(owner.Port))
                throw new StackException(
                    $"stack '{stack.Name}' assigns port '{owner.Port}' more than one owner");
        }
    }

    string FilePath(string name) => Path.Combine(paths.StacksDir, name + ".json");

    // Stack names allow '+' (e.g. "web+api"); they are filenames, not git branches.
    [GeneratedRegex(@"^[A-Za-z0-9._+-]+$")]
    private static partial Regex NamePattern();
}
