using System.Runtime.CompilerServices;
using Sprig.Model;

namespace Sprig;

/// <summary>Entry point of a trellis: the file that puts repos together into something you can sprout.</summary>
public static class Trellis
{
    public static TrellisBuilder Describe(string name, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => new(name, new SourceLocation(file, line));
}

[Obsolete(SproutChoice.Message, error: true)]
public enum PortPolicy { Stable, Random }

static class SproutChoice
{
    public const string Message = "Worktrees and ports are chosen when you make a sprout now (the new-sprout form, or "
        + "sprig up --in-place / --from / --random-ports). Delete this line.";
}

public sealed class TrellisBuilder : DefinitionBuilder
{
    readonly List<RepoRefModel> _repos = [];
    readonly List<BindingModel> _bindings = [];
    readonly List<GroupModel> _groups = [];
    readonly List<LinkModel> _links = [];

    internal TrellisBuilder(string name, SourceLocation location) : base(name, location) { }

    /// <summary>A repo in this trellis, by its folder relative to this file. The repo describes itself in its own
    /// <c>sprig.cs</c>.</summary>
    /// <param name="path">The repo's folder, relative to this file or absolute.</param>
    public RepoRef Repo(string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _repos.Add(new RepoRefModel(path, new SourceLocation(file, line)));
        return new RepoRef(path);
    }

    /// <summary>A repo from sprig's Repos list, by its ID: its folder, and its definition, which is kept in the repo or
    /// in sprig as the Repos list says. Keeps folder paths out of the trellis.</summary>
    public RepoRef UseRepo(string id, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _repos.Add(new RepoRefModel(id, new SourceLocation(file, line), Use: id));
        return new RepoRef(id);
    }

    /// <summary>Answer a repo's import with a value: one this trellis supplies (often from its own infra), or a
    /// particular repo's export when more than one repo exports that name.</summary>
    public TrellisBuilder Bind(string import, Expr value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _bindings.Add(new BindingModel(import, value.ToTemplate(), new SourceLocation(file, line)));
        return this;
    }

    /// <inheritdoc cref="Bind(string, Expr, string, int)"/>
    public TrellisBuilder Bind(string import, [InterpolatedStringHandlerArgument] ExprHandler value, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Bind(import, value.ToExpr(), file, line);

    // Kept only so code written before the choice moved fails with directions rather than "no such method".
    [Obsolete(SproutChoice.Message, error: true)]
    public TrellisBuilder Worktrees(string from = "main") => this;

    [Obsolete(SproutChoice.Message, error: true)]
    public TrellisBuilder Ports(PortPolicy policy) => this;

    /// <summary>A slice you can start on its own: these repos (or some of their apps) and whatever they depend on.</summary>
    public TrellisBuilder Group(string name, params GroupMember[] members)
    {
        _groups.Add(new GroupModel(name, members.Select(m => new GroupMemberModel(m.Repo, m.Apps)).ToArray(),
            new SourceLocation(Location.File, 0)));
        return this;
    }

    /// <summary>
    /// A quick link for the whole sprout, offered in its header: somewhere to start that isn't one app's, such as a
    /// flow through several, or the trellis's own infra (<c>mail.UrlOf("ui")</c>). A repo's app is reached through
    /// what the repo exports (<c>web.Export("web-url")</c>), as for a binding.
    /// </summary>
    public TrellisBuilder Link(string label, Expr url, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _links.Add(new LinkModel(label, url.ToTemplate(), new SourceLocation(file, line)));
        return this;
    }

    /// <inheritdoc cref="Link(string, Expr, string, int)"/>
    public TrellisBuilder Link(string label, [InterpolatedStringHandlerArgument] ExprHandler url, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Link(label, url.ToExpr(), file, line);

    internal TrellisModel BuildTrellis() => (TrellisModel)Build();

    private protected override DefinitionModel Build() => new TrellisModel(
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

/// <summary>A repo in a trellis.</summary>
public sealed class RepoRef(string path)
{
    public string Path { get; } = path;

    /// <summary>This repo's export named <paramref name="name"/>, to settle which of several repos answers an import.</summary>
    public Ref Export(string name) => new($"export.{Path}.{name}");

    /// <summary>Only some of this repo's apps, for a group.</summary>
    public GroupMember Only(params string[] apps) => new(Path, apps);

    public static implicit operator GroupMember(RepoRef repo) => new(repo.Path, null);
}

public sealed record GroupMember(string Repo, IReadOnlyList<string>? Apps);
