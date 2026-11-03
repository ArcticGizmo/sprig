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
    }

    /// <summary>An Npgsql-style connection string for <paramref name="database"/>.</summary>
    public Expr Database(string database)
        => new Template([new Literal("Host=localhost;Port="), Port, new Literal($";Database={database};Username={User};Password={Password}")]);
}

public sealed class RedisResource : ContainerResource
{
    internal RedisResource(string name, string image, SourceLocation location) : base(name, image, location)
        => Endpoint("tcp", 6379, file: location.File, line: location.Line);

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
    }

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
    }

    /// <summary><c>smtp://localhost:&lt;port&gt;</c>.</summary>
    public Expr SmtpUrl => new Template([new Literal("smtp://localhost:"), PortOf("smtp")]);

    /// <summary>The web inbox.</summary>
    public Ref UiUrl => new($"{Name}.ui.url");
}
