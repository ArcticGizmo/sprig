using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Config;

namespace Sprig.App.ViewModels;

/// <summary>One editable <c>${sprig.&lt;name&gt;}</c> input declaration.</summary>
public partial class InputEditRow : ObservableObject
{
    readonly Action<InputEditRow> _remove;
    public InputEditRow(Action<InputEditRow> remove) => _remove = remove;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _example = "";
    [ObservableProperty] private string _description = "";

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>One editable <c>KEY = value</c> pair inside an env file.</summary>
public partial class KvEditRow : ObservableObject
{
    readonly Action<KvEditRow> _remove;
    public KvEditRow(Action<KvEditRow> remove) => _remove = remove;

    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>One editable <c>.env.*</c> file plus the keys it clobbers.</summary>
public partial class EnvFileEditRow : ObservableObject
{
    readonly Action<EnvFileEditRow> _remove;
    public EnvFileEditRow(Action<EnvFileEditRow> remove) => _remove = remove;

    [ObservableProperty] private string _file = "";
    public ObservableCollection<KvEditRow> Set { get; } = [];

    [RelayCommand] private void Remove() => _remove(this);
    [RelayCommand] private void AddKey() => Set.Add(new KvEditRow(r => Set.Remove(r)));
}

/// <summary>One editable compose override: a dotted YAML path and its replacement template.</summary>
public partial class ComposeOverrideEditRow : ObservableObject
{
    readonly Action<ComposeOverrideEditRow> _remove;
    public ComposeOverrideEditRow(Action<ComposeOverrideEditRow> remove) => _remove = remove;

    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _template = "";

    [RelayCommand] private void Remove() => _remove(this);
}

/// <summary>
/// Editable view over a repo's <c>.sprig.json</c>. The repo <b>name</b> is intentionally not
/// editable here — it is the registry/stack key, so renaming is a separate operation — but every
/// value it declares (inputs, env overrides, compose overrides) can be changed and saved back.
/// </summary>
public partial class RepoEditViewModel : ObservableObject
{
    int _schema = SprigConfigLoader.SupportedSchema;

    RepoEditViewModel(string repoPath) => RepoPath = repoPath;

    public string RepoPath { get; }

    /// <summary>The logical repo name — shown for context, not editable (it keys the registry/stacks).</summary>
    public string Name { get; private set; } = "";

    public ObservableCollection<InputEditRow> Inputs { get; } = [];
    public ObservableCollection<EnvFileEditRow> Env { get; } = [];
    public ObservableCollection<ComposeOverrideEditRow> ComposeOverrides { get; } = [];

    /// <summary>Whether this repo declares a compose override block.</summary>
    [ObservableProperty] private bool _hasCompose;
    [ObservableProperty] private string _composeFile = "";
    [ObservableProperty] private string? _error;

    public static RepoEditViewModel Load(string repoPath)
    {
        var vm = new RepoEditViewModel(repoPath);
        var c = SprigConfigLoader.LoadFromFile(Path.Combine(repoPath, ".sprig.json"));

        vm._schema = c.Schema;
        vm.Name = c.Name;

        foreach (var i in c.Inputs)
            vm.Inputs.Add(new InputEditRow(vm.RemoveInputRow)
            {
                Name = i.Name,
                Example = i.Example ?? "",
                Description = i.Description ?? "",
            });

        foreach (var e in c.Env)
        {
            var file = new EnvFileEditRow(vm.RemoveEnvRow) { File = e.File };
            foreach (var kv in e.Set)
                file.Set.Add(new KvEditRow(r => file.Set.Remove(r)) { Key = kv.Key, Value = kv.Value });
            vm.Env.Add(file);
        }

        if (c.Compose is { } comp)
        {
            vm.HasCompose = true;
            vm.ComposeFile = comp.File;
            foreach (var o in comp.Overrides)
                vm.ComposeOverrides.Add(new ComposeOverrideEditRow(vm.RemoveComposeRow)
                {
                    Path = string.Join(".", o.Path),
                    Template = o.Template,
                });
        }

        return vm;
    }

    void RemoveInputRow(InputEditRow r) => Inputs.Remove(r);
    void RemoveEnvRow(EnvFileEditRow r) => Env.Remove(r);
    void RemoveComposeRow(ComposeOverrideEditRow r) => ComposeOverrides.Remove(r);

    [RelayCommand] private void AddInput() => Inputs.Add(new InputEditRow(RemoveInputRow));

    [RelayCommand]
    private void AddEnvFile()
    {
        var file = new EnvFileEditRow(RemoveEnvRow);
        file.Set.Add(new KvEditRow(r => file.Set.Remove(r)));
        Env.Add(file);
    }

    [RelayCommand]
    private void AddComposeOverride() => ComposeOverrides.Add(new ComposeOverrideEditRow(RemoveComposeRow));

    /// <summary>Reconstruct a full config from the edited fields (round-trips every declared value).</summary>
    public SprigRepoConfig Build() => new()
    {
        Schema = _schema,
        Name = Name,
        Inputs = Inputs.Select(i => new InputDeclaration
        {
            Name = i.Name.Trim(),
            Example = Blank(i.Example),
            Description = Blank(i.Description),
        }).ToList(),
        Env = Env.Select(e => new EnvOverride
        {
            File = e.File.Trim(),
            Set = ToDict(e.Set),
        }).ToList(),
        Compose = HasCompose
            ? new ComposeConfig
            {
                File = ComposeFile.Trim(),
                Overrides = ComposeOverrides.Select(o => new ComposeOverride
                {
                    Path = o.Path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    Template = o.Template,
                }).ToList(),
            }
            : null,
    };

    /// <summary>Validate the edited config and, if valid, write it back to <c>.sprig.json</c>.</summary>
    /// <returns>True on success; otherwise <see cref="Error"/> holds the reason.</returns>
    public bool Save()
    {
        var config = Build();

        var result = SprigConfigValidator.Validate(config);
        if (!result.IsValid)
        {
            Error = string.Join("\n", result.Issues.Select(i => i.ToString()));
            return false;
        }

        try { ConfigJson.Write(config, Path.Combine(RepoPath, ".sprig.json")); }
        catch (Exception ex) { Error = ex.Message; return false; }

        Error = null;
        return true;
    }

    static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Last value wins on a duplicate key — the validator has no cross-key check, and a dict can't
    // hold duplicates anyway; this just avoids throwing while the user is mid-edit.
    static Dictionary<string, string> ToDict(IEnumerable<KvEditRow> rows)
    {
        var dict = new Dictionary<string, string>();
        foreach (var r in rows)
        {
            var key = r.Key.Trim();
            if (key.Length > 0) dict[key] = r.Value;
        }
        return dict;
    }
}
