namespace Sprig.App.ViewModels;

public partial class ReposViewModel : PageViewModel
{
    protected readonly AppServices Services;

    public ReposViewModel(AppServices services) => Services = services;

    public override string Title => "Repos";
}
