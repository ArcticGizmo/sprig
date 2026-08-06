using System;
using Avalonia.Controls;
using Avalonia.Input;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

/// <summary>
/// The stack builder in its own resizable window (was an in-page overlay). DataContext is the shared
/// <see cref="StacksViewModel"/> so every binding is unchanged; the wiring canvas fills the window and
/// grows with it. A coach layer over the content lets a guided-tour step still spotlight the canvas and
/// the Create button — wired by the opener via <see cref="AttachCoach"/>.
/// </summary>
public partial class StackEditorWindow : Window
{
    /// <summary>
    /// The most recently opened editor window. The headless renderer captures it because the builder now
    /// lives here rather than inside the main window; the app only ever has one open at a time.
    /// </summary>
    internal static StackEditorWindow? Current { get; private set; }

    public StackEditorWindow()
    {
        InitializeComponent();
        // Block DataContext inheritance so the coach layer stays inert (e.g. static docs captures) until a
        // real run is attached — otherwise it would inherit the window's StacksViewModel and bind nothing.
        Coachmarks.DataContext = null;
        Current = this;
    }

    /// <summary>Point the editor's coach layer at this window's content, bound to the shared coach run.</summary>
    public void AttachCoach(CoachViewModel coach)
    {
        Coachmarks.Secondary = true;   // the main window's layer stays the primary that owns the run
        Coachmarks.DataContext = coach;
        Coachmarks.AnchorRoot = EditorRoot;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (Current == this) Current = null;
        base.OnClosed(e);
    }

    // Escape cancels the edit (matches the old overlay). The cancel flows through the view model, which
    // flips IsCreating and lets the opener close this window.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            (DataContext as StacksViewModel)?.CancelCreateCommand.Execute(null);
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
