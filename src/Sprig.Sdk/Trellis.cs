using System.Runtime.CompilerServices;
using Sprig.Model;

namespace Sprig;

/// <summary>Entry point of a trellis: the file that puts repos together into something you can sprout.</summary>
public static class Trellis
{
    public static TrellisBuilder Describe(string name, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => new(name, new SourceLocation(file, line));
}

public enum PortPolicy
{
    /// <summary>Each sprout keeps the ports it was given until it's removed.</summary>
    Stable,
    /// <summary>Fresh ports every time a sprout comes up.</summary>
    Random,
}

public sealed class TrellisBuilder : DefinitionBuilder
{
    readonly List<RepoRefModel> _repos = [];
    readonly List<BindingModel> _bindings = [];
    readonly List<GroupModel> _groups = [];
    readonly List<LinkModel> _links = [];
    WorktreePolicyModel? _worktrees;
    PortPolicy _ports = PortPolicy.Stable;

    internal TrellisBuilder(string name, SourceLocation location) : base(name, location) { }

    /// <summary>A repo in this trellis, by its folder relative to this file. The repo describes itself in its own
    /// <c>sprig.cs</c>.</summary>
    public RepoRef Repo(string path, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        _repos.Add(new RepoRefModel(path, new SourceLocation(file, line)));
        return new RepoRef(path);
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

    /// <summary>Give each sprout its own worktrees, on branches cut from <paramref name="from"/>. Leave this out to run
    /// the repos where they are.</summary>
    public TrellisBuilder Worktrees(string from = "main")
    {
        _worktrees = new WorktreePolicyModel(from);
        return this;
    }

    public TrellisBuilder Ports(PortPolicy policy)
    {
        _ports = policy;
        return this;
    }

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
        Worktrees: _worktrees,
        PortPolicy: _ports.ToString().ToLowerInvariant(),
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
