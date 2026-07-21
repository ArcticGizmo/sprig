using Avalonia.Controls;
using Sprig.App.Icons;

namespace Sprig.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // The nav logo is a native Avalonia vector (Avalonia rasterises it through Skia), so it stays
        // crisp at any size/DPI. Built from data generated out of sprig.svg — see SprigLogo.
        LogoImage.Source = SprigLogo.Create();
    }
}
