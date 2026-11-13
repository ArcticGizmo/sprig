using System.Net.NetworkInformation;
using Sprig.Engine.Settings;

namespace Sprig.Engine.Planning;

/// <summary>A pin: the ports an endpoint may use, written <c>"8100-8103"</c>, <c>"8100,8200"</c> or a mix.</summary>
public static class PortSet
{
    public static IReadOnlyList<int> Parse(string spec)
    {
        var ports = new List<int>();
        foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                ports.Add(ParsePort(token, spec));
                continue;
            }
            var from = ParsePort(token[..dash], spec);
            var to = ParsePort(token[(dash + 1)..], spec);
            if (to < from) throw new FormatException($"'{token}' in pin \"{spec}\" runs backwards");
            for (var p = from; p <= to; p++) ports.Add(p);
        }
        if (ports.Count == 0) throw new FormatException($"pin \"{spec}\" names no ports");
        return ports.Distinct().ToArray();
    }

    static int ParsePort(string text, string spec)
        => int.TryParse(text.Trim(), out var p) && p is >= SprigSettings.MinPort and <= SprigSettings.MaxPort
            ? p
            : throw new FormatException($"'{text.Trim()}' in pin \"{spec}\" isn't a port number");
}

/// <summary>A workspace that holds a port, as people know it (<c>storefront--2</c>), and whether it's up.</summary>
public sealed record PortHolder(string Name, bool Up);

/// <summary>
/// Picks host ports for a plan: from the pin when there is one, else from the settings range, never a restricted
/// port, never one already taken in this plan, and never one something on the machine is listening on. A port another
/// workspace holds is avoided too, so a workspace that's down comes back on the ports it had, except when the pin
/// names that one port alone: every workspace would want the same one then, so only one that's up keeps it from the rest.
/// </summary>
/// <param name="held">Ports other workspaces hold, and who holds each.</param>
public sealed class PortAllocator(SprigSettings settings, IReadOnlySet<int> inUse, Random? random = null,
    IReadOnlyDictionary<int, PortHolder>? held = null)
{
    readonly HashSet<int> _taken = [];

    /// <summary>Claim a port held from before, if it's still allowed and free. A kept port may lie outside the
    /// current range: moving the range shouldn't move a workspace that's already working.</summary>
    public bool TryKeep(string? pin, int port)
    {
        IReadOnlyList<int>? pinned = null;
        if (pin is not null)
        {
            try { pinned = PortSet.Parse(pin); }
            catch (FormatException) { return false; }
            if (!pinned.Contains(port)) return false;
        }
        if (Why(port, pinned is [_]) is not null) return false;
        _taken.Add(port);
        return true;
    }

    /// <summary>Why <paramref name="port"/> can't be had; null when it can.</summary>
    /// <param name="alone">The pin names this port alone.</param>
    string? Why(int port, bool alone)
    {
        if (_taken.Contains(port)) return $"{port} is taken by another port in this plan";
        if (settings.RestrictedPorts.Contains(port)) return $"{port} is restricted in sprig's settings";
        if (held?.GetValueOrDefault(port) is { } holder && (holder.Up || !alone))
            return $"{port} is held by {holder.Name}, which is {(holder.Up ? "up" : "down and keeps it to come back on")}";
        if (inUse.Contains(port)) return $"something on this machine is listening on {port}";
        return null;
    }

    /// <summary>Ports something on this machine is listening on right now.</summary>
    public static IReadOnlySet<int> Listening()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        }
        catch (NetworkInformationException)
        {
            return new HashSet<int>();
        }
    }

    public bool TryAllocate(string? pin, out int port, out string? error)
    {
        IReadOnlyList<int> candidates;
        if (pin is not null)
        {
            try { candidates = PortSet.Parse(pin); }
            catch (FormatException e) { port = 0; error = e.Message; return false; }
        }
        else
        {
            var count = settings.PortRangeEndExclusive - settings.PortRangeStart;
            var start = random is null || count <= 0 ? 0 : random.Next(count);
            candidates = Enumerable.Range(0, Math.Max(count, 0)).Select(i => settings.PortRangeStart + (start + i) % count).ToArray();
        }

        var alone = pin is not null && candidates.Count == 1;
        foreach (var candidate in candidates)
        {
            if (Why(candidate, alone) is not null) continue;
            _taken.Add(candidate);
            port = candidate;
            error = null;
            return true;
        }

        port = 0;
        error = pin is null ? $"no free port left in the range {settings.PortRangeStart}-{settings.PortRangeEndExclusive - 1}"
            : alone ? Why(candidates[0], alone: true)
            : $"every port in the pin \"{pin}\" is taken or restricted";
        return false;
    }
}
