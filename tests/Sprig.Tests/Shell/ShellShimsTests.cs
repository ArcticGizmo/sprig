using Sprig.Engine.Shell;

namespace Sprig.Tests.Shell;

/// <summary>What the generated shims contain: the exe path written so each shell reads it correctly.</summary>
public class ShellShimsContentTests
{
    [Fact]
    public void Cmd_path_under_LOCALAPPDATA_is_written_through_the_variable()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA")!;
        Assert.Equal(@"%LOCALAPPDATA%\Sprig\current\sprig.exe", ShellShims.CmdPath(Path.Combine(local, "Sprig", "current", "sprig.exe")));
    }

    [Fact]
    public void Cmd_path_doubles_a_literal_percent()
        => Assert.Equal(@"D:\50%%off\sprig.exe", ShellShims.CmdPath(@"D:\50%off\sprig.exe"));

    [Fact]
    public void Bash_path_is_posix_and_quote_safe()
    {
        Assert.Equal("/c/Program Files/sprig.exe", ShellShims.PosixPath(@"C:\Program Files\sprig.exe"));
        Assert.Contains(@"exec '/d/it'\''s/sprig.exe' ""$@""", ShellShims.Bash(@"D:\it's\sprig.exe"));
    }

    [Fact]
    public void Line_endings_suit_each_shell()
    {
        Assert.DoesNotContain("\r", ShellShims.Bash(@"C:\s\sprig.exe"));
        // cmd mis-reads labels in a batch file with bare LF endings.
        Assert.DoesNotContain("\n", ShellShims.Cmd(@"C:\s\sprig.exe").Replace("\r\n", ""));
        Assert.DoesNotContain("\n", ShellShims.PowerShell(@"C:\s\sprig.exe").Replace("\r\n", ""));
    }

    [Fact]
    public void Write_produces_all_three()
    {
        using var s = new TempStore();
        var files = ShellShims.Write(Path.Combine(s.Root, "bin"), @"C:\s\sprig.exe");
        Assert.Equal(["sprig.ps1", "sprig.cmd", "sprig"], files.Select(Path.GetFileName));
        Assert.All(files, f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public void Write_without_PowerShell_leaves_out_the_script_and_removes_an_old_one()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "bin");
        ShellShims.Write(dir, @"C:\s\sprig.exe");

        var files = ShellShims.Write(dir, @"C:\s\sprig.exe", powerShell: false);

        Assert.Equal(["sprig.cmd", "sprig"], files.Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(dir, "sprig.ps1")));
    }

    [Fact]
    public void Write_leaves_an_unchanged_shim_alone_and_replaces_a_changed_one()
    {
        using var s = new TempStore();
        var dir = Path.Combine(s.Root, "bin");
        var cmd = Path.Combine(dir, "sprig.cmd");
        ShellShims.Write(dir, @"C:\s\sprig.exe");
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(cmd, old);

        ShellShims.Write(dir, @"C:\s\sprig.exe");
        Assert.Equal(old, File.GetLastWriteTimeUtc(cmd));

        ShellShims.Write(dir, @"C:\t\sprig.exe");
        Assert.Contains(@"C:\t\sprig.exe", File.ReadAllText(cmd));
    }
}

/// <summary>The exe's half of the contract. The variable is process-wide, so this runs apart from the rest.</summary>
[Collection("cli-console")]
public class ShellShimsRequestTests
{
    [Fact]
    public void A_cd_is_requested_only_when_a_shim_set_up_a_directives_file()
    {
        using var s = new TempStore();
        var previous = Environment.GetEnvironmentVariable(ShellShims.DirectivesVariable);
        try
        {
            Environment.SetEnvironmentVariable(ShellShims.DirectivesVariable, null);
            Assert.False(ShellShims.TryRequestCd(s.Root));

            Directory.CreateDirectory(s.Root);
            var file = Path.Combine(s.Root, "directives.txt");
            Environment.SetEnvironmentVariable(ShellShims.DirectivesVariable, file);
            Assert.False(ShellShims.TryRequestCd(s.Root)); // the shim creates the file; no file, no shim

            File.WriteAllText(file, "");
            Assert.True(ShellShims.TryRequestCd(s.Root));
            Assert.Equal($"cd {s.Root}\n", File.ReadAllText(file));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ShellShims.DirectivesVariable, previous);
        }
    }
}
