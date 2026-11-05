using Sprig.Model;

namespace Sprig;

/// <summary>Postgres on an allocated port, with a data volume per sprout.</summary>
public sealed class PostgresResource : ContainerResource
{
    public const string User = "sprig";
    public const string Password = "sprig";

    internal PostgresResource(string name, string image, SourceLocation location) : base(name, image, location)
    {
        Endpoint("tcp", 5432, file: location.File, line: location.Line);
        Env("POSTGRES_USER", User, location.File, location.Line);
        Env("POSTGRES_PASSWORD", Password, location.File, location.Line);
        Volume("data", "/var/lib/postgresql/data");
        // Over TCP, because the image's first-run setup serves only the unix socket and would pass a plain pg_isready.
        Health($"pg_isready -h 127.0.0.1 -U {User}");
    }

    /// <summary>An Npgsql-style connection string for <paramref name="database"/>.</summary>
    public Expr Database(string database)
        => new Template([new Literal("Host=localhost;Port="), Port, new Literal($";Database={database};Username={User};Password={Password}")]);
}

public sealed class RedisResource : ContainerResource
{
    internal RedisResource(string name, string image, SourceLocation location) : base(name, image, location)
    {
        Endpoint("tcp", 6379, file: location.File, line: location.Line);
        Health("redis-cli ping");
    }

    /// <summary><c>localhost:&lt;port&gt;</c>, the form StackExchange.Redis expects.</summary>
    public Expr Url => new Template([new Literal("localhost:"), Port]);
}

public sealed class RabbitMqResource : ContainerResource
{
    public const string User = "sprig";
    public const string Password = "sprig";

    internal RabbitMqResource(string name, string image, SourceLocation location) : base(name, image, location)
    {
        Endpoint("amqp", 5672, file: location.File, line: location.Line);
        Endpoint("ui", 15672, scheme: "http", file: location.File, line: location.Line);
        Env("RABBITMQ_DEFAULT_USER", User, location.File, location.Line);
        Env("RABBITMQ_DEFAULT_PASS", Password, location.File, location.Line);
        Health(HealthCommand);
    }

    /// <summary>Health checks run as root. If rabbitmq-diagnostics runs before the server has written its Erlang
    /// cookie, it writes one as root that the server (uid 999) can't read, and the server exits at start. So the
    /// check waits for the server's own cookie first.</summary>
    public const string HealthCommand = "test -f /var/lib/rabbitmq/.erlang.cookie && rabbitmq-diagnostics -q ping";

    /// <summary><c>amqp://sprig:sprig@localhost:&lt;port&gt;</c>.</summary>
    public Expr Url => new Template([new Literal($"amqp://{User}:{Password}@localhost:"), PortOf("amqp")]);

    /// <summary>The management UI.</summary>
    public Ref UiUrl => new($"{Name}.ui.url");
}

public sealed class MailpitResource : ContainerResource
{
    internal MailpitResource(string name, string image, SourceLocation location) : base(name, image, location)
    {
        Endpoint("smtp", 1025, file: location.File, line: location.Line);
        Endpoint("ui", 8025, scheme: "http", file: location.File, line: location.Line);
        Health("/mailpit readyz");
    }

    /// <summary><c>smtp://localhost:&lt;port&gt;</c>.</summary>
    public Expr SmtpUrl => new Template([new Literal("smtp://localhost:"), PortOf("smtp")]);

    /// <summary>The web inbox.</summary>
    public Ref UiUrl => new($"{Name}.ui.url");
}
