using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Workspaces;

namespace Sprig.App.ViewModels;

public enum ProgressState { Pending, Running, Done, Warning, Error }

/// <summary>
/// The progress window: a heading and a checklist that grows as the engine reports steps. Reports arrive through a
/// <c>Progress&lt;T&gt;</c> made on the UI thread, so <see cref="Apply"/> always runs there. <see cref="Finish"/> shows
/// the outcome and lets the window close.
/// </summary>
public partial class OperationProgressViewModel(string heading) : ViewModelBase
{
    public string Heading { get; } = heading;

    public ObservableCollection<OperationStepViewModel> Steps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private bool _canClose;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCancelling;

    readonly CancellationTokenSource _cancellation = new();

    /// <summary>Handed to the work, which stops (killing what it started) once the user cancels.</summary>
    public CancellationToken Cancellation => _cancellation.Token;

    public bool IsRunning => !CanClose;

    /// <summary>A docker that has stopped answering can hold a step forever, so a running operation can always be
    /// abandoned rather than leaving the window stuck open.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        if (!CanCancel()) return;
        IsCancelling = true;
        Summary = "Cancelling…";
        SummaryBrush = Brush("MutedBrush");
        _cancellation.Cancel();
    }

    bool CanCancel() => !CanClose && !IsCancelling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string? _summary;

    [ObservableProperty] private IBrush _summaryBrush = Brush("MutedBrush");

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public void Apply(WorkspaceEvent e)
    {
        if (e.State == StepState.Output)
        {
            var target = Steps.LastOrDefault(s => s.Label == e.Step) ?? Steps.LastOrDefault(s => s.State == ProgressState.Running);
            if (e.Detail is { Length: > 0 } line) target?.AppendOutput(line);
            return;
        }

        var step = Steps.LastOrDefault(s => s.Label == e.Step && s.State is ProgressState.Running or ProgressState.Pending);
        if (step is null)
        {
            step = new OperationStepViewModel(e.Step);
            Steps.Add(step);
        }
        step.State = e.State switch
        {
            StepState.Running => ProgressState.Running,
            StepState.Done => ProgressState.Done,
            StepState.Skipped => ProgressState.Done,
            _ => ProgressState.Error,
        };
        if (e.Detail is { Length: > 0 } detail) step.Detail = detail;
    }

    /// <summary>Settle any step still running, show <paramref name="summary"/>, and allow closing.</summary>
    public void Finish(string summary, ProgressState outcome)
    {
        foreach (var step in Steps.Where(s => s.State == ProgressState.Running))
            step.State = outcome == ProgressState.Error ? ProgressState.Error : ProgressState.Done;
        Summary = summary;
        SummaryBrush = outcome switch
        {
            ProgressState.Warning => Brush("WarnBrush"),
            ProgressState.Error => Brush("DangerBrush"),
            _ => Brush("OkBrush"),
        };
        CanClose = true;
        CancelCommand.NotifyCanExecuteChanged();
    }

    internal static IBrush Brush(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var v) && v is IBrush b ? b : Brushes.Gray;
}

/// <summary>One row of the checklist. Colour and glyph follow <see cref="State"/>, so the view binds one value.</summary>
public partial class OperationStepViewModel(string label) : ViewModelBase
{
    // The tail is where the action is, and a fixed number of lines keeps the newest on screen without scrolling.
    const int MaxOutputLines = 6;

    public string Label { get; } = label;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(ShowStaticIndicator), nameof(IndicatorBrush), nameof(Glyph),
        nameof(LabelBrush), nameof(ShowOutput))]
    private ProgressState _state = ProgressState.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOutput))]
    private string? _liveOutput;

    /// <summary>While the step runs, and afterwards when it failed, so the failure stays readable.</summary>
    public bool ShowOutput => !string.IsNullOrEmpty(LiveOutput) && State is ProgressState.Running or ProgressState.Error;

    public void AppendOutput(string line)
    {
        var lines = (LiveOutput ?? "").Split('\n', System.StringSplitOptions.RemoveEmptyEntries).Append(line.TrimEnd());
        LiveOutput = string.Join('\n', lines.TakeLast(MaxOutputLines));
    }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool IsRunning => State == ProgressState.Running;
    public bool ShowStaticIndicator => State != ProgressState.Running;

    public IBrush IndicatorBrush => State switch
    {
        ProgressState.Done => OperationProgressViewModel.Brush("OkBrush"),
        ProgressState.Warning => OperationProgressViewModel.Brush("WarnBrush"),
        ProgressState.Error => OperationProgressViewModel.Brush("DangerBrush"),
        _ => OperationProgressViewModel.Brush("MutedBrush"),
    };

    public string Glyph => State switch
    {
        ProgressState.Done => "✓",
        ProgressState.Warning => "!",
        ProgressState.Error => "✕",
        _ => "",
    };

    public IBrush LabelBrush => State == ProgressState.Pending ? OperationProgressViewModel.Brush("MutedBrush") : OperationProgressViewModel.Brush("FgBrush");
}
