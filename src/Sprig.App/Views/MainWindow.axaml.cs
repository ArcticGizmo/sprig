using System;
using Avalonia.Controls;
using Sprig.App.Icons;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class MainWindow : Window
{
    MainWindowViewModel? _hooked;

    public MainWindow()
    {
        InitializeComponent();
        // The nav logo is a native Avalonia vector (Avalonia rasterises it through Skia), so it stays
        // crisp at any size/DPI. Built from data generated out of sprig.svg — see SprigLogo.
        LogoImage.Source = SprigLogo.Create();
        DataContextChanged += OnDataContextChanged;
        // Anchors are resolved against the window's own content, so a coachmark can point at anything in it.
        Coachmarks.AnchorRoot = this;
    }

    // The DataContext is swapped when entering/leaving the guided tour, so the hook is re-attached
    // rather than wired once (same pattern as ReposView/WorkspacesView).
    void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_hooked is not null) _hooked.OperationStarted -= ShowProgress;
        _hooked = DataContext as MainWindowViewModel;
        if (_hooked is not null) _hooked.OperationStarted += ShowProgress;
    }

    /// <summary>Open a progress checklist in its own non-blocking window, owned by this one.</summary>
    void ShowProgress(OperationProgressViewModel vm)
        => new OperationProgressWindow { DataContext = vm }.Show(this);
}
