using Avalonia.Controls;

namespace Sprig.App.Controls;

/// <summary>
/// Renders a repo's docker-compose file with clickable tokens on the values sprig can isolate.
/// A presentation shell over <see cref="ViewModels.ComposeOverlayViewModel"/>; all logic lives there.
/// </summary>
public partial class ComposeOverlay : UserControl
{
    public ComposeOverlay() => InitializeComponent();
}
