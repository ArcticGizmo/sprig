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

/// <summary>One module tab in the read-only view: the module's name/path and a summary of its env,
/// compose and setup. Inputs are not here — they are shared and shown once above the tabs.</summary>
public sealed record ModuleTabView(
    string Name, string Path,
    IReadOnlyList<EnvGroup> Env, IReadOnlyList<ComposeInfo> Compose, IReadOnlyList<string> Setup)
{
    public bool HasPath => Path.Length > 0;
    public bool HasEnv => Env.Count > 0;
    public bool HasCompose => Compose.Count > 0;
    public bool HasSetup => Setup.Count > 0;
    public bool IsEmpty => Env.Count == 0 && Compose.Count == 0 && Setup.Count == 0;
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
