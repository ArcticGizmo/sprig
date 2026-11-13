using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Sprig.App.ViewModels;

namespace Sprig.App.Views;

public partial class UsageView : UserControl
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    public UsageView()
    {
        InitializeComponent();
        _poll.Tick += (_, _) =>
        {
            if (DataContext is UsageViewModel { IsActive: true } vm)
                _ = vm.PollAsync(usage: TopLevel.GetTopLevel(this) is Window { IsVisible: true, WindowState: not WindowState.Minimized });
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _poll.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _poll.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
