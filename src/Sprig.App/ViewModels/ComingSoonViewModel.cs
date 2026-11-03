namespace Sprig.App.ViewModels;

/// <summary>A page whose milestone hasn't landed yet: says what will live here and when.</summary>
public sealed class ComingSoonViewModel(string title, string heading, string body, string milestone) : PageViewModel
{
    public override string Title { get; } = title;
    public string Heading { get; } = heading;
    public string Body { get; } = body;
    public string Milestone { get; } = milestone;
    public string MilestoneLabel => $"ARRIVES IN {Milestone}";
}
