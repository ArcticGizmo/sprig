using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Sprig.Core.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>
/// Drives the non-blocking operation-progress window: a heading plus a live checklist of
/// <see cref="OperationStepViewModel"/> rows. The owning page seeds it from a plan
/// (<see cref="Load"/>), feeds it per-step reports off the background thread via <see cref="Apply"/>
/// (safe: the report is marshalled to the UI thread by <c>Progress&lt;T&gt;</c>), and calls
/// <see cref="Finish"/> when the operation has gone as far as it can — which enables the Close button.
/// </summary>
public partial class OperationProgressViewModel : ViewModelBase
{
    public OperationProgressViewModel(string heading)
    {
        Heading = heading;
    }

    public string Heading { get; }

    public ObservableCollection<OperationStepViewModel> Steps { get; } = [];

    /// <summary>Enables the Close button — the operation has finished (success, warning, or error).</summary>
    [ObservableProperty] private bool _canClose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    private string? _summary;

    /// <summary>Colour of the final summary line: green / yellow / red for the overall outcome.</summary>
    [ObservableProperty] private IBrush _summaryBrush = Brush("MutedBrush");

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    /// <summary>Populate the checklist from a service-computed plan (every row starts Pending).</summary>
    public void Load(IEnumerable<WorkspaceStep> plan)
    {
        foreach (var step in plan)
            Steps.Add(new OperationStepViewModel(step.Id, step.Label, step.SubStep));
    }

    /// <summary>Append an extra row the page drives itself (e.g. "Start infrastructure"), returning it
    /// so the caller can transition its state.</summary>
    public OperationStepViewModel AddStep(string id, string label)
    {
        var vm = new OperationStepViewModel(id, label);
        Steps.Add(vm);
        return vm;
    }

    /// <summary>Apply one progress report to its row (the <see cref="IProgress{T}"/> sink). A report
    /// carrying <see cref="WorkspaceStepProgress.Output"/> is a streamed line — append it; otherwise it's
    /// a state transition.</summary>
    public void Apply(WorkspaceStepProgress report)
    {
        if (Steps.FirstOrDefault(s => s.Id == report.StepId) is not { } step) return;

        if (report.Output is { } line)
        {
            step.AppendOutput(line);
            return;
        }

        step.State = report.State;
        if (report.Detail is { Length: > 0 }) step.Detail = report.Detail;
    }

    /// <summary>Mark the operation complete: any rows still Pending/Running are settled, the summary is
    /// shown, and Close becomes available.</summary>
    public void Finish(string summary, WorkspaceStepState outcome)
    {
        // A hard failure leaves later rows unreached; a success/warning means every row ran.
        foreach (var step in Steps)
        {
            if (step.State == WorkspaceStepState.Running)
                step.State = outcome == WorkspaceStepState.Error ? WorkspaceStepState.Error : WorkspaceStepState.Done;
            else if (step.State == WorkspaceStepState.Pending && outcome == WorkspaceStepState.Error)
                step.State = WorkspaceStepState.Pending; // unreached — leave hollow
        }
        Summary = summary;
        SummaryBrush = outcome switch
        {
            WorkspaceStepState.Warning => Brush("WarnBrush"),
            WorkspaceStepState.Error => Brush("DangerBrush"),
            _ => Brush("OkBrush"),
        };
        CanClose = true;
    }

    static IBrush Brush(string key)
        => Application.Current is { } app && app.TryFindResource(key, out var v) && v is IBrush b
            ? b : Brushes.Gray;
}
