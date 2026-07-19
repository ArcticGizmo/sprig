namespace Sprig.App.ViewModels;

public partial class WorkspacesViewModel : PageViewModel
{
    protected readonly AppServices Services;

    public WorkspacesViewModel(AppServices services) => Services = services;

    public override string Title => "Workspaces";
}
