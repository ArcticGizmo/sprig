using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sprig.Engine.Definitions;
using Sprig.Engine.Store;

namespace Sprig.Engine.Authoring;

/// <param name="Path">The repo's folder, for a <c>path:</c> entry.</param>
/// <param name="Use">An entry from the Repos list, for a <c>use:</c> entry instead.</param>
public sealed record ScaffoldRepo(string? Path, string? Use = null);

/// <summary>
/// The first draft of a definition, guessed from what's in the folders, so a new one starts from something that
/// plans rather than an empty file. It only ever writes what it can see; everything else is left as a comment.
/// </summary>
public static partial class Scaffold
{
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build", "out", "artifacts", "packages" };

    public static string Orchestration(string name, IReadOnlyList<ScaffoldRepo> repos)
    {
        var text = new StringBuilder();
        text.Append($"schema: {DefinitionReader.CurrentSchema}\norchestration: {YamlText.Scalar(name)}\n\nrepos:\n");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var repo in repos)
        {
            if (repo.Use is { } use)
            {
                text.Append($"  {Unique(use, used)}: {{ use: {YamlText.Scalar(use)} }}\n");
                continue;
            }
            var path = (repo.Path ?? throw new ArgumentException("a repo needs a path or an entry to use")).Replace('\\', '/');
            text.Append($"  {Unique(Ids.Slug(System.IO.Path.GetFileName(path.TrimEnd('/')), "repo"), used)}: {{ path: {YamlText.Scalar(path)} }}\n");
        }
        return text.ToString();
    }

    /// <summary>A repo definition for the folder at <paramref name="repoDir"/>.</summary>
    public static string Repo(string repoDir, string name)
    {
        var apps = new StringBuilder();
        var files = new StringBuilder();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var found = 0;

        foreach (var project in Files(repoDir, "*.csproj").Where(p => !IsTestProject(p)))
        {
            var projectDir = System.IO.Path.GetDirectoryName(project)!;
            var dir = Relative(repoDir, projectDir);
            var appName = Unique(Ids.Slug(Kebab(System.IO.Path.GetFileNameWithoutExtension(project)), "app"), used);
            if (IsWebProject(project))
            {
                apps.Append($"  {appName}:\n    run: dotnet\n    path: {YamlText.Scalar(dir)}\n    ports: {{ http: any }}\n");
                if (File.Exists(System.IO.Path.Combine(projectDir, "Properties", "launchSettings.json")))
                {
                    files.Append("  # Its launchSettings.json is probably committed, so the url goes in a local settings file. Load it last in\n");
                    files.Append("  # Program.cs: builder.Configuration.AddJsonFile(\"appsettings.Local.json\", optional: true);\n");
                    files.Append($"  {YamlText.Scalar(Join(dir, "appsettings.Local.json"))}:\n");
                }
                else files.Append($"  {YamlText.Scalar(Join(dir, "Properties/launchSettings.json"))}:\n");
                files.Append($"    read-by: [{appName}]\n    values:\n      Urls: ${{{appName}.url}}\n");
                found++;
            }
            else if (File.Exists(System.IO.Path.Combine(projectDir, "Program.cs")))
            {
                apps.Append($"  # {appName} ({dir}) isn't a web app. If it should run, as a one-off job say:\n");
                apps.Append($"  # {appName}:\n  #   run: dotnet\n  #   path: {dir}\n  #   once: true\n");
                used.Remove(appName);
            }
        }

        foreach (var package in Files(repoDir, "package.json"))
        {
            var packageDir = System.IO.Path.GetDirectoryName(package)!;
            var dir = Relative(repoDir, packageDir);
            // A workspace's root only holds its apps, which are found on their own; its dev script starts all of them.
            if (ReadPackage(package) is not { HasDev: true, Workspaces: false } info
                || File.Exists(System.IO.Path.Combine(packageDir, "pnpm-workspace.yaml"))) continue;
            var appName = Unique(Ids.Slug(Kebab(info.Name ?? (dir == "." ? name : System.IO.Path.GetFileName(dir))), "web"), used);
            apps.Append($"  {appName}:\n    run: node\n");
            if (dir != ".") apps.Append($"    path: {YamlText.Scalar(dir)}\n");
            apps.Append("    ports: { http: any }\n");
            files.Append($"  {YamlText.Scalar(Join(dir, ".env.local"))}:\n    read-by: [{appName}]\n    values:\n");
            if (info.Vite)
                files.Append("      # Vite doesn't read PORT by itself. In vite.config, take it from the env file:\n"
                    + "      # server: { port: Number(loadEnv(mode, process.cwd(), '').PORT), strictPort: true }\n");
            files.Append($"      PORT: ${{{appName}.port}}\n");
            found++;
        }

        var text = new StringBuilder();
        text.Append($"schema: {DefinitionReader.CurrentSchema}\nrepo: {YamlText.Scalar(name)}\n");
        foreach (var compose in new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" })
        {
            if (!File.Exists(System.IO.Path.Combine(repoDir, compose))) continue;
            text.Append($"\n# {compose} is here. Sprig runs the services you name, on ports of its own:\n");
            text.Append($"# compose:\n#   {compose}:\n#     services:\n#       db:\n#         ports: {{ tcp: {{ container: 5432 }} }}\n");
            break;
        }
        if (apps.Length > 0) text.Append("\napps:\n").Append(apps);
        if (found == 0)
            text.Append("\n# No apps found to start from. Describe one, for example:\n"
                + "# apps:\n#   api:\n#     run: dotnet\n#     path: src/Api\n#     ports: { http: any }\n"
                + "# files:\n#   src/Api/Properties/launchSettings.json:\n#     read-by: [api]\n#     values:\n#       Urls: ${api.url}\n");
        if (files.Length > 0) text.Append("\nfiles:\n").Append(files);
        return text.ToString();
    }

    static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{name}-{n}";
        return candidate;
    }

    static string Join(string dir, string file) => dir == "." ? file : $"{dir}/{file}";

    sealed record PackageInfo(string? Name, bool HasDev, bool Vite, bool Workspaces);

    static PackageInfo? ReadPackage(string file)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var root = json.RootElement;
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var hasDev = root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("dev", out _);
            var vite = (string section) => root.TryGetProperty(section, out var deps) && deps.ValueKind == JsonValueKind.Object
                && deps.TryGetProperty("vite", out _);
            return new PackageInfo(name?.Split('/')[^1], hasDev, vite("devDependencies") || vite("dependencies"), root.TryGetProperty("workspaces", out _));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static bool IsWebProject(string project)
    {
        try { return File.ReadAllText(project).Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
    }

    static bool IsTestProject(string project) => System.IO.Path.GetFileNameWithoutExtension(project).Contains("Test", StringComparison.OrdinalIgnoreCase);

    /// <summary>Breadth-first and shallow, so a big repo (or one with its node_modules installed) stays quick.</summary>
    static IEnumerable<string> Files(string root, string pattern, int depth = 4)
    {
        var level = new List<string> { root };
        for (var d = 0; d <= depth && level.Count > 0; d++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir, pattern);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var f in files.Order(StringComparer.OrdinalIgnoreCase)) yield return f;
                next.AddRange(dirs.Where(x => !SkipDirs.Contains(System.IO.Path.GetFileName(x)) && !System.IO.Path.GetFileName(x).StartsWith('.')));
            }
            level = next;
        }
    }

    static string Relative(string root, string dir)
    {
        var relative = System.IO.Path.GetRelativePath(root, dir).Replace('\\', '/');
        return relative == "" ? "." : relative;
    }

    /// <summary><c>Catalog.Api</c> → <c>catalog-api</c>.</summary>
    static string Kebab(string name)
        => CamelBoundary().Replace(name, "$1-$2").Replace('.', '-').Replace('_', '-').ToLowerInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CamelBoundary();
}
