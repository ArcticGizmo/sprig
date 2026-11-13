using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Shop.Diagnostics;

/// <summary>Finds the host and port in a setting and checks something is listening there. A TCP connect is enough
/// to prove the wiring points at the right place, without a driver for every kind of infra.</summary>
public static partial class Probe
{
    public sealed record Target(string Host, int Port)
    {
        public override string ToString() => $"{Host}:{Port}";
    }

    /// <summary>Understands <c>Host=..;Port=..</c> connection strings, URLs, and plain <c>host:port</c>.</summary>
    public static Target? Find(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var host = HostKey().Match(value);
        var port = PortKey().Match(value);
        if (host.Success)
            return new Target(host.Groups[1].Value, port.Success ? int.Parse(port.Groups[1].Value) : 5432);

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Port > 0 && !string.IsNullOrEmpty(uri.Host))
            return new Target(uri.Host, uri.Port);

        var plain = HostPort().Match(value);
        return plain.Success ? new Target(plain.Groups[1].Value, int.Parse(plain.Groups[2].Value)) : null;
    }

    public static async Task<bool> ReachableAsync(Target target, TimeSpan timeout)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await client.ConnectAsync(target.Host, target.Port, cts.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Hides passwords in connection strings and URLs before a value is shown.</summary>
    public static string Mask(string value)
    {
        var masked = PasswordKey().Replace(value, "$1***");
        return UrlCredentials().Replace(masked, "$1$2:***@");
    }

    [GeneratedRegex(@"(?:^|;)\s*(?:Host|Server)\s*=\s*([^;,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HostKey();

    [GeneratedRegex(@"(?:^|;)\s*Port\s*=\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PortKey();

    [GeneratedRegex(@"^([A-Za-z0-9.\-]+):(\d+)$")]
    private static partial Regex HostPort();

    [GeneratedRegex(@"((?:^|;)\s*Password\s*=\s*)[^;]*", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordKey();

    [GeneratedRegex(@"(\w+://)([^:/@]+):[^@/]*@")]
    private static partial Regex UrlCredentials();
}
