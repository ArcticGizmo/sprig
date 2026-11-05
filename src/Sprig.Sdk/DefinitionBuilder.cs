using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sprig.Model;

namespace Sprig;

/// <summary>What a repo and a trellis have in common: they can own containers, and they hand their description to
/// sprig with <see cref="Emit"/>.</summary>
public abstract class DefinitionBuilder
{
    private protected readonly List<ContainerResource> Containers = [];

    private protected DefinitionBuilder(string name, SourceLocation location)
    {
        Name = name;
        Location = location;
    }

    public string Name { get; }

    private protected SourceLocation Location { get; }

    /// <summary>A container from any image. Prefer a preset (<see cref="Postgres"/>, <see cref="Redis"/>, ...) when
    /// there is one.</summary>
    public ContainerResource Container(string name, string image, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Add(new ContainerResource(name, image, new SourceLocation(file, line)));

    /// <summary>Postgres with a data volume, a <c>sprig</c> user and password, and a port sprig allocates.</summary>
    public PostgresResource Postgres(string name, string image = "postgres:17", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Add(new PostgresResource(name, image, new SourceLocation(file, line)));

    public RedisResource Redis(string name, string image = "redis:7", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Add(new RedisResource(name, image, new SourceLocation(file, line)));

    /// <summary>RabbitMQ with its management UI, and a <c>sprig</c> user and password.</summary>
    public RabbitMqResource RabbitMq(string name, string image = "rabbitmq:4-management", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Add(new RabbitMqResource(name, image, new SourceLocation(file, line)));

    /// <summary>Mailpit: an SMTP server that keeps every message, with a web inbox to read them.</summary>
    public MailpitResource Mailpit(string name, string image = "axllent/mailpit:v1.27", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => Add(new MailpitResource(name, image, new SourceLocation(file, line)));

    T Add<T>(T container) where T : ContainerResource
    {
        Containers.Add(container);
        return container;
    }

    private protected abstract DefinitionModel Build();

    /// <summary>Hand the description to sprig. Call it last, as <c>return repo.Emit(args);</c>.</summary>
    public int Emit(string[] args)
    {
        var json = JsonSerializer.Serialize(Build(), ModelJsonContext.Default.DefinitionModel);

        var outIndex = Array.IndexOf(args, "--out");
        if (args.Contains("emit") && outIndex >= 0 && outIndex + 1 < args.Length)
        {
            File.WriteAllText(args[outIndex + 1], json);
            return 0;
        }

        Console.WriteLine(json);
        return 0;
    }
}

public static class Sdk
{
    /// <summary>The model's shape. Raised whenever <see cref="ModelJsonContext"/>'s records change, so the engine can
    /// refuse (and never cache) a model it would misread.</summary>
    public const int Schema = 7;

    public static string Version { get; } =
        typeof(Sdk).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}

/// <summary>Values about the sprout being built, for names that must differ between sprouts.</summary>
public static class Sprout
{
    /// <summary>The sprout's name, e.g. <c>feat-x</c>.</summary>
    public static Ref Name { get; } = new("sprout.name");
}
