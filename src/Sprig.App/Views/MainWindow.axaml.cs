using System;
using Avalonia.Controls;
using Sprig.App.Icons;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // A native Avalonia vector built from sprig.svg, so it stays crisp at any size and DPI.
        LogoImage.Source = SprigLogo.Create();
        UpdateLogoImage.Source = SprigLogo.Create();

        // Coming back from a terminal is when the CLI has most likely changed what the page shows.
        Activated += (_, _) =>
        {
            LastActivated = DateTime.UtcNow;
            if (DataContext is MainWindowViewModel vm) vm.WindowFocused();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == IsVisibleProperty) ShownChanged?.Invoke();
        };
    }

    /// <summary>Open and not minimised: someone could be looking at it, so the app polls quickly on its behalf.</summary>
    public bool IsShown => IsVisible && WindowState != WindowState.Minimized;

    public event Action? ShownChanged;

    /// <summary>When it last came to the front, so the one used last can be brought back where there's no telling
    /// which desktop a window is on.</summary>
    public DateTime LastActivated { get; private set; } = DateTime.UtcNow;
}
