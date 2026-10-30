using Sprig.Core.Config;
using Sprig.Core.Store;
using Sprig.Core.Substitution;

namespace Sprig.Core.Workspaces;

/// <summary>A browsable entry point a workspace exposes: the capability's <c>open</c> output resolved for this
/// workspace, and its resolved <c>probe</c> URL when it declares one.</summary>
public sealed record WorkspaceUrl(string Repo, string Module, string Capability, string Url, string? Probe);

/// <summary>The result of probing one URL on request.</summary>
public enum ProbeState { Up, Down }

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Every <c>open</c> URL the workspace exposes, read from the record's stored per-module values (so no map
    /// re-resolution) and each worktree's own config (so a branch that adds an entry point shows it). Best-effort:
    /// a repo whose config can't be read, or whose value isn't recorded, contributes nothing.
    /// </summary>
    public IReadOnlyList<WorkspaceUrl> Urls(InstanceRecord record)
    {
        var urls = new List<WorkspaceUrl>();
        foreach (var repo in record.Repos)
        {
            IReadOnlyList<ModuleDeclaration> modules;
            try { modules = SprigConfigLoader.LoadFromFile(repo.WorktreeConfigPath).EffectiveModules; }
            catch (SprigConfigException) { continue; }

            var stored = repo.Modules.ToDictionary(m => m.Name, StringComparer.Ordinal);
            foreach (var module in modules)
            {
                if (!stored.TryGetValue(module.Name, out var scope)) continue;
                foreach (var cap in module.Provides.Where(c => c.Open is not null))
                {
                    if (!scope.Values.TryGetValue($"{cap.Capability}.{cap.Open}", out var url)) continue;
                    urls.Add(new WorkspaceUrl(repo.Name, module.Name, cap.Capability, url,
                        ResolveProbe(cap, record.Workspace, scope.Values)));
                }
            }
        }
        return urls;
    }

    static string? ResolveProbe(ProvidedCapability cap, string workspace, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(cap.Probe)) return null;
        try { return SubstitutionEngine.Resolve(cap.Probe, SprigScope.ForValues(workspace, values)); }
        catch (SubstitutionException) { return null; }
    }

    /// <summary>GET <paramref name="url"/> once with a short timeout: up for any response below 500, down for
    /// a server error, a refused connection or a timeout. Only ever run when a caller asks.</summary>
    public static async Task<ProbeState> ProbeAsync(string url, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        using var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
            return (int)response.StatusCode < 500 ? ProbeState.Up : ProbeState.Down;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return ProbeState.Down;
        }
    }
}
