using System.Runtime.CompilerServices;
using Sprig.Model;

namespace Sprig;

/// <summary>Entry point of an orchestration: the file that puts repos together into something you can run as a workspace.</summary>
public static class Orchestration
{
    public static OrchestrationBuilder Describe(string name, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => new(name, new SourceLocation(file, line));
}

[Obsolete(WorkspaceChoice.Message, error: true)]
public enum PortPolicy { Stable, Random }

static class WorkspaceChoice
{
    public const string Message = "Worktrees are chosen when you make a workspace now (the New workspace form, or "
        + "sprig up --workspace with --in-place / --from), and a workspace keeps its ports until it's removed. Delete this line.";
}

public sealed class OrchestrationBuilder : DefinitionBuilder
{
    readonly List<RepoRefModel> _repos = [];
    readonly List<BindingModel> _bindings = [];
    readonly List<GroupModel> _groups = [];
    readonly List<LinkModel> _links = [];

    internal OrchestrationBuilder(string name, SourceLocation location) : base(name, location) { }

    /// <summary>A repo in this orchestration, by its folder relative to this file. The repo describes itself in its own
    /// <c>sprig.cs</c>.</summary>
    /// <param name="path">The repo's folder, relative to this file or absolute.</param>
    public RepoRef Repo(string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _repos.Add(new RepoRefModel(path, new SourceLocation(file, line)));
        return new RepoRef(path);
    }

    /// <summary>A repo from sprig's Repos list, by its ID: its folder, and its definition, which is kept in the repo or
    /// in sprig as the Repos list says. Keeps folder paths out of the orchestration.</summary>
    public RepoRef UseRepo(string id, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _repos.Add(new RepoRefModel(id, new SourceLocation(file, line), Use: id));
        return new RepoRef(id);
    }

    /// <summary>Give the repos' setting a value: one this orchestration supplies (often from its own infra), or a
    /// particular repo's export when more than one repo exports that name. A value set on a workspace still wins.</summary>
    public OrchestrationBuilder Bind(string setting, Expr value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _bindings.Add(new BindingModel(setting, value.ToTemplate(), new SourceLocation(file, line)));
        return this;
    }

    /// <inheritdoc cref="Bind(string, Expr, string, int)"/>
    public OrchestrationBuilder Bind(string setting, [InterpolatedStringHandlerArgument] ExprHandler value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Bind(setting, value.ToExpr(), file, line);

    // Kept only so code written before the choice moved fails with directions rather than "no such method".
    [Obsolete(WorkspaceChoice.Message, error: true)]
    public OrchestrationBuilder Worktrees(string from = "main") => this;

    [Obsolete(WorkspaceChoice.Message, error: true)]
    public OrchestrationBuilder Ports(PortPolicy policy) => this;

    /// <summary>A slice you can start on its own: these repos (or some of their apps) and whatever they depend on.</summary>
    public OrchestrationBuilder Group(string name, params GroupMember[] members)
    {
        _groups.Add(new GroupModel(name, members.Select(m => new GroupMemberModel(m.Repo, m.Apps)).ToArray(),
            new SourceLocation(Location.File, 0)));
        return this;
    }

    /// <summary>
    /// A quick link for the whole workspace, offered in its header: somewhere to start that isn't one app's, such as a
    /// flow through several, or the orchestration's own infra (<c>mail.UrlOf("ui")</c>). A repo's app is reached through
    /// what the repo exports (<c>web.Export("web-url")</c>), as for a binding.
    /// </summary>
    public OrchestrationBuilder Link(string label, Expr url, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _links.Add(new LinkModel(label, url.ToTemplate(), new SourceLocation(file, line)));
        return this;
    }

    /// <inheritdoc cref="Link(string, Expr, string, int)"/>
    public OrchestrationBuilder Link(string label, [InterpolatedStringHandlerArgument] ExprHandler url, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Link(label, url.ToExpr(), file, line);

    internal OrchestrationModel BuildOrchestration() => (OrchestrationModel)Build();

    private protected override DefinitionModel Build() => new OrchestrationModel(
        Schema: Sdk.Schema,
        Name: Name,
        SdkVersion: Sdk.Version,
        Location: Location,
        Containers: Containers.Select(c => c.Build()).ToArray(),
        Repos: _repos.ToArray(),
        Bindings: _bindings.ToArray(),
        Groups: _groups.ToArray(),
        Links: _links.Count > 0 ? _links.ToArray() : null);
}

/// <summary>A repo in an orchestration.</summary>
public sealed class RepoRef(string path)
{
    public string Path { get; } = path;

    /// <summary>This repo's export named <paramref name="name"/>, to settle which of several repos gives a setting.</summary>
    public Ref Export(string name) => new($"export.{Path}.{name}");

    /// <summary>Only some of this repo's apps, for a group.</summary>
    public GroupMember Only(params string[] apps) => new(Path, apps);

    public static implicit operator GroupMember(RepoRef repo) => new(repo.Path, null);
}

public sealed record GroupMember(string Repo, IReadOnlyList<string>? Apps);
