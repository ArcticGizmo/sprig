using Avalonia.Controls;
using Sprig.App.Icons;

namespace Sprig.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // A native Avalonia vector built from sprig.svg, so it stays crisp at any size and DPI.
        LogoImage.Source = SprigLogo.Create();
        UpdateLogoImage.Source = SprigLogo.Create();
    }
}
