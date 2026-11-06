using System.Collections.Generic;
using System.Linq;

namespace Sprig.App.ViewModels;

public enum ChecklistState { Done, Open, Waiting }

/// <summary>What a step's button does; the page turns it into a command.</summary>
public enum StepAction { None, Review, EditCode, ShowValues, Start }

/// <summary>One step on the way to an orchestration's first workspace.</summary>
public sealed record ChecklistStep(int Number, string Title, string Detail, ChecklistState State, StepAction Action = StepAction.None, string ActionLabel = "")
{
    public bool IsDone => State == ChecklistState.Done;
    public bool IsOpen => State == ChecklistState.Open;
    public bool IsWaiting => State == ChecklistState.Waiting;
    public bool HasAction => Action != StepAction.None && State == ChecklistState.Open;
    public string Marker => State == ChecklistState.Done ? "✓" : State == ChecklistState.Open ? "!" : Number.ToString();
}

/// <summary>
/// The steps between an orchestration and a running workspace, each with whether it's done and the one thing that
/// clears it. A step after one that's open waits, since what it would say isn't known until the one before is cleared.
/// </summary>
public static class OrchestrationChecklist
{
    /// <param name="pendingApprovals">Definitions that haven't run because their content isn't approved.</param>
    /// <param name="loadProblems">Problems that stopped the orchestration from planning at all.</param>
    /// <param name="cards">The orchestration's repos, as far as they're known.</param>
    /// <param name="planned">Whether it got as far as a plan.</param>
    /// <param name="otherErrors">The plan's errors.</param>
    public static IReadOnlyList<ChecklistStep> Steps(int pendingApprovals, int loadProblems, IReadOnlyList<RepoCard> cards, bool planned, int otherErrors)
    {
        var steps = new List<ChecklistStep>();
        var blocked = false;

        void Add(string title, bool done, string doneText, string openText, StepAction action = StepAction.None, string label = "")
        {
            var number = steps.Count + 1;
            if (blocked) steps.Add(new ChecklistStep(number, title, "Once the step above is done.", ChecklistState.Waiting));
            else if (done) steps.Add(new ChecklistStep(number, title, doneText, ChecklistState.Done));
            else
            {
                steps.Add(new ChecklistStep(number, title, openText, ChecklistState.Open, action, label));
                blocked = true;
            }
        }

        Add("Review the code", pendingApprovals == 0,
            "Every file it runs is approved.",
            $"{Count(pendingApprovals, "file")} to read and approve. Nothing runs until {(pendingApprovals == 1 ? "it's" : "they're")} approved.",
            StepAction.Review, $"Review {Count(pendingApprovals, "file")}");

        var notRun = cards.Where(c => !c.HasModel).ToArray();
        Add("Its definitions run", loadProblems == 0 && notRun.Length == 0,
            cards.Count == 1 ? "The orchestration and its repo both run." : $"The orchestration and its {cards.Count} repos all run.",
            notRun.Length > 0
                ? $"{string.Join(", ", notRun.Select(c => c.Name))}: {notRun[0].Problem}."
                : $"{Count(loadProblems, "problem")} {(loadProblems == 1 ? "stops" : "stop")} it planning; see below.",
            StepAction.EditCode, "Open the code");

        Add("The plan checks out", planned && otherErrors == 0,
            "No problems.",
            planned ? $"{Count(otherErrors, "problem")} to fix; each says where." : "It hasn't planned yet.",
            StepAction.ShowValues, "See them on Values");

        Add("Start a workspace", false, "", "Ready: make a workspace of it.", StepAction.Start, "New workspace");
        return steps;
    }

    /// <summary>Only worth showing while something is in the way.</summary>
    public static bool Blocks(IReadOnlyList<ChecklistStep> steps) => steps.Any(s => s.State == ChecklistState.Open && s.Action != StepAction.Start);

    static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
}
