using System.ComponentModel;
using Spectre.Console.Cli;

namespace Sprig.Cli.Commands;

// `sprig settings <sub>` (alias `config`) — the port-allocation policy.

[Description("Show port range and restricted ports")]
public sealed class SettingsShowCommand(CliContext cli) : Command<GlobalSettings>
{
    protected override int Execute(CommandContext context, GlobalSettings s, CancellationToken cancellation)
    {
        var current = cli.Settings.Get();
        // Project just the port-allocation policy — the app-internal fields (changelog flags,
        // completed guides, …) aren't the CLI's contract and would only leak.
        if (s.Json)
        {
            CliOutput.Json(new
            {
                portRangeStart = current.PortRangeStart,
                portRangeEndExclusive = current.PortRangeEndExclusive,
                restrictedPorts = current.RestrictedPorts,
            });
            return 0;
        }
        Console.WriteLine($"port range:       {current.PortRangeStart}..{current.PortRangeEndExclusive} (end exclusive)");
        Console.WriteLine($"restricted ports: {(current.RestrictedPorts.Count == 0 ? "-" : string.Join(", ", current.RestrictedPorts))}");
        return 0;
    }
}

[Description("Update the port range and restricted ports")]
public sealed class SettingsSetCommand(CliContext cli) : Command<SettingsSetCommand.Settings>
{
    public sealed class Settings : GlobalSettings
    {
        [CommandOption("--start <port>")]
        [Description("Port range start (inclusive)")]
        public string? Start { get; set; }

        [CommandOption("--end <port>")]
        [Description("Port range end (exclusive)")]
        public string? End { get; set; }

        [CommandOption("--restrict <ports>")]
        [Description("Add restricted ports (comma-separated or repeated)")]
        public string[] Restrict { get; set; } = [];

        [CommandOption("--unrestrict <ports>")]
        [Description("Remove restricted ports (comma-separated or repeated)")]
        public string[] Unrestrict { get; set; } = [];
    }

    protected override int Execute(CommandContext context, Settings s, CancellationToken cancellation)
    {
        var current = cli.Settings.Get();
        var updated = current.Clone();
        if (s.Start is not null) updated.PortRangeStart = CliFormat.ParsePort(s.Start, "--start");
        if (s.End is not null) updated.PortRangeEndExclusive = CliFormat.ParsePort(s.End, "--end");
        var ports = new SortedSet<int>(updated.RestrictedPorts);
        foreach (var p in CliFormat.SplitList(s.Restrict)) ports.Add(CliFormat.ParsePort(p, "--restrict"));
        foreach (var p in CliFormat.SplitList(s.Unrestrict)) ports.Remove(CliFormat.ParsePort(p, "--unrestrict"));
        updated.RestrictedPorts = ports.ToList();

        cli.Settings.Save(updated); // validates the range and restricted ports
        return CliOutput.Ok(s.Json, "settings updated", new
        {
            ok = true,
            portRangeStart = updated.PortRangeStart,
            portRangeEndExclusive = updated.PortRangeEndExclusive,
            restrictedPorts = updated.RestrictedPorts,
        });
    }
}
