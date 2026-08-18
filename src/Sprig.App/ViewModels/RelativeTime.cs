namespace Sprig.App.ViewModels;

/// <summary>Short "3d ago"-style relative timestamps for list rows (branches, commits).</summary>
static class RelativeTime
{
    public static string Format(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
        return $"{(int)(d.TotalDays / 30)}mo ago";
    }
}
