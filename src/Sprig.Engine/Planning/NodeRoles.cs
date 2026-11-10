using System.Text.RegularExpressions;

namespace Sprig.Engine.Planning;

/// <summary>
/// What a node in a workspace is, for how it's drawn: the role its definition gives it (<c>.Is(Role.Queue)</c>), else
/// one worked out from its image, its name or its value. Names are matched word by word (<c>safety-db</c> is a
/// database, <c>rabbitmq-url</c> a queue), so a word only counts whole or as the start of one (<c>postgresql</c>).
/// </summary>
public static partial class NodeRoles
{
    public const string Database = "database";
    public const string Cache = "cache";
    public const string Queue = "queue";
    public const string Search = "search";
    public const string Mail = "mail";
    public const string Storage = "storage";
    public const string Identity = "identity";
    public const string Proxy = "proxy";
    public const string Web = "web";
    public const string Api = "api";
    public const string Worker = "worker";

    // Earlier roles win: "redis-db" is a cache, "event-bus-db" a queue.
    static readonly (string Role, string[] Words)[] InfraWords =
    [
        (Cache, ["redis", "valkey", "memcached", "garnet", "dragonfly", "cache"]),
        (Queue, ["rabbitmq", "rabbit", "kafka", "redpanda", "nats", "activemq", "artemis", "servicebus", "sqs", "pubsub", "pulsar", "amqp", "mq", "queue", "bus", "broker"]),
        (Search, ["elasticsearch", "elastic", "opensearch", "meilisearch", "meili", "typesense", "solr", "search"]),
        (Mail, ["mailpit", "mailhog", "papercut", "smtp", "mail"]),
        (Storage, ["minio", "azurite", "blob", "s3", "storage", "bucket"]),
        (Identity, ["keycloak", "identity", "auth0", "oidc", "oauth", "sso", "idp", "authentik", "zitadel"]),
        (Proxy, ["nginx", "traefik", "envoy", "caddy", "haproxy", "yarp", "proxy"]),
        (Database, ["postgres", "mysql", "mariadb", "mssql", "sqlserver", "sql", "mongo", "cosmos", "cockroach", "oracle", "db", "database"]),
    ];

    static readonly (string Role, string[] Words)[] AppWords =
    [
        (Api, ["api", "service", "backend"]),
        (Worker, ["worker", "consumer", "processor", "scheduler", "cron", "jobs", "daemon"]),
        (Web, ["web", "client", "frontend", "spa", "ui", "admin", "portal", "site", "app"]),
    ];

    /// <summary>The roles a definition can give; anything else it says is ignored.</summary>
    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
        { Database, Cache, Queue, Search, Mail, Storage, Identity, Proxy, Web, Api, Worker };

    /// <summary>A role that belongs with the infra rather than the apps.</summary>
    public static bool IsInfra(string? role) => role is Database or Cache or Queue or Search or Mail or Storage or Identity or Proxy;

    /// <summary>Infra or an outside value: what it says, else its image, else its name, else its value's scheme.</summary>
    public static string? OfInfra(string? given, string name, string? image = null, string? value = null)
        => Valid(given) ?? FromWords(InfraWords, image) ?? FromWords(InfraWords, name) ?? FromValue(value);

    /// <summary>An app: what it says, else its name, read for the kind of app (<c>orders-api</c>, <c>cdc-worker</c>), else
    /// a front end when it's a Node app.</summary>
    public static string? OfApp(string? given, string name, string? kind = null)
        => Valid(given) ?? FromWords(AppWords, name) ?? (kind == "node" ? Web : null);

    static string? Valid(string? role) => role is not null && Known.Contains(role) ? role : null;

    static string? FromWords((string Role, string[] Words)[] table, string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var words = WordSplit().Split(text.ToLowerInvariant()).Where(w => w.Length > 0).ToArray();
        foreach (var (role, known) in table)
            if (words.Any(w => known.Any(k => w == k || (k.Length >= 4 && w.StartsWith(k, StringComparison.Ordinal)))))
                return role;
        return null;
    }

    /// <summary>A url's scheme or a connection string's keys: <c>amqp://</c> is a queue, <c>Host=…</c> a database.</summary>
    static string? FromValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var v = value.TrimStart().ToLowerInvariant();
        if (v.Contains("sb://", StringComparison.Ordinal)) return Queue;
        if (Scheme().Match(v) is { Success: true } m)
            return m.Groups[1].Value switch
            {
                "postgres" or "postgresql" or "mysql" or "mariadb" or "mongodb" or "mongodb+srv" or "sqlserver" or "jdbc" => Database,
                "redis" or "rediss" => Cache,
                "amqp" or "amqps" or "kafka" or "nats" => Queue,
                "smtp" or "smtps" => Mail,
                "s3" => Storage,
                _ => null,
            };
        return ConnectionString().IsMatch(v) ? Database : null;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex WordSplit();

    [GeneratedRegex(@"^([a-z][a-z0-9+.-]*):(//)?")]
    private static partial Regex Scheme();

    [GeneratedRegex(@"(^|;)\s*(host|server|data source|address)\s*=")]
    private static partial Regex ConnectionString();
}
