using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sprig.App.ViewModels;

/// <summary>
/// One tickable repo in the create-workspace form. Unticking it leaves that repo out of the
/// workspace entirely — no worktree, no env, no compose — which is what makes the workspace
/// <i>partial</i>. Reports every toggle back to the form so it can restate the consequences
/// (which repos are dropped, which stack ports that orphans).
/// <para>Distinct from the stack builder's <see cref="RepoChoiceViewModel"/>: that one picks the
/// repos a stack is <i>made of</i>, this one picks which of them to stand up this time.</para>
/// </summary>
public partial class WorkspaceRepoChoiceViewModel(string name, Action onChanged) : ViewModelBase
{
    public string Name { get; } = name;

    /// <summary>Ticked by default: the no-decision path creates the whole stack.</summary>
    [ObservableProperty] private bool _included = true;

    partial void OnIncludedChanged(bool value) => onChanged();
}
