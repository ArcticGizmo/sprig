using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Sprig.Core.Config;

namespace Sprig.App.ViewModels;

public sealed record InputRow(string Name, string? Example, string? Description, string? AllowedPorts);
public sealed record KvRow(string Key, string Value);
public sealed record EnvGroup(string File, IReadOnlyList<string> Templates, IReadOnlyList<KvRow> Items)
{
    public bool HasTemplates => Templates.Count > 0;
    public string TemplatesSummary => string.Join(", ", Templates);
}
public sealed record ComposeInfo(string File, IReadOnlyList<KvRow> Overrides);

/// <summary>One capability a module provides (map model): its contract name, optional type hint, and its
/// outputs — each shown as <c>name</c> → <c>port</c> or the derived template.</summary>
public sealed record ProvideRow(string Capability, string? Type, IReadOnlyList<KvRow> Outputs);

/// <summary>One capability a module needs (map model): the contract name and the local alias it's
/// referenced by (only shown when it differs from the capability name).</summary>
public sealed record NeedRow(string Capability, string Alias)
{
    public bool ShowAlias => Alias.Length > 0 && Alias != Capability;
    public string AliasLabel => ShowAlias ? $"as {Alias}" : "";
}

/// <summary>One module tab in the read-only view: the module's name/path and a summary of what it
/// <b>provides</b> and <b>needs</b> (the map model), plus its env, compose and setup.</summary>
public sealed record ModuleTabView(
    string Name, string Path,
    IReadOnlyList<ProvideRow> Provides, IReadOnlyList<NeedRow> Needs,
    IReadOnlyList<EnvGroup> Env, IReadOnlyList<ComposeInfo> Compose, IReadOnlyList<string> Setup)
{
    public bool HasPath => Path.Length > 0;
    public bool HasProvides => Provides.Count > 0;
    public bool HasNeeds => Needs.Count > 0;
    public bool HasEnv => Env.Count > 0;
    public bool HasCompose => Compose.Count > 0;
    public bool HasSetup => Setup.Count > 0;
    public bool IsEmpty => Provides.Count == 0 && Needs.Count == 0
        && Env.Count == 0 && Compose.Count == 0 && Setup.Count == 0;
}

/// <summary>
/// Read-only presentation of a repo's <c>.sprig.json</c>: the <b>inputs</b> it needs (shared across
/// modules, shown once at the top), then one tab per <b>module</b>, each summarising where those inputs
/// are used (env overrides, compose overrides, setup). The stack supplies the values — this view just
/// shows what the repo consumes.
/// </summary>
public sealed partial class RepoConfigViewModel : ObservableObject
{
    RepoConfigViewModel(string name, IReadOnlyList<InputRow> inputs, IReadOnlyList<ModuleTabView> modules, string? error)
    {
        Name = name;
        Inputs = inputs;
        Modules = modules;
        Error = error;
        _selectedModule = modules.Count > 0 ? modules[0] : null;
    }

    public string Name { get; }
    public IReadOnlyList<InputRow> Inputs { get; }
    public IReadOnlyList<ModuleTabView> Modules { get; }
    public string? Error { get; }

    /// <summary>The module whose detail is shown below the tab strip.</summary>
    [ObservableProperty] private ModuleTabView? _selectedModule;

    public bool Ok => Error is null;
    public bool HasInputs => Inputs.Count > 0;
    public bool HasModules => Modules.Count > 0;

    public static RepoConfigViewModel Load(string repoPath)
    {
        var configPath = Path.Combine(repoPath, ".sprig.json");
        try
        {
            var c = SprigConfigLoader.LoadFromFile(configPath);
            var modules = c.EffectiveModules.Select(m => new ModuleTabView(
                m.Name,
                m.Path,
                m.Provides.Select(p => new ProvideRow(p.Capability, p.Type,
                    p.Outputs.Select(o => new KvRow(o.Key, o.Value.IsPort
                        ? (string.IsNullOrWhiteSpace(o.Value.Allowed) ? "port" : $"port ({o.Value.Allowed})")
                        : o.Value.Template ?? "")).ToList())).ToList(),
                m.Needs.Select(n => new NeedRow(n.Capability, n.Alias)).ToList(),
                m.Env.Select(e => new EnvGroup(e.File,
                    e.Templates ?? [],
                    e.Set.Select(kv => new KvRow(kv.Key, kv.Value)).ToList())).ToList(),
                m.Compose.Select(comp => new ComposeInfo(comp.File,
                    comp.Overrides.Select(o => new KvRow(string.Join(".", o.Path), o.Template)).ToList())).ToList(),
                m.Setup.ToList())).ToList();
            return new RepoConfigViewModel(
                c.Name,
                c.Inputs.Select(i => new InputRow(i.Name, i.Example, i.Description, i.AllowedPorts)).ToList(),
                modules,
                error: null);
        }
        catch (Exception ex)
        {
            return new RepoConfigViewModel("", [], [], ex.Message);
        }
    }
}
