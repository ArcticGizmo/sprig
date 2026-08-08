using Sprig.Core.Stacks;
using Spectre.Console;

namespace Sprig.Cli;

/// <summary>Presentation helpers shared across commands: the compact strings the human output has always
/// used, the <c>--bind</c> parsing/merging, and the Spectre table builder that the list commands render
/// through. Kept apart from the command classes so the look stays consistent in one place.</summary>
static class CliFormat
{
    public static string Ports(IReadOnlyDictionary<string, int> ports)
        => ports.Count == 0 ? "-" : string.Join(",", ports.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

    public static string Kv(IReadOnlyDictionary<string, string> kv)
        => kv.Count == 0 ? "-" : string.Join("  ", kv.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"));

    /// <summary>A plain (borderless-by-caller) table with dim, upper-cased headers — the shared look for
    /// every list command. Callers add columns and rows, then hand it to the console.</summary>
    public static Table Table(params string[] headers)
    {
        var table = new Table().Border(TableBorder.Rounded);
        foreach (var h in headers)
            table.AddColumn(new TableColumn($"[bold]{h}[/]"));
        return table;
    }

    // Parse "--bind repo:input=expr" args into Bindings[repo][input] = expr.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ParseBindings(IEnumerable<string> binds)
    {
        var raw = new Dictionary<string, Dictionary<string, string>>();
        foreach (var b in binds)
        {
            var colon = b.IndexOf(':');
            var eq = colon < 0 ? -1 : b.IndexOf('=', colon + 1);
            if (colon <= 0 || eq < 0)
                throw new ArgumentException($"--bind must be repo:input=expr, got '{b}'");
            var repo = b[..colon];
            var input = b[(colon + 1)..eq];
            var expr = b[(eq + 1)..];
            if (!raw.TryGetValue(repo, out var d)) raw[repo] = d = new Dictionary<string, string>();
            d[input] = expr;
        }
        return raw.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value);
    }

    // Overlay override bindings onto the existing set: a repo/input present in both takes the new
    // expression; everything else is kept. Removal isn't expressed here — redefine with create instead.
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> MergeBindings(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> existing,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> overrides)
    {
        var merged = existing.ToDictionary(
            kv => kv.Key,
            kv => new Dictionary<string, string>(kv.Value.ToDictionary(x => x.Key, x => x.Value)));
        foreach (var repo in overrides)
        {
            if (!merged.TryGetValue(repo.Key, out var inputs))
                merged[repo.Key] = inputs = new Dictionary<string, string>();
            foreach (var input in repo.Value) inputs[input.Key] = input.Value;
        }
        return merged.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, string>)kv.Value);
    }

    // Human-readable stack dump for `stack show` (the --json path serialises the record instead).
    public static void PrintStack(StackDefinition stack)
    {
        Console.WriteLine($"stack: {stack.Name}");
        Console.WriteLine($"  repos: {string.Join(", ", stack.Repos)}");
        Console.WriteLine($"  ports: {(stack.Ports.Count == 0 ? "-" : string.Join(", ", stack.Ports))}");
        foreach (var repo in stack.Bindings.OrderBy(b => b.Key))
        {
            Console.WriteLine($"  {repo.Key}:");
            foreach (var input in repo.Value.OrderBy(i => i.Key))
                Console.WriteLine($"    {input.Key} = {input.Value}");
        }
        foreach (var share in stack.Shares)
            Console.WriteLine($"  shared port {share.Port}: {string.Join(", ", share.Consumers.Select(c => $"{c.Repo}.{c.Input}"))}");
    }

    public static int ParsePort(string value, string flag)
        => int.TryParse(value, out var n) ? n : throw new ArgumentException($"{flag} must be a number, got '{value}'");

    /// <summary>Split repeated <c>--flag a,b --flag c</c> values into a flat list, honouring both the
    /// comma and repeated-flag forms the old parser accepted.</summary>
    public static List<string> SplitList(IEnumerable<string> values)
        => values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
}
