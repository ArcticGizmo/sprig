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
    readonly List<SettingModel> _settings = [];
    readonly List<ExportModel> _exports = [];
    readonly List<RepoGroupModel> _groups = [];
    readonly List<WorktreeCopyModel> _copies = [];

    internal RepoBuilder(string name, SourceLocation location) : base(name, location) { }

    /// <summary>A .NET app, run with <c>dotnet run</c> from <paramref name="path"/>.</summary>
    public AppResource DotNet(string name, string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => AddApp(name, "dotnet", path, file, line);

    /// <summary>A Node app (Vite, Ionic and the like), run from <paramref name="path"/> with <c>npm run dev</c>, or
    /// <c>pnpm run dev</c> when it's in a pnpm workspace or says pnpm is its <c>packageManager</c>. Its packages are
    /// installed first when they're missing or the lockfile has changed since.</summary>
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

    /// <summary>
    /// A value the repo takes from outside, and what to use when nothing else gives one, so the repo runs on its own.
    /// A value set on the workspace wins, then an orchestration's binding or another repo's export of the same name.
    /// The default can be the repo's own infra, such as <c>repo.Mailpit("mail").SmtpUrl</c>; infra only a default uses is
    /// left out of a workspace where something else gives the value. Without a default, sprig asks for a value when a
    /// workspace is made.
    /// </summary>
    // One method with an optional default, not two overloads: with a default-less overload, a string default would be
    // taken as the caller file path.
    public Setting<T> Setting<T>(string name, Expr? @default = null, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : ValueType
        => AddSetting<T>(name, @default?.ToTemplate(), file, line);

    /// <inheritdoc cref="Setting{T}(string, Expr?, string, int)"/>
    public Setting<T> Setting<T>(string name, [InterpolatedStringHandlerArgument] ExprHandler @default, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : ValueType
        => AddSetting<T>(name, @default.ToExpr().ToTemplate(), file, line);

    [Obsolete("Import is Setting now, with what to use on its own as a second argument: repo.Setting<Url>(\"name\", default). Leave the default out to be asked for a value when a workspace is made.", error: true)]
    public Setting<T> Import<T>(string name) where T : ValueType => throw new NotSupportedException();

    Setting<T> AddSetting<T>(string name, string? @default, string file, int line) where T : ValueType
    {
        _settings.Add(new SettingModel(name, ValueType.NameOf<T>(), new SourceLocation(file, line), @default));
        return new Setting<T>(name) { Owner = this };
    }

    internal void SetSettingRole(string name, string role)
    {
        for (var i = 0; i < _settings.Count; i++)
            if (_settings[i].Name == name) _settings[i] = _settings[i] with { Role = role };
    }

    /// <summary>Make a value available to other repos in the orchestration under <paramref name="name"/>.</summary>
    public void Export<T>(string name, Expr value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : ValueType
        => _exports.Add(new ExportModel(name, ValueType.NameOf<T>(), value.ToTemplate(), new SourceLocation(file, line)));

    /// <inheritdoc cref="Export{T}(string, Expr, string, int)"/>
    public void Export<T>(string name, [InterpolatedStringHandlerArgument] ExprHandler value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) where T : ValueType
        => Export<T>(name, value.ToExpr(), file, line);

    /// <summary>
    /// A slice of the repo's apps to start on its own, such as only the front ends, to save the machine running the
    /// rest. Starting it starts these apps and everything they wait for, in the usual order. An app can be in any
    /// number of groups; an orchestration's repos' groups of the same name start together.
    /// </summary>
    public RepoBuilder Group(string name, params IResource[] apps)
    {
        _groups.Add(new RepoGroupModel(name, apps.Select(a => a.Name).ToArray(), new SourceLocation(Location.File, 0)));
        return this;
    }

    /// <summary>
    /// A file or folder the repo needs that git doesn't have, such as a <c>.env</c> holding secrets, copied from the
    /// repo's own checkout into each worktree a slot makes. A copy the worktree already has is kept, so a change made
    /// there stays. It must be one the repo ignores, so it can't be committed from a worktree by mistake.
    /// </summary>
    /// <param name="path">Relative to the repo's folder, e.g. <c>backend/src/.env</c>.</param>
    public RepoBuilder CopyIntoWorktrees(string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _copies.Add(new WorktreeCopyModel(path.Replace('\\', '/').Trim('/'), new SourceLocation(file, line)));
        return this;
    }

    internal RepoModel BuildRepo() => (RepoModel)Build();

    private protected override DefinitionModel Build() => new RepoModel(
        Schema: Sdk.Schema,
        Name: Name,
        SdkVersion: Sdk.Version,
        Location: Location,
        Containers: Containers.Select(c => c.Build()).ToArray(),
        Apps: [.. _apps.Select(a => a.Build()), .. _jobs.Select(j => j.Build())],
        ComposeFiles: _compose.Select(c => c.Build()).ToArray(),
        Settings: _settings.ToArray(),
        Exports: _exports.ToArray(),
        Groups: _groups.Count > 0 ? _groups.ToArray() : null,
        WorktreeCopies: _copies.Count > 0 ? _copies.ToArray() : null);

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
