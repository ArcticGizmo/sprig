namespace Sprig;

/// <summary>
/// What a resource or setting is, for how sprig draws it: each role has an icon of its own, and infra roles sit with
/// the infra. Sprig works most out for itself, from an image (<c>rabbitmq:4</c>), a name (<c>safety-db</c>) or a value
/// (<c>amqp://…</c>); say it with <c>.Is(...)</c> when it can't, e.g. <c>repo.Setting&lt;Url&gt;("events-url").Is(Role.Queue)</c>.
/// </summary>
public enum Role
{
    Database,
    Cache,
    Queue,
    Search,
    Mail,
    Storage,
    Identity,
    Proxy,

    /// <summary>A front end people open in a browser.</summary>
    Web,
    Api,

    /// <summary>A background process with no page or API of its own.</summary>
    Worker,
}

static class Roles
{
    internal static string Name(Role role) => role.ToString().ToLowerInvariant();
}
