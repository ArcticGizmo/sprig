using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Compose;
using Sprig.Core.Config;

namespace Sprig.App.ViewModels;

/// <summary>
/// The interactive compose overlay: renders the docker-compose file with every scalar value
/// clickable, and lets the user replace any of them with a full <c>${sprig.*}</c> template (resolved
/// per workspace at creation time). State is a map of value <em>path</em> → template; the file view
/// always shows the <em>result</em> (the template where set, otherwise the original), and the
/// original is shown only in a value's editor. Seeds from the repo's existing
/// <see cref="ComposeConfig.Overrides"/> and projects back to the same shape via <see cref="ToOverrides"/>.
/// </summary>
public partial class ComposeOverlayViewModel : ObservableObject
{
    private readonly ComposeOutline _outline;
    private readonly Dictionary<string, Entry> _values = new(); // pathKey -> entry

    public ObservableCollection<ComposeLineViewModel> Lines { get; } = new();
    public ObservableCollection<ComposeOverrideViewModel> Overrides { get; } = new();

    /// <summary>The <c>${sprig.*}</c> variable names available to token editors (workspace + inputs).
    /// A live collection owned by the repo editor, bound straight into each token's completion box.</summary>
    public IEnumerable<string> Variables { get; }

    public bool Parsed => _outline.Parsed;
    public string? Error => _outline.Error;
    public bool HasError => !_outline.Parsed;
    public int OverrideCount => Overrides.Count;
    public bool HasOverrides => Overrides.Count > 0;

    private sealed record Entry(IReadOnlyList<string> Path, string Template);

    public ComposeOverlayViewModel(string composeText, IEnumerable<ComposeOverride>? seed = null,
        IEnumerable<string>? variables = null)
    {
        _outline = ComposeScanner.Scan(composeText);
        Variables = variables ?? [];
        SeedFrom(seed);
        Rebuild();
    }

    /// <summary>The current overrides, ready to persist to <see cref="ComposeConfig.Overrides"/>.
    /// Entries whose path is no longer present in the file are preserved (so correcting the compose
    /// path back and forth doesn't silently drop a replacement).</summary>
    public IReadOnlyList<ComposeOverride> ToOverrides() =>
        _outline.Tokens
            .Where(t => _values.ContainsKey(Key(t.Path)))
            .Select(t => _values[Key(t.Path)])
            .Concat(_values.Values.Where(e => !TokenExists(e.Path)))
            .Distinct()
            .Select(e => new ComposeOverride { Path = e.Path.ToList(), Template = e.Template })
            .ToList();

    // -- commands -------------------------------------------------------------

    [RelayCommand]
    private void Apply(ComposeRunViewModel? run)
    {
        if (run is null || !run.IsToken)
            return;
        var template = (run.Draft ?? string.Empty).Trim();
        var key = Key(run.Path);
        if (template.Length == 0 || template == run.OriginalText)
            _values.Remove(key); // no change from the original — nothing to store
        else
            _values[key] = new Entry(run.Path, template);
        Rebuild();
    }

    [RelayCommand]
    private void Remove(ComposeRunViewModel? run)
    {
        if (run is null)
            return;
        _values.Remove(Key(run.Path));
        Rebuild();
    }

    // -- seeding --------------------------------------------------------------

    private void SeedFrom(IEnumerable<ComposeOverride>? seed)
    {
        if (seed is null)
            return;
        foreach (var v in seed)
            _values[Key(v.Path)] = new Entry(v.Path, v.Template);
    }

    // -- projection -----------------------------------------------------------

    private void Rebuild()
    {
        Lines.Clear();
        var runByPath = new Dictionary<string, ComposeRunViewModel>();
        foreach (var line in _outline.Lines)
            Lines.Add(BuildLine(line, runByPath));

        Overrides.Clear();
        foreach (var token in _outline.Tokens.Where(t => _values.ContainsKey(Key(t.Path))))
        {
            var entry = _values[Key(token.Path)];
            runByPath.TryGetValue(Key(token.Path), out var run);
            Overrides.Add(new ComposeOverrideViewModel
            {
                Group = token.Service ?? "compose",
                Label = FieldLabel(token),
                Rewrite = entry.Template,
                Run = run,
            });
        }

        OnPropertyChanged(nameof(OverrideCount));
        OnPropertyChanged(nameof(HasOverrides));
    }

