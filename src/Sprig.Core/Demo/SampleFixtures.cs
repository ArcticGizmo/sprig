using Sprig.Core.Maps;

namespace Sprig.Core.Demo;

/// <summary>One file written into a scaffolded sample repo, and the embedded resource it comes from.</summary>
/// <param name="RelativePath">Destination path relative to the repo root.</param>
/// <param name="Resource">Manifest resource name inside this assembly.</param>
public sealed record SampleFile(string RelativePath, string Resource);

/// <summary>
/// The guided tour's fixture content: two throwaway repos and the map that composes them.
///
/// Everything here is <b>content, not code</b> — the repo files are embedded verbatim and written to
/// disk unmodified, so nothing can generate them wrongly. They are authored to the same
/// <c>.sprig.json</c> schema a user's repo uses, which means a schema change breaks them; the tests
/// load these exact bytes through the real loader and validator so that breakage fails CI rather
/// than a new user's first launch (see docs/guided-tour-plan.md §7).
/// </summary>
public static class SampleFixtures
{
    /// <summary>Registry name of the sample backend (matches <c>name</c> in its config).</summary>
    public const string ApiRepo = "sample-api";

    /// <summary>Registry name of the sample front end (matches <c>name</c> in its config).</summary>
    public const string WebRepo = "sample-web";

    /// <summary>Name of the map that composes the two sample repos.</summary>
    public const string MapName = "sample";

    const string Prefix = "Sprig.Demo.";

    public static IReadOnlyList<SampleFile> ApiFiles { get; } =
    [
        new(".sprig.json", Prefix + "api.sprig.json"),
        new(".env.template", Prefix + "api.env.template"),
        new(".gitignore", Prefix + "api.gitignore"),
        new("docker-compose.yml", Prefix + "api.docker-compose.yml"),
        new("README.md", Prefix + "api.README.md"),
        // A real subdirectory so the "Split a repo into modules" guide can point a module's path at
        // apps/api and show the green ✓, rather than a "no such directory" warning in a lesson.
        new("apps/api/README.md", Prefix + "api.apps-api.README.md"),
    ];

    public static IReadOnlyList<SampleFile> WebFiles { get; } =
    [
        new(".sprig.json", Prefix + "web.sprig.json"),
        new(".env.template", Prefix + "web.env.template"),
        new(".gitignore", Prefix + "web.gitignore"),
        new("README.md", Prefix + "web.README.md"),
    ];

    /// <summary>
    /// The sample map. It just lists the two repos — the wiring is derived from their own provides/needs:
    /// sample-web NEEDS <c>api</c>, sample-api PROVIDES it (a port plus a <c>url</c> built from that port),
    /// so the map composes them with no explicit wiring or defaults. A pool ceiling so the tour can show
    /// checkout/release.
    /// </summary>
    public static MapDefinition Map() => new()
    {
        Name = MapName,
        Repos = [MapRepo.Local(ApiRepo), MapRepo.Local(WebRepo)],
        MaxSlots = 3,
    };

    /// <summary>Read one embedded fixture as text.</summary>
    public static string Read(string resource)
    {
        var assembly = typeof(SampleFixtures).Assembly;
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException(
                $"missing embedded sample fixture '{resource}' — check the EmbeddedResource LogicalName in Sprig.Core.csproj");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Write a repo's fixture files into <paramref name="repoDir"/>, creating it if needed.</summary>
    public static void WriteTo(IReadOnlyList<SampleFile> files, string repoDir)
    {
        Directory.CreateDirectory(repoDir);
        foreach (var file in files)
        {
            var dest = Path.Combine(repoDir, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, Read(file.Resource));
        }
    }

    /// <summary>Every fixture in the tour, for tests that assert all of them are present and valid.</summary>
    public static IEnumerable<SampleFile> All => ApiFiles.Concat(WebFiles);
}
