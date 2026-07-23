using Sprig.Core.Store;

namespace Sprig.Tests.Store;

public class InstanceStoreTests
{
    static InstanceRecord Sample(string ws) => new()
    {
        Workspace = ws,
        Stack = "example-stack",
        Repos =
        [
            new InstanceRepo
            {
                Name = "dotnet-api",
                SourcePath = @"C:\repos\dotnet-api",
                WorktreePath = $@"C:\repos\dotnet-api--{ws}",
                Branch = $"sprig/{ws}",
                GeneratedComposePaths = [$@"C:\store\instances\{ws}\docker-compose.sprig.yml"],
            }
        ],
        Ports = new Dictionary<string, int> { ["api"] = 20001, ["postgres"] = 20002 },
        LastStatus = "running",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Save_then_load_round_trips()
    {
        using var s = new TempStore();
        var store = new InstanceStore(s.Paths);
        var record = Sample("feature-x");

        store.Save(record);
        var loaded = store.TryLoad("feature-x");

        // Record equality compares collections by reference, so assert field-by-field.
        Assert.NotNull(loaded);
        Assert.Equal(record.Workspace, loaded!.Workspace);
        Assert.Equal(record.Stack, loaded.Stack);
        Assert.Equal(record.LastStatus, loaded.LastStatus);
        Assert.Equal(record.CreatedAt, loaded.CreatedAt);
        Assert.Equal(record.Ports, loaded.Ports);
        Assert.Single(loaded.Repos);
        var (a, b) = (record.Repos[0], loaded.Repos[0]);
        Assert.Equal(a.Name, b.Name);
        Assert.Equal(a.SourcePath, b.SourcePath);
        Assert.Equal(a.WorktreePath, b.WorktreePath);
        Assert.Equal(a.Branch, b.Branch);
        Assert.Equal(a.GeneratedComposePaths, b.GeneratedComposePaths);
        Assert.Equal(a.ComposePaths, b.ComposePaths);
    }

    [Fact]
    public void Legacy_single_compose_path_is_read_as_a_list()
    {
        // Records written before multi-compose stored a single "GeneratedComposePath".
        using var s = new TempStore();
        var store = new InstanceStore(s.Paths);
        var dir = s.Paths.InstanceDir("legacy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(s.Paths.InstanceRecordFile("legacy"), """
            {
              "Workspace": "legacy",
              "Repos": [ { "Name": "api", "SourcePath": "C:\\r", "WorktreePath": "C:\\r--legacy",
                           "GeneratedComposePath": "C:\\store\\legacy\\docker-compose.sprig.yml" } ]
            }
            """);

        var loaded = store.TryLoad("legacy");

        Assert.NotNull(loaded);
        Assert.Equal(["C:\\store\\legacy\\docker-compose.sprig.yml"], loaded!.Repos[0].ComposePaths);
    }

    [Fact]
    public void TryLoad_missing_returns_null()
    {
        using var s = new TempStore();
        Assert.Null(new InstanceStore(s.Paths).TryLoad("nope"));
    }

    [Fact]
    public void LoadAll_returns_every_saved_instance()
    {
        using var s = new TempStore();
        var store = new InstanceStore(s.Paths);
        store.Save(Sample("a"));
        store.Save(Sample("b"));

        var all = store.LoadAll();

        Assert.Equal(["a", "b"], all.Select(r => r.Workspace).Order());
    }

    [Fact]
    public void LoadAll_on_empty_store_is_empty()
    {
        using var s = new TempStore();
        Assert.Empty(new InstanceStore(s.Paths).LoadAll());
    }

    [Fact]
    public void Delete_removes_the_instance()
    {
        using var s = new TempStore();
        var store = new InstanceStore(s.Paths);
        store.Save(Sample("gone"));

        store.Delete("gone");

        Assert.Null(store.TryLoad("gone"));
        store.Delete("gone"); // idempotent — no throw
    }

    [Fact]
    public void Save_is_atomic_no_temp_files_left()
    {
        using var s = new TempStore();
        var store = new InstanceStore(s.Paths);
        store.Save(Sample("x"));

        var dir = s.Paths.InstanceDir("x");
        Assert.DoesNotContain(Directory.GetFiles(dir), f => Path.GetFileName(f).Contains(".tmp-"));
    }
}
