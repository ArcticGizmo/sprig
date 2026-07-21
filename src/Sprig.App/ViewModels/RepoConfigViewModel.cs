using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sprig.Core.Config;

namespace Sprig.App.ViewModels;

public sealed record InputRow(string Name, string? Example, string? Description);
public sealed record KvRow(string Key, string Value);
public sealed record EnvGroup(string File, IReadOnlyList<KvRow> Items);
public sealed record ComposeInfo(string File, IReadOnlyList<KvRow> Overrides);

/// <summary>
/// Read-only presentation of a repo's <c>.sprig.json</c>: the <b>inputs</b> it needs (name +
/// example), and where it uses them (env overrides, compose overrides). The stack supplies the
/// values — this view just shows what the repo consumes.
/// </summary>
public sealed class RepoConfigViewModel
{
    RepoConfigViewModel(string name, IReadOnlyList<InputRow> inputs, IReadOnlyList<EnvGroup> env,
        ComposeInfo? compose, string? error)
    {
        Name = name;
        Inputs = inputs;
        Env = env;
        Compose = compose;
        Error = error;
    }

    public string Name { get; }
    public IReadOnlyList<InputRow> Inputs { get; }
    public IReadOnlyList<EnvGroup> Env { get; }
    public ComposeInfo? Compose { get; }
    public string? Error { get; }

    public bool Ok => Error is null;
    public bool HasInputs => Inputs.Count > 0;
    public bool HasEnv => Env.Count > 0;
    public bool HasCompose => Compose is not null;

    public static RepoConfigViewModel Load(string repoPath)
    {
        var configPath = Path.Combine(repoPath, ".sprig.json");
        try
        {
            var c = SprigConfigLoader.LoadFromFile(configPath);
            return new RepoConfigViewModel(
                c.Name,
                c.Inputs.Select(i => new InputRow(i.Name, i.Example, i.Description)).ToList(),
                c.Env.Select(e => new EnvGroup(e.File,
                    e.Set.Select(kv => new KvRow(kv.Key, kv.Value)).ToList())).ToList(),
                c.Compose is { } comp
                    ? new ComposeInfo(comp.File,
                        comp.Overrides.Select(o => new KvRow(string.Join(".", o.Path), o.Template)).ToList())
                    : null,
                error: null);
        }
        catch (Exception ex)
        {
            return new RepoConfigViewModel("", [], [], null, ex.Message);
        }
    }
}