    private ComposeLineViewModel BuildLine(ComposeOutlineLine line, Dictionary<string, ComposeRunViewModel> runByPath)
    {
        var runs = new ObservableCollection<ComposeRunViewModel>();
        var cursor = 0;
        foreach (var token in line.Tokens)
        {
            if (token.StartColumn > cursor)
                runs.Add(ComposeRunViewModel.Plain(line.Text[cursor..token.StartColumn]));
            var run = BuildToken(token);
            runByPath[Key(token.Path)] = run;
            runs.Add(run);
            cursor = token.StartColumn + token.Length;
        }
        runs.Add(ComposeRunViewModel.Plain(cursor < line.Text.Length ? line.Text[cursor..] : string.Empty));
        return new ComposeLineViewModel(runs);
    }

    private ComposeRunViewModel BuildToken(ComposeToken token)
    {
        var applied = _values.TryGetValue(Key(token.Path), out var entry);
        return new ComposeRunViewModel
        {
            IsToken = true,
            Path = token.Path,
            Kind = token.Kind,
            Service = token.Service,
            TargetPort = token.TargetPort,
            VolumeName = token.VolumeName,
            OriginalText = token.Text,
            IsApplied = applied,
            Display = applied ? entry!.Template : token.Text,
            Draft = applied ? entry!.Template : DefaultDraft(token),
        };
    }

    // The editor opens on the current value; the user templates it with autocomplete. (We don't
    // pre-fill a ${sprig.ports.*} guess — a repo references its own declared inputs, not stack ports,
    // so a guessed token would just read as invalid.)
    private static string DefaultDraft(ComposeToken token) => token.Text;

    private static string FieldLabel(ComposeToken token) => token.Kind switch
    {
        ComposeTokenKind.ContainerName => "container name",
        ComposeTokenKind.PublishedPort => $"published port {token.TargetPort}",
        ComposeTokenKind.NamedVolume => $"volume {token.VolumeName}",
        _ => token.Path.Count > 0 ? token.Path[^1] : "value",
    };

    // A pipe can't appear in a compose map key or list index, so joined paths never clash.
    private static string Key(IReadOnlyList<string> path) => string.Join("|", path);
    private bool TokenExists(IReadOnlyList<string> path) => _outline.Tokens.Any(t => Key(t.Path) == Key(path));
}

/// <summary>One rendered line of the compose file: an ordered run of plain text and value tokens.</summary>
public sealed class ComposeLineViewModel
{
    public ObservableCollection<ComposeRunViewModel> Runs { get; }
    public ComposeLineViewModel(ObservableCollection<ComposeRunViewModel> runs) => Runs = runs;
}

/// <summary>A span within a line: literal text (<see cref="IsToken"/> false) or a templatable value.</summary>
public partial class ComposeRunViewModel : ObservableObject
{
    public bool IsToken { get; init; }

    /// <summary>What the file shows here: the template if set, otherwise the original value.</summary>
    public string Display { get; init; } = string.Empty;

    public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();
    public ComposeTokenKind Kind { get; init; }
    public string? Service { get; init; }
    public int? TargetPort { get; init; }
    public string? VolumeName { get; init; }

    /// <summary>The value as written in the file — shown in the editor for comparison.</summary>
    public string OriginalText { get; init; } = string.Empty;

    public bool IsApplied { get; init; }

    /// <summary>The editor's working template, two-way bound to the token text box.</summary>
    [ObservableProperty] private string _draft = string.Empty;

    public static ComposeRunViewModel Plain(string text) => new() { IsToken = false, Display = text };

    public string KindLabel => Kind switch
    {
        ComposeTokenKind.ContainerName => "Container name",
        ComposeTokenKind.PublishedPort => "Published port",
        ComposeTokenKind.NamedVolume => "Named volume",
        _ => "Value",
    };

    public string PopoverName
    {
        get
        {
            var name = Path.Count > 0 ? Path[^1] : "value";
            return Service is null ? name : $"{Service} · {name}";
        }
    }

    public string Note => Kind switch
    {
        ComposeTokenKind.PublishedPort =>
            $"Replace the host port with a declared ${{sprig.<input>}} so parallel workspaces don't collide (container port {TargetPort} stays).",
        ComposeTokenKind.NamedVolume =>
            "Suffix the volume with ${sprig.workspace} so it's renamed per workspace and each gets its own data.",
        ComposeTokenKind.ContainerName =>
            "Add ${sprig.workspace} to keep container names unique across workspaces.",
        _ => "Replace with a ${sprig.*} template — a declared input or ${sprig.workspace}; resolved when a workspace is created.",
    };
}

/// <summary>An applied override, shown in the inspector list (grouped by service).</summary>
public sealed class ComposeOverrideViewModel
{
    public string Group { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Rewrite { get; init; } = string.Empty;
    public ComposeRunViewModel? Run { get; init; }
}
