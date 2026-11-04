using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Sprig.App.Icons;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class MainWindow : Window
{
    static readonly TimeSpan Visible = TimeSpan.FromSeconds(1);
    static readonly TimeSpan Minimised = TimeSpan.FromSeconds(5);

    readonly DispatcherTimer _hostPoll = new() { Interval = Visible };

    public MainWindow()
    {
        InitializeComponent();
        // A native Avalonia vector built from sprig.svg, so it stays crisp at any size and DPI.
        LogoImage.Source = SprigLogo.Create();
        UpdateLogoImage.Source = SprigLogo.Create();

        _hostPoll.Tick += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm) _ = vm.HostStatus.PollAsync();
        };
        Opened += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm) _ = vm.HostStatus.PollAsync();
            _hostPoll.Start();
        };
        Closed += (_, _) => _hostPoll.Stop();
        // Hidden in the tray it still polls, so the tray's count of running apps stays true, just less often.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == IsVisibleProperty)
                _hostPoll.Interval = WindowState == WindowState.Minimized || !IsVisible ? Minimised : Visible;
        };
    }
}
