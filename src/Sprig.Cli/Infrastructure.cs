using System.ComponentModel;
using System.Text.Json;
using Sprig.Core.Git;
using Sprig.Core.Maps;
using Sprig.Core.Pools;
using Sprig.Core.Settings;
using Sprig.Core.Stacks;
using Sprig.Core.Store;
using Sprig.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Sprig.Cli;

/// <summary>The Core services a command needs, built once per <see cref="CliApp.Run(string[], ISprigPaths)"/>
/// and handed to every command by the type resolver. One object keeps command constructors to a single
/// dependency; <see cref="Ansi"/> is the per-run console the framework also renders help/errors through.</summary>
public sealed record CliContext(
    ISprigPaths Paths,
    WorkspaceService Workspaces,
    WorkspaceReconciler Reconciler,
    RepoRegistryStore Repos,
    MapStore Maps,
    MapResolver MapResolver,
    MapPoolService MapPool,
    ISettingsStore Settings,
    IGitService Git,
    IAnsiConsole Ansi);

/// <summary>Base settings carrying the one global flag. Every command's settings inherits it, so
/// <c>--json</c> is accepted everywhere and stays the machine-output contract it always was.</summary>
public class GlobalSettings : CommandSettings
{
    [CommandOption("--json")]
    [Description("Machine-readable output")]
    public bool Json { get; set; }
}

/// <summary>Command settings that name a single workspace — the shape shared by up/down/reset/status/info.
/// The name is optional: omit it at a terminal and the verb picks from a list (see
/// <see cref="Commands.WorkspacePrompt"/>); in a script or under <c>--json</c> it's required.</summary>
public class WorkspaceSettings : GlobalSettings
{
    [CommandArgument(0, "[workspace]")]
    [Description("Workspace name (omit at a terminal to pick interactively)")]
    public string? Workspace { get; set; }
}

/// <summary>A minimal container over the handful of instances the CLI constructs up front. Spectre asks
/// the resolver for each command type (and for <see cref="IAnsiConsole"/>); anything registered is
/// returned as-is, command types are constructed by reflection, and unknown framework interfaces fall
/// back to Spectre's defaults by returning null.</summary>
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
        // Spectre resolves IEnumerable<T> for its extension points (e.g. help providers) and expects an
        // empty collection, not null, when nothing is registered.
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return Array.CreateInstance(type.GetGenericArguments()[0], 0);
        if (type.IsInterface || type.IsAbstract) return null; // let Spectre supply its own default
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (ctor is null) return null;
        var args = ctor.GetParameters().Select(p => Resolve(p.ParameterType)).ToArray();
        return Activator.CreateInstance(type, args);
    }

    public void Dispose() { }
}

/// <summary>An <see cref="IProgress{T}"/> that invokes its handler synchronously on the reporting
/// thread — unlike <see cref="Progress{T}"/>, which marshals to a captured context and would arrive
/// out of order (or after the work finished) inside a Spectre status/live display.</summary>
sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>Output primitives shared by the command classes. <see cref="Json"/> writes straight to stdout
/// so the JSON contract is never routed through the markup parser (a payload full of <c>[</c>/<c>]</c> would
/// otherwise be mangled); the human helpers render through the shared console so colour, glyphs and the
/// plain-when-redirected behaviour stay consistent with the tables the list commands print.</summary>
static class CliOutput
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Json<T>(T value)
        => Console.WriteLine(JsonSerializer.Serialize(value, Indented));

    /// <summary>The shared, late-bound console (set in <see cref="CliApp.Run(string[], ISprigPaths)"/>):
    /// coloured at a real terminal, plain when redirected. Everything human-facing renders through it.</summary>
    static IAnsiConsole Console_ => AnsiConsole.Console;

    /// <summary>A green-ticked success line — the shared "it worked" look every mutating command shares.
    /// The message is escaped, so paths and names with <c>[</c>/<c>]</c> render literally.</summary>
    public static void Success(string message)
        => Console_.MarkupLine($"[green]{Commands.Glyph.Check(Console_)}[/] {Markup.Escape(message)}");

    /// <summary>A dim, secondary line — empty-state notes ("no repos registered") and the like.</summary>
    public static void Muted(string message)
        => Console_.MarkupLine($"[dim]{Markup.Escape(message)}[/]");

    /// <summary>Emit a success result honouring <c>--json</c>: the machine payload when asked for, a
    /// green-ticked human line otherwise. Mutating commands route through here so <c>--json</c> is a promise
    /// scripts can rely on everywhere, not just on the read commands.</summary>
    public static int Ok(bool json, string human, object payload)
    {
        if (json) Json(payload);
        else if (!string.IsNullOrEmpty(human)) Success(human);
        return 0;
    }
}
