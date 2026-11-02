namespace Sprig.Core.Maps;

/// <summary>How one need would be met by a checkout of the whole map.</summary>
public enum NeedState
{
    /// <summary>Exactly one provider wins (a same-repo provider first, else the only one in the map).</summary>
    Wired,
    /// <summary>Several repos provide it and nothing picks one, so checkout can't proceed.</summary>
    Ambiguous,
    /// <summary>No provider and no map default, so checkout can't proceed.</summary>
    Gap,
    /// <summary>No provider, but the map supplies constant values for it.</summary>
    Default,
}

/// <summary>Who relies on a provided capability.</summary>
public enum ProvideUse
{
    /// <summary>At least one module wires a need to it.</summary>
    Shared,
    /// <summary>Only templates in its own repo reference it.</summary>
    OwnUse,
    /// <summary>Nothing references it — often a sign of a name that doesn't line up.</summary>
    Unused,
}

/// <summary>A template that references a capability: the env key or compose path it sets, and the
/// repo-relative file it sets it in.</summary>
public sealed record TemplateUse(string File, string Key);

public sealed record ModuleRef(string Repo, string Module);

public sealed record ProviderRef(string Repo, string Module, string Capability);

/// <summary>A likely value for one output of an unmet need, recovered from a module's env files.</summary>
public sealed record ValueHint(string Output, string Value, string Source);

public sealed record NeedInfo(
    string Repo,
    string Module,
    string Capability,
    string Alias,
    NeedState State,
    string Target,
    IReadOnlyList<ProviderRef> Providers,
    IReadOnlyDictionary<string, string>? DefaultValues,
    IReadOnlyList<TemplateUse> Uses,
    ProviderRef? Similar,
    IReadOnlyList<ValueHint> Hints);

public sealed record ProvideInfo(
    string Repo,
    string Module,
    string Capability,
    IReadOnlyList<string> Outputs,
    IReadOnlyDictionary<string, string>? Constants,
    IReadOnlyList<ModuleRef> Consumers,
    IReadOnlyList<TemplateUse> OwnUses)
{
    public ProvideUse Use => Consumers.Count > 0 ? ProvideUse.Shared
        : OwnUses.Count > 0 ? ProvideUse.OwnUse
        : ProvideUse.Unused;
}

/// <param name="ReliedOnBy">How many other modules wire a need to one of this module's provides.</param>
public sealed record ModuleInfo(
    string Repo,
    string Module,
    string Path,
    IReadOnlyList<ProvideInfo> Provides,
    IReadOnlyList<NeedInfo> Needs,
    int ReliedOnBy);

public sealed record RepoInfo(string Name, string Root, bool IsVirtual, IReadOnlyList<ModuleInfo> Modules);

public enum FixedKind
{
    /// <summary>A local host and port, such as <c>localhost:1025</c>.</summary>
    Address,
    /// <summary>A bare port number.</summary>
    Port,
}

/// <summary>A value a module's config sets literally where a per-workspace value could collide: every
/// workspace of the map gets the same address or port.</summary>
public sealed partial record FixedValue(string Repo, string Module, string File, string Key, string Value, FixedKind Kind)
{
    /// <summary><see cref="Value"/> with any password masked (URL credentials, <c>Password=</c> settings),
    /// for showing on screen. <see cref="Value"/> stays exact, since a conversion writes it back.</summary>
    public string DisplayValue => PasswordSetting().Replace(UrlPassword().Replace(Value, "$1•••@"), "$1•••");

    [System.Text.RegularExpressions.GeneratedRegex(@"(://[^:/@\s]+:)[^@/\s]+@")]
    private static partial System.Text.RegularExpressions.Regex UrlPassword();

    [System.Text.RegularExpressions.GeneratedRegex(@"(?i)((?:password|pwd)\s*=\s*)[^;]*")]
    private static partial System.Text.RegularExpressions.Regex PasswordSetting();
}

/// <summary>A map repo that can't take part in the wiring as things stand.</summary>
public sealed record RepoProblem(string Repo, string Message);

/// <summary>
/// Everything the Maps view and <c>sprig map show/check</c> report about a map, before any checkout: how
/// each need is met, who relies on each provide, constants, likely fixes, and what can't be read at all.
/// </summary>
public sealed record MapInspection(
    MapDefinition Map,
    IReadOnlyList<RepoInfo> Repos,
    IReadOnlyList<RepoProblem> Problems,
    IReadOnlyList<FixedValue> FixedValues)
{
    public IEnumerable<ModuleInfo> Modules => Repos.SelectMany(r => r.Modules);
    public IEnumerable<NeedInfo> Needs => Modules.SelectMany(m => m.Needs);
    public IEnumerable<ProvideInfo> Provides => Modules.SelectMany(m => m.Provides);

    public IReadOnlyList<NeedInfo> Gaps => Needs.Where(n => n.State == NeedState.Gap).ToList();
    public IReadOnlyList<NeedInfo> Ambiguities => Needs.Where(n => n.State == NeedState.Ambiguous).ToList();
    public IReadOnlyList<NeedInfo> Defaulted => Needs.Where(n => n.State == NeedState.Default).ToList();

    /// <summary>Modules two or more others rely on, most relied on first.</summary>
    public IReadOnlyList<ModuleInfo> Hubs => Modules.Where(m => m.ReliedOnBy >= 2)
        .OrderByDescending(m => m.ReliedOnBy).ThenBy(m => m.Repo, StringComparer.Ordinal).ToList();

    /// <summary>True when a checkout of the whole map would wire every need.</summary>
    public bool IsReady => Problems.Count == 0 && !Needs.Any(n => n.State is NeedState.Gap or NeedState.Ambiguous);

    public ModuleInfo? FindModule(string repo, string module)
        => Modules.FirstOrDefault(m => m.Repo == repo && m.Module == module);
}
