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

/// <summary>
/// Picks host ports for a plan: from the pin when there is one, else from the settings range, never a restricted
/// port, never one already taken in this plan, and never one something on the machine is listening on.
/// </summary>
public sealed class PortAllocator(SprigSettings settings, IReadOnlySet<int> inUse)
{
    readonly HashSet<int> _taken = [];

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
        IEnumerable<int> candidates;
        if (pin is not null)
        {
            try { candidates = PortSet.Parse(pin); }
            catch (FormatException e) { port = 0; error = e.Message; return false; }
        }
        else
        {
            candidates = Enumerable.Range(settings.PortRangeStart, settings.PortRangeEndExclusive - settings.PortRangeStart);
        }

        foreach (var candidate in candidates)
        {
            if (_taken.Contains(candidate) || inUse.Contains(candidate) || settings.RestrictedPorts.Contains(candidate)) continue;
            _taken.Add(candidate);
            port = candidate;
            error = null;
            return true;
        }

        port = 0;
        error = pin is null
            ? $"no free port left in the range {settings.PortRangeStart}-{settings.PortRangeEndExclusive - 1}"
            : $"every port in the pin \"{pin}\" is taken or restricted";
        return false;
    }
}
