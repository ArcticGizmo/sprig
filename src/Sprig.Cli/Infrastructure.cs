using System.ComponentModel;
using System.Text.Json;
using Sprig.Engine.Definitions;
using Sprig.Engine.Settings;
using Sprig.Engine.Trellises;
using Sprig.Engine.Store;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli;

/// <summary>The engine services a command needs, built once per run and handed to every command by the type
/// resolver, so command constructors take a single dependency.</summary>
public sealed record CliContext(
    ISprigPaths Paths,
    ISettingsStore Settings,
    ITrustStore Trust,
    DefinitionRunner Definitions,
    TrellisRegistry Trellises,
    IAnsiConsole Ansi)
{
    /// <summary>Prompts are only shown at a real terminal, never under <c>--json</c> or when input is piped.</summary>
    public bool Interactive(bool json) => !json && !Console.IsInputRedirected && !Console.IsOutputRedirected;
}

/// <summary>Base settings carrying the one global flag, so <c>--json</c> is accepted by every command.</summary>
public class GlobalSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Machine-readable output")]
    public bool Json { get; set; }
}

/// <summary>A minimal container over the instances the CLI constructs up front. Registered instances are
/// returned as-is, command types are constructed by reflection, and unknown framework interfaces return null
/// so Spectre falls back to its defaults.</summary>
sealed class TypeRegistrar(IDictionary<Type, object> instances) : ITypeRegistrar
{
    public void Register(Type service, Type implementation) { }
    public void RegisterInstance(Type service, object implementation) => instances[service] = implementation;
    public void RegisterLazy(Type service, Func<object> factory) => instances[service] = factory();
    public ITypeResolver Build() => new TypeResolver(instances);
}

sealed class TypeResolver(IDictionary<Type, object> instances) : ITypeResolver, IDisposable
{
    public object? Resolve(Type? type)
    {
        if (type is null) return null;
        if (instances.TryGetValue(type, out var instance)) return instance;
        // Spectre resolves IEnumerable<T> for its extension points and expects an empty collection, not null.
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        if (type.IsInterface || type.IsAbstract) return null;
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (ctor is null) return null;
        var args = ctor.GetParameters().Select(p => Resolve(p.ParameterType)).ToArray();
        return Activator.CreateInstance(type, args);
    }

    public void Dispose() { }
}

/// <summary>Output primitives shared by the commands. <see cref="Json"/> writes straight to stdout so a payload
/// full of <c>[</c>/<c>]</c> is never mangled by the markup parser.</summary>
static class CliOutput
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Json<T>(T value)
        => Console.WriteLine(JsonSerializer.Serialize(value, Indented));

    static IAnsiConsole Console_ => AnsiConsole.Console;

    /// <summary>A green-ticked success line. The message is escaped, so brackets in paths render literally.</summary>
    public static void Success(string message)
        => Console_.MarkupLine($"[green]{Commands.Glyph.Check(Console_)}[/] {Markup.Escape(message)}");

    /// <summary>The machine payload under <c>--json</c>, else a green-ticked line.</summary>
    public static int Ok(bool json, string human, object payload)
    {
        if (json) Json(payload);
        else Success(human);
        return 0;
    }
}
