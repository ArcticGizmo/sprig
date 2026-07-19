using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprig.Core.Config;

namespace Sprig.App.ViewModels;

public sealed record PortRow(string Name, string? Description);
public sealed record KvRow(string Key, string Value);
public sealed record EnvGroup(string File, IReadOnlyList<KvRow> Items);
public sealed record ComposeInfo(string File, IReadOnlyList<KvRow> Overrides);

/// <summary>
/// Read-only presentation of a repo's <c>.sprig.json</c> (ports, env overrides, compose overrides,
/// provides). Editing is a later stage — this just shows the current configuration.
/// </summary>
public sealed class RepoConfigViewModel
{
    RepoConfigViewModel(string name, IReadOnlyList<PortRow> ports, IReadOnlyList<EnvGroup> env,
        ComposeInfo? compose, IReadOnlyList<KvRow> provides, string? error)
    {
        Name = name;
        Ports = ports;
        Env = env;
        Compose = compose;
        Provides = provides;
        Error = error;
    }

    public string Name { get; }
    public IReadOnlyList<PortRow> Ports { get; }
    public IReadOnlyList<EnvGroup> Env { get; }
    public ComposeInfo? Compose { get; }
    public IReadOnlyList<KvRow> Provides { get; }
    public string? Error { get; }

    public bool Ok => Error is null;
    public bool HasPorts => Ports.Count > 0;
    public bool HasEnv => Env.Count > 0;
    public bool HasCompose => Compose is not null;
    public bool HasProvides => Provides.Count > 0;

    /// <summary>Load and present the repo's config, or an error card if it can't be read.</summary>
    public static RepoConfigViewModel Load(string repoPath)
    {
        var configPath = Path.Combine(repoPath, ".sprig.json");
        try
        {
            var c = SprigConfigLoader.LoadFromFile(configPath);
            return new RepoConfigViewModel(
                c.Name,
                c.Ports.Select(p => new PortRow(p.Name, p.Description)).ToList(),
                c.Env.Select(e => new EnvGroup(e.File,
                    e.Set.Select(kv => new KvRow(kv.Key, kv.Value)).ToList())).ToList(),
                c.Compose is { } comp
                    ? new ComposeInfo(comp.File,
                        comp.Overrides.Select(o => new KvRow(string.Join(".", o.Path), o.Template)).ToList())
                    : null,
                c.Provides.Select(kv => new KvRow(kv.Key, kv.Value)).ToList(),
                error: null);
        }
        catch (Exception ex)
        {
            return new RepoConfigViewModel("", [], [], null, [], ex.Message);
        }
    }
}
