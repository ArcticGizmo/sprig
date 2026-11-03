using System.Runtime.CompilerServices;
using Sprig.Model;

namespace Sprig;

/// <summary>Something sprig can wait for before starting another app.</summary>
public interface IResource
{
    string Name { get; }
}

public sealed class AppResource : IResource
{
    readonly string _kind;
    readonly string _path;
    readonly SourceLocation _location;
    readonly List<EndpointModel> _endpoints = [];
    readonly List<EnvModel> _env = [];
    readonly List<string> _waitFor = [];
    readonly List<EnvOutputModel> _outputs = [];
    string? _run;
    string? _primaryEndpoint;

    internal AppResource(string name, string kind, string path, SourceLocation location)
    {
        Name = name;
        _kind = kind;
        _path = path;
        _location = location;
    }

    public string Name { get; }

    /// <summary>The port of this app's first HTTP endpoint.</summary>
    public PortRef Port => new(Name, PrimaryEndpoint);

    /// <summary>The url of this app's first HTTP endpoint, e.g. <c>http://localhost:8342</c>.</summary>
    public Ref Url => new($"{Name}.{PrimaryEndpoint}.url");

    /// <summary>The port of a named endpoint.</summary>
    public PortRef PortOf(string endpoint) => new(Name, endpoint);

    /// <summary>The url of a named endpoint.</summary>
    public Ref UrlOf(string endpoint) => new($"{Name}.{endpoint}.url");

    /// <summary>An HTTP endpoint on a port sprig allocates.</summary>
    /// <param name="pin">Restrict the port to a set, e.g. <c>"5170-5173"</c> or <c>"8100,8200"</c>.</param>
    /// <param name="portEnv">An env var to receive the port, e.g. <c>"PORT"</c>.</param>
    /// <param name="urlEnv">An env var to receive the url. For ASP.NET Core use <c>"Urls"</c>: it outranks a <c>urls</c>
    /// setting in appsettings.json, which <c>ASPNETCORE_URLS</c> doesn't.</param>
    public AppResource Http(string name = "http", string? pin = null, string? portEnv = null, string? urlEnv = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var location = new SourceLocation(file, line);
        _endpoints.Add(new EndpointModel(name, null, pin, "http", location));
        _primaryEndpoint ??= name;
        if (portEnv is not null) _env.Add(new EnvModel(portEnv, new PortRef(Name, name).ToTemplate(), null, location));
        if (urlEnv is not null) _env.Add(new EnvModel(urlEnv, UrlOf(name).ToTemplate(), null, location));
        return this;
    }

    /// <summary>Set an env var on the app's process.</summary>
    public AppResource Env(string key, Expr value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _env.Add(new EnvModel(key, value.ToTemplate(), null, new SourceLocation(file, line)));
        return this;
    }

