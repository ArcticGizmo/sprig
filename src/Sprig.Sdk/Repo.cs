using System.Runtime.CompilerServices;
using Sprig.Model;

namespace Sprig;

/// <summary>Entry point of a repo's <c>sprig.cs</c>.</summary>
public static class Repo
{
    /// <summary>Start describing this repo: its apps, infra, and the values it shares or needs.</summary>
    public static RepoBuilder Describe(string name, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => new(name, new SourceLocation(file, line));
}

public sealed class RepoBuilder : DefinitionBuilder
{
    readonly List<AppResource> _apps = [];
    readonly List<JobResource> _jobs = [];
    readonly List<ComposeFileResource> _compose = [];
    readonly List<ImportModel> _imports = [];
    readonly List<ExportModel> _exports = [];

    internal RepoBuilder(string name, SourceLocation location) : base(name, location) { }

    /// <summary>A .NET app, run with <c>dotnet run</c> from <paramref name="path"/>.</summary>
    public AppResource DotNet(string name, string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => AddApp(name, "dotnet", path, file, line);

    /// <summary>A Node app (Vite, Ionic and the like), run from <paramref name="path"/>.</summary>
    public AppResource Node(string name, string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => AddApp(name, "node", path, file, line);

    /// <summary>Any other app. Give it a <see cref="AppResource.Run(Expr)"/> command.</summary>
    public AppResource App(string name, string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => AddApp(name, "app", path, file, line);

    /// <summary>A .NET one-off command (migrations, seeding), run with <c>dotnet run</c> from <paramref name="path"/>.</summary>
    public JobResource DotNetJob(string name, string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => AddJob(name, "dotnet", path, file, line);

    /// <summary>Any other one-off command. Give it a <see cref="JobResource.Run(Expr)"/> command.</summary>
    public JobResource Job(string name, string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => AddJob(name, "app", path, file, line);

    /// <summary>A compose file the repo already has. Name its services to give them sprig-allocated ports, and set
    /// any other value in it with <see cref="ComposeFileResource.Set(string, Expr, string, int)"/>.</summary>
    public ComposeFileResource ComposeFile(string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var compose = new ComposeFileResource(path, new SourceLocation(file, line));
        _compose.Add(compose);
        return compose;
    }

    /// <summary>Ask the trellis for a value. It binds to an export of the same name from another repo, or to a value
    /// the trellis supplies itself.</summary>
    public Import<T> Import<T>(string name, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : ValueType
    {
        _imports.Add(new ImportModel(name, ValueType.NameOf<T>(), new SourceLocation(file, line)));
        return new Import<T>(name);
    }

    /// <summary>Make a value available to other repos in the trellis under <paramref name="name"/>.</summary>
    public void Export<T>(string name, Expr value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : ValueType
        => _exports.Add(new ExportModel(name, ValueType.NameOf<T>(), value.ToTemplate(), new SourceLocation(file, line)));

    /// <inheritdoc cref="Export{T}(string, Expr, string, int)"/>
    public void Export<T>(string name, [InterpolatedStringHandlerArgument] ExprHandler value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : ValueType
        => Export<T>(name, value.ToExpr(), file, line);

    internal RepoModel BuildRepo() => (RepoModel)Build();

    private protected override DefinitionModel Build() => new RepoModel(
        Schema: Sdk.Schema,
        Name: Name,
        SdkVersion: Sdk.Version,
        Location: Location,
        Containers: Containers.Select(c => c.Build()).ToArray(),
        Apps: [.. _apps.Select(a => a.Build()), .. _jobs.Select(j => j.Build())],
        ComposeFiles: _compose.Select(c => c.Build()).ToArray(),
        Imports: _imports.ToArray(),
        Exports: _exports.ToArray());

    AppResource AddApp(string name, string kind, string path, string file, int line)
    {
        var app = new AppResource(name, kind, path, new SourceLocation(file, line));
        _apps.Add(app);
        return app;
    }

    JobResource AddJob(string name, string kind, string path, string file, int line)
    {
        var job = new JobResource(name, kind, path, new SourceLocation(file, line));
        _jobs.Add(job);
        return job;
    }
}
