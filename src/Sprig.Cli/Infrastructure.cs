using System.ComponentModel;
using System.Text.Json;
using Sprig.Engine.Definitions;
using Sprig.Engine.Settings;
using Sprig.Engine.Workspaces;
using Sprig.Engine.Orchestrations;
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
    OrchestrationRegistry Orchestrations,
    WorkspaceService Workspaces,
    Sprig.Engine.Hosting.HostClient Host,
    IAnsiConsole Ansi)
{
    /// <summary>Prompts are only shown at a real terminal, never under <c>--json</c> or when input is piped.</summary>
    public bool Interactive(bool json) => !json && !Console.IsInputRedirected && !Console.IsOutputRedirected;
}

/// <summary>Base settings carrying the global flags, so <c>--json</c> and <c>--unmask-secrets</c> are accepted by every
/// command.</summary>
public class GlobalSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Machine-readable output")]
    public bool Json { get; set; }

    // A flag only, never a setting or an environment variable, so it can't be left on and forgotten.
    [CommandOption("--unmask-secrets")]
    [Description("Show every value in full, secrets included. Without it, secrets are hidden and --json leaves out values that aren't ports, addresses or flags")]
    public bool UnmaskSecrets { get; set; }
}

/// <summary>Records and plans as output shows them: trimmed (see <see cref="Sprig.Engine.Planning.SecretValues"/>)
/// unless <c>--unmask-secrets</c> asks for everything, which is then said on stderr, so it shows in a log even when the
/// output itself is piped elsewhere.</summary>
static class Shown
{
    public static WorkspaceRecord Record(WorkspaceRecord record, Sprig.Engine.Planning.Plan? plan, GlobalSettings s)
        => s.UnmaskSecrets ? Said(record) : record with { Values = Sprig.Engine.Planning.SecretValues.Trimmed(record.Values, plan) };

    public static Sprig.Engine.Planning.Plan? Plan(Sprig.Engine.Planning.Plan? plan, GlobalSettings s)
        => plan is null ? null : s.UnmaskSecrets ? Said(plan) : Sprig.Engine.Planning.SecretValues.Trimmed(plan);

    /// <summary>A setting's value for the screen.</summary>
    public static string Value(Sprig.Engine.Planning.Plan? plan, string setting, string value, GlobalSettings s)
        => s.UnmaskSecrets ? Said(value) : Sprig.Engine.Planning.SecretValues.Show(plan, setting, value);

    static bool _said;

    static T Said<T>(T value)
    {
        if (!_said) Console.Error.WriteLine("sprig: secrets are shown unmasked in this output (--unmask-secrets)");
        _said = true;
        return value;
    }
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

/// <summary>
/// A failure as <c>--json</c> gives it: <c>{ ok: false, code, error, … }</c>. <c>code</c> is part of the contract, so a
/// script or an agent branches on it rather than on the English, and the lists that say what to do next come with it.
/// </summary>
static class CliErrors
{
    public const string InputsNeeded = "inputs-needed";
    public const string Untrusted = "untrusted";
    public const string Held = "held";
    public const string SlotBroken = "slot-broken";
    public const string SlotBusy = WorkspaceService.BusyCode;
    public const string Problems = "problems";
    public const string Error = "error";

    public static string Code(Exception e) => e switch
    {
        WorkspaceException { Code: { } code } => code,
        WorkspaceException { Asks.Count: > 0 } => InputsNeeded,
        WorkspaceException { Untrusted.Count: > 0 } => Untrusted,
        WorkspaceException { HeldBy: not null } => Held,
        WorkspaceException { Problems.Count: > 0 } => Problems,
        _ => Error,
    };

    public static object Payload(Exception e) => e is WorkspaceException w
        ? new
        {
            ok = false,
            code = Code(e),
            error = e.Message,
            problems = w.Problems,
            asks = w.Asks.Select(a => new { name = a.Name, type = a.Type, owner = a.Owner }),
            untrusted = w.Untrusted.Select(u => u.Path),
            heldBy = w.HeldBy?.SlotName,
        }
        : new { ok = false, code = Code(e), error = e.Message };
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
