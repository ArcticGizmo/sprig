using System;

namespace Sprig.App.ViewModels;

public static class Ago
{
    /// <summary>"just now", "5 min ago", "3 h ago", "yesterday", "3 days ago", "2 weeks ago".</summary>
    public static string Text(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(2)) return "yesterday";
        if (span < TimeSpan.FromDays(14)) return $"{(int)span.TotalDays} days ago";
        return $"{(int)(span.TotalDays / 7)} weeks ago";
    }
}