    /// <inheritdoc cref="Env(string, Expr, string, int)"/>
    public AppResource Env(string key, [InterpolatedStringHandlerArgument] ExprHandler value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Env(key, value.ToExpr(), file, line);

    /// <summary>Write values into an env file under the app's path, e.g. <c>.env.local</c> for Vite. The repo's .gitignore
    /// must cover the file, or sprig refuses to write it.</summary>
    public AppResource EnvFile(string fileName, Action<EnvFileBuilder> set)
    {
        var builder = new EnvFileBuilder(fileName);
        set(builder);
        _env.AddRange(builder.Entries);
        return this;
    }

    /// <summary>
    /// Also write the app's env vars (from <see cref="Env(string, Expr, string, int)"/> and its endpoints) into a file, so
    /// it gets them when you run it yourself. Pick a file the app (or its tooling) actually reads, and that the repo's
    /// .gitignore covers: sprig refuses to write a file git tracks or doesn't ignore.
    /// </summary>
    public AppResource Output(EnvOutput output, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _outputs.Add(new EnvOutputModel(output.Kind, output.File, output.Profile, new SourceLocation(file, line)));
        return this;
    }

    /// <summary>The command that starts the app. Defaults to the app kind's usual command.</summary>
    public AppResource Run(Expr command)
    {
        _run = command.ToTemplate();
        return this;
    }

    /// <inheritdoc cref="Run(Expr)"/>
    public AppResource Run([InterpolatedStringHandlerArgument] ExprHandler command) => Run(command.ToExpr());

    /// <summary>Start only once these are ready.</summary>
    public AppResource WaitFor(params IResource[] resources)
    {
        _waitFor.AddRange(resources.Select(r => r.Name));
        return this;
    }

    string PrimaryEndpoint => _primaryEndpoint
        ?? throw new InvalidOperationException($"app '{Name}' has no endpoint yet; call .Http() before using its Port or Url");

    internal AppModel Build() => new(Name, _kind, _path, _location, _endpoints.ToArray(), _env.ToArray(), _run, _waitFor.ToArray(), _outputs.ToArray());
}

/// <summary>Where an app's env vars are written for running it by hand. Paths are relative to the app's path. Each file
/// must be one the app reads and the repo's .gitignore covers.</summary>
public sealed record EnvOutput
{
    internal string Kind { get; }
    internal string File { get; }
    internal string? Profile { get; }

    EnvOutput(string kind, string file, string? profile)
    {
        Kind = kind;
        File = file;
        Profile = profile;
    }

    /// <summary>A <c>KEY=value</c> file. Sprig keeps its values between marker lines and leaves the rest alone. Only
    /// useful for tools that read one (Vite, Next.js and the like read <c>.env.local</c>); .NET doesn't.</summary>
    public static EnvOutput DotEnv(string file = ".env.local") => new("dotenv", file, null);

    /// <summary>A profile in <c>Properties/launchSettings.json</c>, used by <c>dotnet run</c>, Visual Studio, Rider and
    /// VS Code. The profile goes first, so a plain <c>dotnet run</c> picks it. Many repos commit this file; for those,
    /// use <see cref="AppSettings"/> instead.</summary>
    public static EnvOutput LaunchSettings(string profile = "sprig") => new("launchSettings", "Properties/launchSettings.json", profile);

    /// <summary>A JSON configuration file, with <c>Section__Key</c> names nested into sections. The app has to load it
    /// after its other settings (e.g. <c>builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true)</c>),
    /// or the values do nothing; sprig can't check that.</summary>
    public static EnvOutput AppSettings(string file = "appsettings.Local.json") => new("appSettings", file, null);
}

public sealed class EnvFileBuilder
{
    readonly string _file;
    internal List<EnvModel> Entries { get; } = [];

    internal EnvFileBuilder(string file) => _file = file;

    public EnvFileBuilder Set(string key, Expr value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        Entries.Add(new EnvModel(key, value.ToTemplate(), _file, new SourceLocation(file, line)));
        return this;
    }

    /// <inheritdoc cref="Set(string, Expr, string, int)"/>
    public EnvFileBuilder Set(string key, [InterpolatedStringHandlerArgument] ExprHandler value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Set(key, value.ToExpr(), file, line);
}

public class ContainerResource : IResource
{
    readonly string _image;
    readonly SourceLocation _location;
    readonly List<EndpointModel> _endpoints = [];
    readonly List<EnvModel> _env = [];
    readonly List<VolumeModel> _volumes = [];
    string? _health;

    internal ContainerResource(string name, string image, SourceLocation location)
    {
        Name = name;
        _image = image;
        _location = location;
    }

    public string Name { get; }

    /// <summary>The host port mapped to the container's first endpoint.</summary>
    public PortRef Port => new(Name, _endpoints.Count > 0 ? _endpoints[0].Name
        : throw new InvalidOperationException($"container '{Name}' has no endpoint yet; call .Endpoint() first"));

    /// <summary>The host port mapped to a named endpoint.</summary>
    public PortRef PortOf(string endpoint) => new(Name, endpoint);

