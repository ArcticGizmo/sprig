using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sprig.Model;

namespace Sprig;

/// <summary>Entry point of a repo's <c>sprig.cs</c>.</summary>
public static class Repo
{
    /// <summary>Start describing this repo: its apps, infra, and the values it shares or needs.</summary>
    public static RepoBuilder Describe(string name, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => new(name, new SourceLocation(file, line));
}

public sealed class RepoBuilder
{
    readonly string _name;
    readonly SourceLocation _location;
    readonly List<AppResource> _apps = [];
    readonly List<ContainerResource> _containers = [];
    readonly List<ImportModel> _imports = [];
    readonly List<ExportModel> _exports = [];

    internal RepoBuilder(string name, SourceLocation location)
    {
        _name = name;
        _location = location;
    }

    /// <summary>A .NET app, run with <c>dotnet run</c> from <paramref name="path"/>.</summary>
    public AppResource DotNet(string name, string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => AddApp(name, "dotnet", path, file, line);

    /// <summary>A Node app (Vite, Ionic and the like), run from <paramref name="path"/>.</summary>
    public AppResource Node(string name, string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => AddApp(name, "node", path, file, line);

    /// <summary>A container sprig adds to the instance's compose project.</summary>
    public ContainerResource Container(string name, string image, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var container = new ContainerResource(name, image, new SourceLocation(file, line));
        _containers.Add(container);
        return container;
    }

    /// <summary>Ask the system for a value. It binds to an export of the same name from another repo, or to a
    /// value the system supplies itself.</summary>
    public Import<T> Import<T>(string name, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : ValueType
    {
        _imports.Add(new ImportModel(name, ValueType.NameOf<T>(), new SourceLocation(file, line)));
        return new Import<T>(name);
    }

    /// <summary>Make a value available to other repos in the system under <paramref name="name"/>.</summary>
    public void Export<T>(string name, Expr value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : ValueType
        => _exports.Add(new ExportModel(name, ValueType.NameOf<T>(), value.ToTemplate(), new SourceLocation(file, line)));

    /// <inheritdoc cref="Export{T}(string, Expr, string, int)"/>
    public void Export<T>(string name, [InterpolatedStringHandlerArgument] ExprHandler value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : ValueType
        => Export<T>(name, value.ToExpr(), file, line);

    /// <summary>Hand the description to sprig. Call it last, as <c>return repo.Emit(args);</c>.</summary>
    public int Emit(string[] args)
    {
        var model = Build();
        var json = JsonSerializer.Serialize(model, ModelJsonContext.Default.RepoModel);

        var outIndex = Array.IndexOf(args, "--out");
        if (args.Contains("emit") && outIndex >= 0 && outIndex + 1 < args.Length)
        {
            File.WriteAllText(args[outIndex + 1], json);
            return 0;
        }

        Console.WriteLine(json);
        return 0;
    }

    internal RepoModel Build() => new(
        Schema: 1,
        Kind: "repo",
        Name: _name,
        SdkVersion: SdkVersion,
        Location: _location,
        Apps: _apps.Select(a => a.Build()).ToArray(),
        Containers: _containers.Select(c => c.Build()).ToArray(),
        Imports: _imports.ToArray(),
        Exports: _exports.ToArray());

    AppResource AddApp(string name, string kind, string path, string file, int line)
    {
        var app = new AppResource(name, kind, path, new SourceLocation(file, line));
        _apps.Add(app);
        return app;
    }

    public static string SdkVersion { get; } =
        typeof(RepoBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
