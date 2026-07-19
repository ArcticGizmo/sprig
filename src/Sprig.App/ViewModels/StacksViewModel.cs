namespace Sprig.App.ViewModels;

public partial class StacksViewModel : PageViewModel
{
    protected readonly AppServices Services;

    public StacksViewModel(AppServices services) => Services = services;

    public override string Title => "Stacks";
}
