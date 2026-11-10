using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Sprig.Engine.Orchestrations;
using Sprig.Engine.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>What the new-workspace form checks out: a branch it makes, one that's there already, or none.</summary>
public enum NewCheckout { NewBranch, Existing, NoBranch }

/// <summary>A slot the form can use: <see cref="Slot"/> null for the lowest free one, else a new one.</summary>
public sealed record SlotChoice(int? Slot, string Label);

/// <summary>
/// The new-workspace form asks what to check out first, since each case needs different things: a new branch needs a
/// name and where it starts, an existing one is picked from the branches the repos have (a remote's included), and no
/// branch makes a free slot to claim later. Branches are listed rather than typed, so what can be picked is plain.
/// </summary>
public partial class WorkspacesViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewModeNew), nameof(NewModeExisting), nameof(NewModeNone), nameof(NewShowsFrom), nameof(NewFromLabel),
        nameof(NewShowsSlotChoices), nameof(CreateLabel))]
    private NewCheckout _newMode = NewCheckout.NewBranch;

    public bool NewModeNew
    {
        get => NewMode == NewCheckout.NewBranch;
        set { if (value) NewMode = NewCheckout.NewBranch; }
    }

    public bool NewModeExisting
    {
        get => NewMode == NewCheckout.Existing;
        set { if (value) NewMode = NewCheckout.Existing; }
    }

    public bool NewModeNone
    {
        get => NewMode == NewCheckout.NoBranch;
        set { if (value) NewMode = NewCheckout.NoBranch; }
    }

    public bool NewShowsFrom => NewMode != NewCheckout.Existing;
    public string NewFromLabel => NewMode == NewCheckout.NoBranch ? "AT" : "FROM";
    public string CreateLabel => NewMode == NewCheckout.NoBranch ? "Make the slot" : "Run it";

    /// <summary>The existing branch picked, as git names it (<c>origin/feat/x</c> for a remote's).</summary>
    [ObservableProperty] private string _newExisting = "";

    /// <summary>The branches the form's repos have, local ones first, then each remote's.</summary>
    [ObservableProperty] private IReadOnlyList<BranchChoice> _newBranchChoices = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewShowsSlotChoices))]
    private IReadOnlyList<SlotChoice> _newSlotChoices = [];

    [ObservableProperty] private SlotChoice? _newSlotChoice;

    /// <summary>Worth asking only when there's a free slot to choose between it and a new one; no branch always makes
    /// a new one.</summary>
    public bool NewShowsSlotChoices => NewMode != NewCheckout.NoBranch && NewSlotChoices.Count > 2;

    bool _fillingSlotChoices;

    partial void OnNewModeChanged(NewCheckout value)
    {
        if (IsAddOpen) CheckSlotSoon(immediately: true);
    }

    partial void OnNewExistingChanged(string value)
    {
        if (IsAddOpen) CheckSlotSoon();
    }

    partial void OnNewFromChanged(string value)
    {
        if (IsAddOpen && NewMode == NewCheckout.NoBranch) CheckSlotSoon();
    }

    partial void OnNewSlotChoiceChanged(SlotChoice? value)
    {
        if (_fillingSlotChoices || value is null) return;
        _newSlot = value.Slot;
        if (IsAddOpen) CheckSlotSoon(immediately: true);
    }

    /// <summary>The branch to check out and the remote to take it from, as the mode says; empty when none is given yet.</summary>
    (string Branch, string? Track) NewTarget()
    {
        if (NewMode == NewCheckout.NewBranch) return (NewBranch.Trim(), null);
        if (NewMode == NewCheckout.NoBranch) return ("", null);
        var text = NewExisting.Trim();
        return NewBranchChoices.FirstOrDefault(c => c.Ref == text) is { } choice ? (choice.Name, choice.Remote) : (text, null);
    }

    /// <summary>The lowest free slot (or a new one), each free slot by name, and a new slot, with the one asked for
    /// chosen.</summary>
    void FillSlotChoices(string? id)
    {
        _fillingSlotChoices = true;
        try
        {
            if (id is null)
            {
                NewSlotChoices = [];
                NewSlotChoice = null;
                return;
            }
            var free = _services.Workspaces.List().Where(r => r.Orchestration == id && r.IsFree && WorkspaceService.IsSlot(r.Name))
                .OrderBy(r => int.Parse(r.Name, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var fresh = int.Parse(_services.Workspaces.FreeSlotName(id), System.Globalization.CultureInfo.InvariantCulture);
            NewSlotChoices =
            [
                new SlotChoice(null, free.Length > 0 ? $"The lowest free slot ({free[0].SlotName})" : $"A new slot ({id}--{fresh})"),
                .. free.Select(r => new SlotChoice(int.Parse(r.Name, System.Globalization.CultureInfo.InvariantCulture), $"{r.SlotName}, free")),
                new SlotChoice(fresh, $"A new slot ({id}--{fresh})"),
            ];
            NewSlotChoice = NewSlotChoices.FirstOrDefault(c => c.Slot == _newSlot) ?? NewSlotChoices[0];
        }
        finally
        {
            _fillingSlotChoices = false;
        }
    }

    /// <summary>The branches of the form's repos, read once their definitions are; kept when they haven't changed, so the
    /// open list doesn't reset.</summary>
    async Task LoadBranchChoicesAsync(LoadedOrchestration loaded)
    {
        if (loaded.Orchestration is null) return;
        var choices = await AppServices.RunAsync(() => _services.Workspaces.BranchChoices(loaded));
        if (_newLoaded != loaded) return;
        if (!choices.Select(c => c.Ref).SequenceEqual(NewBranchChoices.Select(c => c.Ref))) NewBranchChoices = choices;
    }

    /// <summary>The line under the form when no branch is checked out: which slot it makes, and where.</summary>
    (string Line, bool Bad) FreeSlotPreview(string id, string? at)
    {
        try
        {
            var name = _services.Workspaces.FreeSlotName(id);
            return ($"Makes {id}--{name}, a free slot, detached at {at ?? "each repo's default branch"}. Nothing is started; check a branch out in it later.", false);
        }
        catch (WorkspaceException e)
        {
            return (e.Message, true);
        }
    }

    /// <summary>A new slot with no branch, its worktrees made now so a branch checked out in it later comes up quicker.</summary>
    async Task CreateFreeSlotAsync(string name, string path)
    {
        var loaded = await LoadAsync(path, Create);
        if (loaded is null) return;
        var at = NewFrom.Trim() is { Length: > 0 } f ? f : null;
        var slot = _services.Workspaces.FreeSlotName(loaded.Id);
        var slotName = $"{loaded.Id}--{slot}";
        IsAddOpen = false;
        await RunAsync($"Making {slotName}, a free slot",
            (progress, cancellation) => _services.Workspaces.MakeFreeSlot(loaded, at, int.Parse(slot, System.Globalization.CultureInfo.InvariantCulture),
                fixSlot: NewFixSlot, progress, cancellation),
            $"{slotName} is free, detached at {at ?? "each repo's default branch"}. Check a branch out in it when you need it.", retry: Create);
        Refresh();
        Selected = Workspaces.FirstOrDefault(s => s.Record.Orchestration == loaded.Id && s.Record.Name == slot) ?? Selected;
    }
}
