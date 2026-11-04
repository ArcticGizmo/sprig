using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Sprig.Engine.Definitions;
using Sprig.Engine.Store;

namespace Sprig.Engine.Authoring;

/// <param name="Definition">A virtual definition's ID; null for the repo's own sprig.cs.</param>
public sealed record ScaffoldRepo(string Path, string? Definition);

/// <summary>
/// The first draft of a definition, guessed from what's in the folders, so a new one starts from something that
/// plans rather than an empty file. It only ever writes what it can see; everything else is left as a comment.
/// </summary>
public static partial class Scaffold
{
    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build", "out", "artifacts", "packages" };

    public static string Trellis(string name, IReadOnlyList<ScaffoldRepo> repos, string? worktreesFrom, string portPolicy)
    {
        var text = new StringBuilder();
        Header(text);
        text.Append($"var trellis = Trellis.Describe({Literal(name)});\n\n");
        var used = new HashSet<string>(StringComparer.Ordinal) { "trellis", "args" };
        foreach (var repo in repos)
        {
            var variable = Variable(System.IO.Path.GetFileName(repo.Path.TrimEnd('/', '\\')), used);
            var definition = repo.Definition is { } id ? $", definition: {Literal(id)}" : "";
            text.Append($"var {variable} = trellis.Repo({Literal(repo.Path.Replace('\\', '/'))}{definition});\n");
        }
        text.Append('\n');
        text.Append(worktreesFrom is { } from
            ? $"trellis.Worktrees(from: {Literal(from)});        // each sprout gets its own worktrees and branch\n"
            : "// trellis.Worktrees(from: \"main\");      // give each sprout its own worktrees instead of running in place\n");
        text.Append($"trellis.Ports(PortPolicy.{portPolicy});\n\n");
        text.Append("return trellis.Emit(args);\n");
        return text.ToString();
    }

    /// <summary>A repo definition for the folder at <paramref name="repoDir"/>.</summary>
    public static string Repo(string repoDir, string name)
    {
        var text = new StringBuilder();
        Header(text);
        text.Append($"var repo = Repo.Describe({Literal(name)});\n\n");

        var used = new HashSet<string>(StringComparer.Ordinal) { "repo", "args" };
        var found = 0;
        foreach (var project in Files(repoDir, "*.csproj").Where(p => !IsTestProject(p)))
        {
            var dir = Relative(repoDir, System.IO.Path.GetDirectoryName(project)!);
            var appName = Ids.Slug(Kebab(System.IO.Path.GetFileNameWithoutExtension(project)), "app");
            if (IsWebProject(project))
            {
                text.Append($"var {Variable(appName, used)} = repo.DotNet({Literal(appName)}, {Literal(dir)})\n");
                text.Append("    .Http(urlEnv: \"Urls\");\n\n");
                found++;
            }
            else if (File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project)!, "Program.cs")))
            {
                text.Append($"// {appName} ({dir}) isn't a web app. If it should run, as a one-off job say:\n");
                text.Append($"// repo.DotNetJob({Literal(appName)}, {Literal(dir)});\n\n");
            }
        }

        foreach (var package in Files(repoDir, "package.json"))
        {
            var dir = Relative(repoDir, System.IO.Path.GetDirectoryName(package)!);
            if (ReadPackage(package) is not { HasDev: true } info) continue;
            var appName = Ids.Slug(Kebab(info.Name ?? (dir == "." ? name : System.IO.Path.GetFileName(dir))), "web");
            var variable = Variable(appName, used);
            text.Append($"var {variable} = repo.Node({Literal(appName)}, {Literal(dir)}).Http();\n");
            // Vite ignores PORT, so its port goes on the command line.
            text.Append(info.Vite
                ? $"{variable}.Run($\"npm run dev -- --port {{{variable}.Port}}\");\n\n"
                : $"{variable}.EnvFile(\".env.local\", e => e.Set(\"PORT\", {variable}.Port));\n\n");
            found++;
        }

        foreach (var compose in new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" })
        {
            if (!File.Exists(System.IO.Path.Combine(repoDir, compose))) continue;
            text.Append($"// {compose} is here. Sprig runs the services you name, on ports of its own:\n");
            text.Append($"// repo.ComposeFile({Literal(compose)}).Service(\"db\").Endpoint(\"tcp\", targetPort: 5432);\n\n");
            break;
        }

        if (found == 0)
            text.Append("// No apps found to start from. Describe one, for example:\n"
                + "// repo.DotNet(\"api\", \"src/Api\").Http(urlEnv: \"Urls\");\n"
                + "// repo.Node(\"web\", \".\").Http(portEnv: \"PORT\");\n\n");
        text.Append("return repo.Emit(args);\n");
        return text.ToString();
    }

    static void Header(StringBuilder text) => text.Append(SdkReference.Line()).Append("\nusing Sprig;\n\n");

    sealed record PackageInfo(string? Name, bool HasDev, bool Vite);

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
            return new PackageInfo(name?.Split('/')[^1], hasDev, vite("devDependencies") || vite("dependencies"));
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

    /// <summary>A C# local name for <paramref name="name"/> (<c>shop-web</c> → <c>shopWeb</c>) not in
    /// <paramref name="used"/>.</summary>
    static string Variable(string name, HashSet<string> used)
    {
        var parts = Regex.Split(name, "[^A-Za-z0-9]+").Where(p => p.Length > 0).ToArray();
        var variable = parts.Length == 0 ? "app"
            : parts[0].ToLowerInvariant() + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
        if (char.IsDigit(variable[0])) variable = "_" + variable;
        if (SyntaxFacts.GetKeywordKind(variable) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(variable) != SyntaxKind.None)
            variable += "App";
        var candidate = variable;
        for (var n = 2; !used.Add(candidate); n++) candidate = $"{variable}{n}";
        return candidate;
    }

    static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CamelBoundary();
}
