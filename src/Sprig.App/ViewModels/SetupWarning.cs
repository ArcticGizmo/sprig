using System.Linq;
using Sprig.Core.Store;

namespace Sprig.App.ViewModels;

/// <summary>Summarises any failed <c>setup</c> command on a freshly created instance, for the soft
/// warning both create paths show (setup failure warns rather than rolling back).</summary>
internal static class SetupWarning
{
    /// <summary>A one-line summary of the first failed setup command across the record's repos, or
    /// <c>null</c> if every setup command succeeded (or none were declared).</summary>
    public static string? Summarize(InstanceRecord record)
    {
        var failed = record.Repos
            .SelectMany(r => r.Setup)
            .FirstOrDefault(s => !s.Success);
        return failed is null ? null : $"setup failed: '{failed.Command}' (exit {failed.ExitCode})";
    }
}
