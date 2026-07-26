namespace Sprig.Core.Shared;

/// <summary>
/// What sprig knows about a poolable image: the values it publishes, how to carve a namespace out of it,
/// and how to reach it. Extraction fills these in so the common case is zero authoring — the escape hatch
/// is that everything a preset writes is ordinary editable JSON.
/// </summary>
/// <param name="Match">Substring of the image name this preset claims (e.g. <c>postgres</c>).</param>
/// <param name="DefaultPort">The port the service listens on inside the container.</param>
/// <param name="Values">Published values, as templates. <c>${sprig.workspace}</c> makes one per-workspace.</param>
/// <param name="Attach">Commands that create this workspace's namespace.</param>
/// <param name="Detach">Commands that drop it.</param>
/// <param name="ConnectionKeys">
/// Env-key names, lowercased, whose value is likely to be a whole connection string for this kind of
/// service — used to spot the key an overlay needs to rewrite when no input carries the namespace.
/// </param>
/// <param name="CredentialsFrom">
/// Published value → the container env vars that actually decide it. Extraction reads these off the
/// service it lifts, because the container initialises with whatever the repo put there: asserting a
/// username the image never created produces a resource whose attach command can't log in.
/// </param>
public sealed record SharedResourcePreset(
    string Match,
    int DefaultPort,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyList<string> Attach,
    IReadOnlyList<string> Detach,
    IReadOnlyList<string> ConnectionKeys,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CredentialsFrom)
{
    /// <summary>Presets sprig ships. Anything else extracts with values and no attach/detach commands.</summary>
    public static IReadOnlyList<SharedResourcePreset> All { get; } =
    [
        new("postgres", 5432,
            new Dictionary<string, string>
            {
                ["host"] = "localhost",
                ["port"] = "5432",
                ["database"] = "sprig_${sprig.workspace}",
                ["user"] = "postgres",
                ["password"] = "postgres",
                // psql with no -d connects to a database named after the user, which usually doesn't
                // exist. Admin commands need one that does: whatever the image was told to create,
                // falling back to the `postgres` database initdb always makes.
                ["maintenance"] = "postgres",
                ["url"] = "postgres://${sprig.shared.user}:${sprig.shared.password}@${sprig.shared.host}:${sprig.shared.port}/${sprig.shared.database}",
            },
            [
                """psql -U "${sprig.shared.user}" -d "${sprig.shared.maintenance}" -tc "SELECT 1 FROM pg_database WHERE datname='${sprig.shared.database}'" | grep -q 1 || psql -U "${sprig.shared.user}" -d "${sprig.shared.maintenance}" -c 'CREATE DATABASE "${sprig.shared.database}"'""",
            ],
            ["""psql -U "${sprig.shared.user}" -d "${sprig.shared.maintenance}" -c 'DROP DATABASE IF EXISTS "${sprig.shared.database}"'"""],
            ["connectionstring", "database_url", "db_url", "databaseurl", "dburl", "postgres_url"],
            new Dictionary<string, IReadOnlyList<string>>
            {
                // The official image creates POSTGRES_USER, defaulting to 'postgres' when unset.
                ["user"] = ["POSTGRES_USER"],
                ["password"] = ["POSTGRES_PASSWORD"],
                ["maintenance"] = ["POSTGRES_DB"],
            }),

        new("mysql", 3306,
            new Dictionary<string, string>
            {
                ["host"] = "localhost",
                ["port"] = "3306",
                ["database"] = "sprig_${sprig.workspace}",
                ["user"] = "root",
                ["password"] = "sprig",
                ["url"] = "mysql://${sprig.shared.user}:${sprig.shared.password}@${sprig.shared.host}:${sprig.shared.port}/${sprig.shared.database}",
            },
            ["""mysql -u"${sprig.shared.user}" -p"${sprig.shared.password}" -e 'CREATE DATABASE IF NOT EXISTS `${sprig.shared.database}`'"""],
            ["""mysql -u"${sprig.shared.user}" -p"${sprig.shared.password}" -e 'DROP DATABASE IF EXISTS `${sprig.shared.database}`'"""],
            ["connectionstring", "database_url", "db_url", "mysql_url"],
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["password"] = ["MYSQL_ROOT_PASSWORD"],
            }),

        // Redis namespaces by numbered database rather than by name, and there are exactly 16 of them —
        // so the slot number *is* the namespace, and capacity isn't a preference so much as a fact.
        new("redis", 6379,
            new Dictionary<string, string>
            {
                ["host"] = "localhost",
                ["port"] = "6379",
                ["database"] = "${sprig.slot}",
                ["url"] = "redis://${sprig.shared.host}:${sprig.shared.port}/${sprig.shared.database}",
            },
            ["""redis-cli -n "${sprig.shared.database}" FLUSHDB"""],
            ["""redis-cli -n "${sprig.shared.database}" FLUSHDB"""],
            ["redis_url", "redisurl", "cache_url"],
            new Dictionary<string, IReadOnlyList<string>>()),

        new("mongo", 27017,
            new Dictionary<string, string>
            {
                ["host"] = "localhost",
                ["port"] = "27017",
                ["database"] = "sprig_${sprig.workspace}",
                ["url"] = "mongodb://${sprig.shared.host}:${sprig.shared.port}/${sprig.shared.database}",
            },
            // Mongo creates a database on first write, so there is nothing to do on attach.
            [],
            ["""mongosh --quiet --eval 'db.getSiblingDB("${sprig.shared.database}").dropDatabase()'"""],
            ["mongo_url", "mongodb_url", "mongourl", "database_url"],
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["user"] = ["MONGO_INITDB_ROOT_USERNAME"],
                ["password"] = ["MONGO_INITDB_ROOT_PASSWORD"],
            }),
    ];

    /// <summary>The preset for an image reference (<c>postgres:16-alpine</c>), or null if sprig has none.</summary>
    public static SharedResourcePreset? For(string? image)
        => image is not { Length: > 0 } ? null
            : All.FirstOrDefault(p => image.Contains(p.Match, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A name derived from the image, so two versions of the same thing don't quietly become one pool —
    /// <c>postgres:16-alpine</c> → <c>postgres-16</c>. Version skew is a real reason to run two resources,
    /// and the naming should make that the obvious move rather than a workaround.
    /// </summary>
    public static string NameFor(string image)
    {
        var withoutRegistry = image.Split('/').Last();
        var parts = withoutRegistry.Split(':');
        var name = Sanitise(parts[0]);
        if (parts.Length < 2) return name;

        // "16-alpine" → "16"; "latest" adds nothing worth saying.
        var tag = Sanitise(parts[1].Split('-')[0]);
        return tag is "" or "latest" ? name : $"{name}-{tag}";
    }

    static string Sanitise(string text)
        => new([.. text.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? char.ToLowerInvariant(c) : '-')]);
}
