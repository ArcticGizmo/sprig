namespace Sprig;

/// <summary>The kind of value a setting or export carries. Checked when the engine joins repos, so a repo whose
/// setting is a <see cref="Url"/> can't be fed a connection string.</summary>
public abstract class ValueType
{
    private protected ValueType() { }

    internal static string NameOf<T>() where T : ValueType => typeof(T).Name.ToLowerInvariant();
}

public sealed class Url : ValueType;
public sealed class ConnectionString : ValueType;
public sealed class HostPort : ValueType;
public sealed class Secret : ValueType;
public sealed class Text : ValueType;

/// <summary>A value the repo takes from outside: from the workspace, an orchestration, or else the repo's own
/// default.</summary>
public sealed record Setting<T>(string Name) : Ref("setting." + Name) where T : ValueType
{
    /// <summary>The repo that declared it, so <see cref="Is"/> can record its role there.</summary>
    internal RepoBuilder? Owner { get; init; }

    /// <summary>Say what the value points at, where sprig can't tell from its name or value, e.g.
    /// <c>repo.Setting&lt;Url&gt;("events-url").Is(Role.Queue)</c>: drawn as a queue with the infra, not as a value from
    /// outside.</summary>
    public Setting<T> Is(Role role)
    {
        Owner?.SetSettingRole(Name, Roles.Name(role));
        return this;
    }

    // The same setting whichever repo asked for it, as a reference in a value is.
    public bool Equals(Setting<T>? other) => other is not null && Name == other.Name;

    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);
}

/// <summary>A port sprig allocates for an app's endpoint.</summary>
public sealed record PortRef(string App, string Endpoint) : Ref($"{App}.{Endpoint}.port");
