using Avalonia.Controls;

namespace Sprig.App.Controls;

/// <summary>
/// Renders a repo's <c>.env</c> file as the merged aggregate of the file and its templates, with a
/// clickable token on every value to override it. A presentation shell over
/// <see cref="ViewModels.EnvOverlayViewModel"/>; all logic lives there.
/// </summary>
public partial class EnvOverlay : UserControl
{
    public EnvOverlay() => InitializeComponent();
}
