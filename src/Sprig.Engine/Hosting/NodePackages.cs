using System.Text.Json;

namespace Sprig.Engine.Hosting;

/// <summary>
/// Which tool installs a Node app's packages, and where. pnpm when the app's folder has no npm lockfile of its own and
/// it or a folder above it in the repo has pnpm's lockfile or workspace file, or its package.json names pnpm as its
/// <c>packageManager</c>; npm otherwise.
/// </summary>
/// <param name="Root">Where the install runs and its node_modules lives: a pnpm workspace's folder for any of its apps.</param>
/// <param name="Lockfile">The lockfile the install follows; null when there's none, so it can't be a frozen install.</param>
public sealed partial record NodePackages(bool Pnpm, string Root, string? Lockfile)
{
    const string PnpmLock = "pnpm-lock.yaml";
    const string NpmLock = "package-lock.json";

    public static NodePackages Of(string appDir, string repoDir)
    {
        appDir = Path.GetFullPath(appDir);
        if (File.Exists(Path.Combine(appDir, NpmLock))) return new(false, appDir, Path.Combine(appDir, NpmLock));

        // The search stops at the repo's folder, so a lockfile outside it never decides.
        var repo = Path.GetFullPath(repoDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var dir = appDir; dir is not null && dir.StartsWith(repo, comparison); dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, PnpmLock))) return new(true, dir, Path.Combine(dir, PnpmLock));
            if (File.Exists(Path.Combine(dir, "pnpm-workspace.yaml"))) return new(true, dir, null);
        }
        return new(NamesPnpm(Path.Combine(appDir, "package.json")), appDir, null);
    }

    /// <summary>
    /// Whether node_modules is there and was installed since the lockfile last changed, judged by the file each tool
    /// writes into node_modules as it finishes. Checking out another branch rewrites a lockfile that differs, so a
    /// slot moving to a branch with new packages installs again.
    /// </summary>
    public bool Installed()
    {
        var modules = Path.Combine(Root, "node_modules");
        if (!Directory.Exists(modules)) return false;
        if (Lockfile is null) return true;
        var marker = new FileInfo(Path.Combine(modules, Pnpm ? ".modules.yaml" : ".package-lock.json"));
        return marker.Exists && marker.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(Lockfile);
    }

    /// <summary>Whether the pnpm workspace lists which packages may run install scripts (<c>onlyBuiltDependencies</c>, or
    /// <c>allowBuilds</c> in later pnpm). A list that only blocks some doesn't count.</summary>
    public bool ListsBuilds()
    {
        try
        {
            var file = Path.Combine(Root, "pnpm-workspace.yaml");
            return Pnpm && File.Exists(file) && File.ReadLines(file).Any(l => AllowList().IsMatch(l));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(onlyBuiltDependencies|allowBuilds)\s*:")]
    private static partial System.Text.RegularExpressions.Regex AllowList();

    static bool NamesPnpm(string packageJson)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(packageJson));
            return json.RootElement.TryGetProperty("packageManager", out var pm) && pm.ValueKind == JsonValueKind.String
                && pm.GetString()!.StartsWith("pnpm@", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
