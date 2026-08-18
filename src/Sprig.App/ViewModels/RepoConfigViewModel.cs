using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sprig.Core.Config;

namespace Sprig.App.ViewModels;

public sealed record KvRow(string Key, string Value);
public sealed record EnvGroup(string File, IReadOnlyList<string> Templates, IReadOnlyList<KvRow> Items)
{
    public bool HasTemplates => Templates.Count > 0;
    public string TemplatesSummary => string.Join(", ", Templates);
}
public sealed record ComposeInfo(string File, IReadOnlyList<KvRow> Overrides);

/// <summary>One output of a provided capability in the read-only preview: the reference a consumer types
/// (<see cref="Reference"/>), whether it's the always-present port anchor (green) or a derived shape (amber),
/// and its detail (allowed port set, or the shape's template). Mirrors an editor provides output row.</summary>
public sealed record ProvideOutputRow(string Name, string Reference, bool IsPort, string Detail)
{
    public string Badge => IsPort ? "● port" : "● derived";
    public IBrush BannerBrush => EditBrush.Of(IsPort ? "OkBrush" : "WarnBrush");
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>One capability a module provides (map model): its name (the <c>${sprig.&lt;capability&gt;}</c>
/// namespace) and its outputs.</summary>
public sealed record ProvideRow(string Capability, IReadOnlyList<ProvideOutputRow> Outputs);

/// <summary>One output of a needed value the module references, with where it's referenced — discovered
/// from the module's env/compose overrides, exactly as the editor does.</summary>
public sealed record NeedUsageRow(string Output, string Reference, IReadOnlyList<string> Locations)
{
    public string LocationsSummary => string.Join("   ", Locations);
    public bool HasLocations => Locations.Count > 0;
}

/// <summary>One value a module needs (map model): the value name (the <c>${sprig.&lt;value&gt;}</c>
/// namespace) and the outputs it's referenced with.</summary>
public sealed record NeedRow(string Value, IReadOnlyList<NeedUsageRow> Usages)
{
    public bool HasUsages => Usages.Count > 0;
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
/// Read-only presentation of a repo's <c>.sprig.json</c> (the map model): one tab per <b>module</b>, each
/// summarising what it <b>provides</b> and <b>needs</b> and where those capabilities are used (env
/// overrides, compose overrides, setup).
/// </summary>
public sealed partial class RepoConfigViewModel : ObservableObject
{
    RepoConfigViewModel(string name, IReadOnlyList<ModuleTabView> modules, string? error, string? validationError = null)
    {
        Name = name;
        Modules = modules;
        Error = error;
        ValidationError = validationError;
        _selectedModule = modules.Count > 0 ? modules[0] : null;
    }

    public string Name { get; }
    public IReadOnlyList<ModuleTabView> Modules { get; }

    /// <summary>Set when the <c>.sprig.json</c> couldn't be read/parsed at all — the config is unusable.</summary>
    public string? Error { get; }

    /// <summary>Set when the config parsed but failed validation — e.g. it was written by an older sprig whose
    /// schema this build no longer understands. The read-only view shows it as a fixable warning (edit or
    /// delete), rather than pretending the config is fine and letting a checkout blow up downstream.</summary>
    public string? ValidationError { get; }

    /// <summary>The module whose detail is shown below the tab strip.</summary>
    [ObservableProperty] private ModuleTabView? _selectedModule;

    public bool Ok => Error is null;

    /// <summary>Parsed AND valid — the only state from which a workspace can be safely checked out.</summary>
    public bool IsValid => Error is null && ValidationError is null;

    /// <summary>True when the config parsed but is invalid — drives the fixable-warning banner.</summary>
    public bool HasValidationError => Error is null && ValidationError is not null;

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
                m.Provides.Select(p => new ProvideRow(p.Capability,
                    p.Ports.Select(o => new ProvideOutputRow(o.Key, ProvideEditRow.Token(p.Capability, o.Key), true,
                            string.IsNullOrWhiteSpace(o.Value.Allowed) ? "" : o.Value.Allowed!))
                        .Concat(p.Shapes.Select(sp => new ProvideOutputRow(sp.Key, ProvideEditRow.Token(p.Capability, sp.Key), false, sp.Value)))
                        .ToList())).ToList(),
                m.Needs.Select(n => new NeedRow(n.Value, UsagesFor(m, n.Value))).ToList(),
                m.Env.Select(e => new EnvGroup(e.File,
                    e.Templates ?? [],
                    e.Set.Select(kv => new KvRow(kv.Key, kv.Value)).ToList())).ToList(),
                m.Compose.Select(comp => new ComposeInfo(comp.File,
                    comp.Overrides.Select(o => new KvRow(string.Join(".", o.Path), o.Template)).ToList())).ToList(),
                m.Setup.ToList())).ToList();

            // A config can parse yet be invalid — most commonly one written by an older sprig (e.g. the
            // pre-"ports/shapes" `outputs` shape). Surface that as a fixable warning here rather than letting
            // it read as fine and crash a later checkout.
            var validation = SprigConfigValidator.Validate(c);
            var validationError = validation.IsValid
                ? null
                : "This repo's .sprig.json doesn't match the current schema — it was likely written by an "
                  + "older sprig. Open it in the editor to fix it, or delete the config and re-add the repo to "
                  + "scaffold a fresh one.\n\n  • " + string.Join("\n  • ", validation.Issues);

            return new RepoConfigViewModel(c.Name, modules, error: null, validationError);
        }
        catch (Exception ex)
        {
            return new RepoConfigViewModel("", [], ex.Message);
        }
    }

    /// <summary>The outputs a module references a given need <paramref name="value"/> with, and where —
    /// read off its env/compose override templates (the read-only analogue of the editor's per-need usage
    /// discovery, so the preview shows the same value → outputs shape as editing does).</summary>
    static IReadOnlyList<NeedUsageRow> UsagesFor(ModuleDeclaration mod, string value)
    {
        var order = new List<string>();
        var byOutput = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Scan(string template, string location)
        {
            foreach (var reference in ConfigReferences.ReferencedNames(template))
            {
                var dot = reference.IndexOf('.');
                if (dot <= 0 || reference[..dot] != value) continue;
                var output = reference[(dot + 1)..].Trim();
                if (output.Length == 0) continue;
                if (!byOutput.TryGetValue(output, out var locs)) { byOutput[output] = locs = []; order.Add(output); }
                if (!locs.Contains(location)) locs.Add(location);
            }
        }

        foreach (var e in mod.Env)
        {
            var f = string.IsNullOrWhiteSpace(e.File) ? ".env" : e.File.Trim();
            foreach (var (k, t) in e.Set) Scan(t, $"{f} · {k}");
        }
        foreach (var comp in mod.Compose)
        {
            var f = string.IsNullOrWhiteSpace(comp.File) ? "compose" : comp.File.Trim();
            foreach (var o in comp.Overrides) Scan(o.Template, $"{f} · {string.Join(".", o.Path)}");
        }

        return order.Select(o => new NeedUsageRow(o, ProvideEditRow.Token(value, o), byOutput[o])).ToList();
    }
}
