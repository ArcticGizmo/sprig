namespace Sprig.Core.Config;

/// <summary>
/// Brings a persisted <see cref="SprigRepoConfig"/> up to the current schema on load. Modules arrived
/// in schema 3; a schema-≤2 file has a flat <see cref="SprigRepoConfig.Env"/>/<see cref="SprigRepoConfig.Compose"/>/<see cref="SprigRepoConfig.Setup"/>
/// surface, so it is lifted into a single default module named <c>"app"</c> (its <c>path</c> is the repo
/// root). Schema-3+ files are trusted as-is (they were written module-shaped and validated), so migration
/// never re-folds over them. The upgrade is in-memory; it persists the next time the config is saved.
/// Forward-tolerant (<c>&gt;=</c>), idempotent.
/// </summary>
public static class SprigConfigMigration
{
    /// <summary>The name given to the single module a schema-≤2 config is folded into.</summary>
    public const string DefaultModuleName = "app";

    public static SprigRepoConfig Normalize(SprigRepoConfig config)
    {
        if (config.Schema >= 3) return config;

        var hasFlat = config.Env is { Count: > 0 } || config.Compose is { Count: > 0 } || config.Setup is { Count: > 0 };
        var modules = hasFlat
            ? new List<ModuleDeclaration>
            {
                new()
                {
                    Name = DefaultModuleName,
                    Path = "",
                    Env = config.Env ?? [],
                    Compose = config.Compose ?? [],
                    Setup = config.Setup ?? [],
                },
            }
            : [];

        return config with
        {
            Schema = 3,
            Modules = modules,
            Env = null,
            Compose = null,
            Setup = null,
        };
    }
}