    /// <summary>Map a host port sprig allocates to <paramref name="targetPort"/> inside the container.</summary>
    /// <param name="scheme">Gives the endpoint a url (e.g. <c>"http"</c> for a web UI).</param>
    public ContainerResource Endpoint(string name, int targetPort, string? pin = null, string? scheme = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _endpoints.Add(new EndpointModel(name, targetPort, pin, scheme, new SourceLocation(file, line)));
        return this;
    }

    public ContainerResource Env(string key, Expr value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _env.Add(new EnvModel(key, value.ToTemplate(), null, new SourceLocation(file, line)));
        return this;
    }

    /// <summary>Keep <paramref name="target"/> in a volume, one per sprout.</summary>
    public ContainerResource Volume(string name, string target)
    {
        _volumes.Add(new VolumeModel(name, target));
        return this;
    }

    /// <summary>A shell command run inside the container that exits 0 once it's ready, e.g. <c>"pg_isready"</c>. Sprig
    /// waits for it before calling the sprout up.</summary>
    public ContainerResource Health(string command)
    {
        _health = command;
        return this;
    }

    internal ContainerModel Build() => new(Name, _image, _location, _endpoints.ToArray(), _env.ToArray(), _volumes.ToArray(), _health);
}

public sealed class ComposeFileResource
{
    readonly string _file;
    readonly SourceLocation _location;
    readonly List<ComposeServiceResource> _services = [];
    readonly List<ComposeOverrideModel> _overrides = [];

    internal ComposeFileResource(string file, SourceLocation location)
    {
        _file = file;
        _location = location;
    }

    /// <summary>A service in the compose file. Give it endpoints to have sprig allocate and map its host ports.</summary>
    public ComposeServiceResource Service(string name, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var service = new ComposeServiceResource(name, new SourceLocation(file, line));
        _services.Add(service);
        return service;
    }

    /// <summary>Set any value in the file, by a dotted path such as <c>services.postgres.container_name</c>.</summary>
    public ComposeFileResource Set(string path, Expr value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _overrides.Add(new ComposeOverrideModel(path, value.ToTemplate(), new SourceLocation(file, line)));
        return this;
    }

    /// <inheritdoc cref="Set(string, Expr, string, int)"/>
    public ComposeFileResource Set(string path, [InterpolatedStringHandlerArgument] ExprHandler value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Set(path, value.ToExpr(), file, line);

    internal ComposeFileModel Build() => new(_file, _location, _services.Select(s => s.Build()).ToArray(), _overrides.ToArray());
}

public sealed class ComposeServiceResource : IResource
{
    readonly SourceLocation _location;
    readonly List<EndpointModel> _endpoints = [];
    string? _health;

    internal ComposeServiceResource(string name, SourceLocation location)
    {
        Name = name;
        _location = location;
    }

    public string Name { get; }

    /// <summary>The host port mapped to the service's first endpoint.</summary>
    public PortRef Port => new(Name, _endpoints.Count > 0 ? _endpoints[0].Name
        : throw new InvalidOperationException($"service '{Name}' has no endpoint yet; call .Endpoint() first"));

    public PortRef PortOf(string endpoint) => new(Name, endpoint);

    /// <summary>Allocate a host port and map it to <paramref name="targetPort"/> in the service's <c>ports</c>.</summary>
    public ComposeServiceResource Endpoint(string name, int targetPort, string? pin = null, string? scheme = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _endpoints.Add(new EndpointModel(name, targetPort, pin, scheme, new SourceLocation(file, line)));
        return this;
    }

    /// <summary>A shell command run inside the service that exits 0 once it's ready, e.g. <c>"pg_isready -h 127.0.0.1"</c>.
    /// Sprig waits for it. Without one (here or in the file), sprig only waits for the service to be running.</summary>
    public ComposeServiceResource Health(string command)
    {
        _health = command;
        return this;
    }

    internal ComposeServiceModel Build() => new(Name, _location, _endpoints.ToArray(), _health);
}
