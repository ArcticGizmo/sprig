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

    /// <summary>An HTTP endpoint on a port sprig allocates.</summary>
    /// <param name="pin">Restrict the port to a set, e.g. <c>"5170-5173"</c> or <c>"8100,8200"</c>.</param>
    public AppResource Http(string name = "http", string? pin = null, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _endpoints.Add(new EndpointModel(name, null, pin, new SourceLocation(file, line)));
        _primaryEndpoint ??= name;
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

    /// <summary>Write values into an env file under the app's path, e.g. <c>.env.local</c> for Vite.</summary>
    public AppResource EnvFile(string fileName, Action<EnvFileBuilder> set)
    {
        var builder = new EnvFileBuilder(fileName);
        set(builder);
        _env.AddRange(builder.Entries);
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

    internal AppModel Build() => new(Name, _kind, _path, _location, _endpoints.ToArray(), _env.ToArray(), _run, _waitFor.ToArray());
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

public sealed class ContainerResource : IResource
{
    readonly string _image;
    readonly SourceLocation _location;
    readonly List<EndpointModel> _endpoints = [];
    readonly List<EnvModel> _env = [];

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

    /// <summary>Map a host port sprig allocates to <paramref name="targetPort"/> inside the container.</summary>
    public ContainerResource Endpoint(string name, int targetPort, string? pin = null, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _endpoints.Add(new EndpointModel(name, targetPort, pin, new SourceLocation(file, line)));
        return this;
    }

    public ContainerResource Env(string key, Expr value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _env.Add(new EnvModel(key, value.ToTemplate(), null, new SourceLocation(file, line)));
        return this;
    }

    internal ContainerModel Build() => new(Name, _image, _location, _endpoints.ToArray(), _env.ToArray());
}
