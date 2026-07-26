using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sprig.Core.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>One row in the operation-progress checklist: a label plus a status indicator that
/// transitions Pending → Running (blue spinner) → Done/Warning/Error. Colours and glyph derive from
/// <see cref="State"/> so the view binds a single enum.</summary>
public partial class OperationStepViewModel : ViewModelBase
{
    /// <summary>Live-output view keeps only the last few lines — the tail is where the action is, and a
    /// bounded line count means the newest output is always on screen without scrolling.</summary>
    const int MaxOutputLines = 6;

    public OperationStepViewModel(string id, string label, bool subStep = false)
    {
        Id = id;
        Label = label;
        SubStep = subStep;
    }

    public string Id { get; }

    /// <summary>A child row (one setup command) the view indents under its parent.</summary>
    public bool SubStep { get; }

    /// <summary>Left indent for the row — sub-steps sit under their parent.</summary>
    public Thickness RowMargin => SubStep ? new Thickness(26, 2, 0, 2) : new Thickness(0, 4, 0, 2);

    /// <summary>Sub-step labels are the raw command, so render them monospace and a touch smaller.</summary>
    public FontFamily LabelFont => SubStep ? FontFamily.Parse("Cascadia Code, Consolas, monospace") : FontFamily.Default;
    public double LabelSize => SubStep ? 12 : 13;

    [ObservableProperty] private string _label;

    /// <summary>Short note shown after the label for the Warning/Error states (e.g. the failing command).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(ShowStaticIndicator),
        nameof(IndicatorBrush), nameof(Glyph), nameof(LabelBrush), nameof(ShowOutput))]
    private WorkspaceStepState _state = WorkspaceStepState.Pending;

    /// <summary>Rolling tail of the command's stdout/stderr, shown live while it runs (and kept visible
    /// afterwards if it warned/errored, so the failure is readable).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput), nameof(ShowOutput))]
    private string? _liveOutput;

    public bool HasOutput => !string.IsNullOrEmpty(LiveOutput);

    /// <summary>Show the output box while running, or afterwards if the step didn't cleanly succeed.</summary>
    public bool ShowOutput => HasOutput && State is WorkspaceStepState.Running
        or WorkspaceStepState.Warning or WorkspaceStepState.Error;

    /// <summary>Append one streamed line, keeping only the last <see cref="MaxOutputLines"/>.</summary>
    public void AppendOutput(string line)
    {
        var lines = (LiveOutput ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Append(line);
        LiveOutput = string.Join('\n', lines.TakeLast(MaxOutputLines));
    }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    /// <summary>True while this step runs — the view shows the spinning ring instead of the static circle.</summary>
    public bool IsRunning => State == WorkspaceStepState.Running;

    /// <summary>The static circle is shown for every non-running state.</summary>
    public bool ShowStaticIndicator => State != WorkspaceStepState.Running;

    /// <summary>Circle colour: muted outline until done, then green/yellow/red per outcome.</summary>
    public IBrush IndicatorBrush => State switch
    {
        WorkspaceStepState.Done => Brush("OkBrush"),
        WorkspaceStepState.Warning => Brush("WarnBrush"),
        WorkspaceStepState.Error => Brush("DangerBrush"),
        _ => Brush("MutedBrush"),
    };

    /// <summary>Centre glyph for the terminal states (empty for pending — a hollow circle).</summary>
    public string Glyph => State switch
    {
        WorkspaceStepState.Done => "✓",
        WorkspaceStepState.Warning => "!",
        WorkspaceStepState.Error => "✕",
        _ => "",
    };

    /// <summary>Pending rows are dimmed; once a step has run (or is running) it reads at full strength.</summary>
    public IBrush LabelBrush => State == WorkspaceStepState.Pending ? Brush("MutedBrush") : Brush("FgBrush");

    static IBrush Brush(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var v) && v is IBrush b
            ? b : Brushes.Gray;
}
