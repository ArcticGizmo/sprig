using System.Text.Json.Nodes;
using Sprig.Engine.Workspaces;

namespace Sprig.Tests.Workspaces;

public sealed class EnvFilesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sprig-envfiles-" + Guid.NewGuid().ToString("N"));

    public EnvFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    static List<KeyValuePair<string, string>> Values(params (string k, string v)[] values)
        => values.Select(x => KeyValuePair.Create(x.k, x.v)).ToList();

    [Fact]
    public void A_dotenv_block_wraps_the_developers_lines_and_comes_back_out_cleanly()
    {
        var file = Path.Combine(_dir, ".env.local");
        File.WriteAllText(file, "SECRET=mine\n# a comment\n");

        EnvFiles.WriteDotEnv(file, Values(("PORT", "5171"), ("NOTE", "has space")), seed: null);
        EnvFiles.WriteDotEnv(file, Values(("PORT", "5172")), seed: null);

        var lines = File.ReadAllLines(file);
        Assert.Equal([EnvFiles.BeginMarker, "PORT=5172", EnvFiles.EndMarker, "SECRET=mine", "# a comment", EnvFiles.BeginMarker, "PORT=5172", EnvFiles.EndMarker], lines);

        EnvFiles.RemoveDotEnv(new WrittenFile(file, "dotenv", Created: false));
        Assert.Equal("SECRET=mine\n# a comment\n", File.ReadAllText(file));
    }

    [Fact]
    public void Writing_the_same_values_again_leaves_the_file_untouched_so_dev_servers_dont_restart()
    {
        var file = Path.Combine(_dir, ".env.local");
        EnvFiles.WriteDotEnv(file, Values(("PORT", "5171")), seed: null);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, written);

        EnvFiles.WriteDotEnv(file, Values(("PORT", "5171")), seed: null);
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));

        EnvFiles.WriteDotEnv(file, Values(("PORT", "5172")), seed: null);
        Assert.NotEqual(written, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void A_new_dotenv_file_starts_from_the_main_checkouts_copy_and_is_deleted_when_sprig_made_it()
    {
        var seed = Path.Combine(_dir, "seed.env");
        File.WriteAllText(seed, "API_KEY=local-only\n");
        var file = Path.Combine(_dir, "wt", ".env");

        EnvFiles.WriteDotEnv(file, Values(("URL", "http://localhost:8000/?a=1#x")), seed);

        var text = File.ReadAllText(file);
        Assert.Contains("API_KEY=local-only", text);
        Assert.Contains("URL=\"http://localhost:8000/?a=1#x\"", text);

        File.WriteAllText(file, EnvFiles.StripBlocks(text).Replace("API_KEY=local-only", ""));
        EnvFiles.RemoveDotEnv(new WrittenFile(file, "dotenv", Created: true));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void A_launch_profile_goes_first_keeps_other_profiles_and_matches_its_url()
    {
        var file = Path.Combine(_dir, "Properties", "launchSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            {
              // a comment, as Visual Studio allows
              "profiles": { "http": { "commandName": "Project", "applicationUrl": "http://localhost:5100" } }
            }
            """);

        EnvFiles.WriteLaunchProfile(file, "sprig", Values(("ASPNETCORE_URLS", "http://localhost:8003"), ("Db", "Host=x")));

        var profiles = JsonNode.Parse(File.ReadAllText(file))!["profiles"]!.AsObject();
        Assert.Equal(["sprig", "http"], profiles.Select(p => p.Key));
        Assert.Equal("http://localhost:8003", (string?)profiles["sprig"]!["applicationUrl"]);
        Assert.Equal("Host=x", (string?)profiles["sprig"]!["environmentVariables"]!["Db"]);

        EnvFiles.RemoveLaunchProfile(new WrittenFile(file, "launchSettings", Created: false, Profile: "sprig"));
        Assert.Equal(["http"], JsonNode.Parse(File.ReadAllText(file))!["profiles"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void A_launch_profile_starts_from_the_env_of_the_profile_it_puts_second_so_dotnet_run_keeps_it()
    {
        var file = Path.Combine(_dir, "Properties", "launchSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """
            { "profiles": {
              "docker": { "commandName": "Docker" },
              "http": { "commandName": "Project", "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "Db": "mine" } }
            } }
            """);

        EnvFiles.WriteLaunchProfile(file, "sprig", Values(("Db", "Host=x")));
        // Again, as the next up does, with sprig's own profile now first.
        EnvFiles.WriteLaunchProfile(file, "sprig", Values(("Db", "Host=y")));

        var env = JsonNode.Parse(File.ReadAllText(file))!["profiles"]!["sprig"]!["environmentVariables"]!;
        Assert.Equal("Development", (string?)env["ASPNETCORE_ENVIRONMENT"]);
        Assert.Equal("Host=y", (string?)env["Db"]);
        Assert.Equal("mine", (string?)JsonNode.Parse(File.ReadAllText(file))!["profiles"]!["http"]!["environmentVariables"]!["Db"]);
    }

    [Fact]
    public void A_launch_settings_file_sprig_made_is_deleted_with_its_folder()
    {
        var file = Path.Combine(_dir, "app", "Properties", "launchSettings.json");

        EnvFiles.WriteLaunchProfile(file, "sprig", Values(("A", "1")));
        EnvFiles.RemoveLaunchProfile(new WrittenFile(file, "launchSettings", Created: true, Profile: "sprig"));

        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public void App_settings_nest_double_underscore_keys_and_remove_only_what_sprig_set()
    {
        var file = Path.Combine(_dir, "appsettings.Local.json");
        File.WriteAllText(file, """{ "Logging": { "LogLevel": { "Default": "Debug" } }, "ConnectionStrings": { "Other": "keep" } }""");

        var keys = EnvFiles.WriteAppSettings(file, Values(("ConnectionStrings__Db", "Host=x"), ("Smtp__Url", "smtp://localhost:8004")));

        var root = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("Host=x", (string?)root["ConnectionStrings"]!["Db"]);
        Assert.Equal("smtp://localhost:8004", (string?)root["Smtp"]!["Url"]);
        Assert.Equal(["ConnectionStrings.Db", "Smtp.Url"], keys);

        EnvFiles.RemoveAppSettings(new WrittenFile(file, "appSettings", Created: false, Keys: keys));
        var after = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal(["Logging", "ConnectionStrings"], after.Select(p => p.Key));
        Assert.Equal("keep", (string?)after["ConnectionStrings"]!["Other"]);
    }
}
