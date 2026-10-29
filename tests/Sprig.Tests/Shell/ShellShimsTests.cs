using System.Diagnostics;
using Sprig.Core.Shell;
using Sprig.Core.Store;

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

/// <summary>
/// The shims run for real: a lasting cmd (started with /k, as an interactive window is) and a PowerShell
/// session, each calling the shim and then reporting where it is. Hostile directives come from a fake "exe"
/// (cmd.exe running a batch file that copies a prepared line into the directives file), so the shim's own
/// checks are what's under test.
/// </summary>
public sealed class ShellShimsEndToEndTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "sprig-shims-" + Guid.NewGuid().ToString("N"));
    readonly string _shims;
    readonly string _fakeShims;
    readonly string _emit;

    public ShellShimsEndToEndTests()
    {
        _shims = Path.Combine(_root, "bin");
        _fakeShims = Path.Combine(_root, "fakebin");
        ShellShims.Write(_shims, Path.Combine(AppContext.BaseDirectory, "sprig.exe"));
        ShellShims.Write(_fakeShims, Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        _emit = Path.Combine(_root, "emit.cmd");
        File.WriteAllText(_emit, "@copy /y \"%~1\" \"%SPRIG_SHELL_DIRECTIVES%\" >nul\r\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    // A workspace whose worktree path has the characters that break careless batch files.
    string SeedWorkspace()
    {
        var store = Path.Combine(_root, "store");
        var worktree = Path.Combine(_root, "work tree (a&b) 50%", "api--w1");
        Directory.CreateDirectory(worktree);
        new InstanceStore(new SprigPaths(store)).Save(new InstanceRecord
        {
            Workspace = "w1",
            Repos = [new InstanceRepo { Name = "api", SourcePath = worktree, WorktreePath = worktree }],
        });
        return worktree;
    }

    string Line(string name, string content)
    {
        var path = Path.Combine(_root, $"line-{name}.txt");
        File.WriteAllText(path, content + "\n");
        return path;
    }

    // Run batch lines in a cmd that, like an interactive window, outlives each command; returns its stdout.
    string InLastingCmd(params string[] lines)
    {
        var script = Path.Combine(_root, $"run-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, "@echo off\r\n" + string.Join("\r\n", lines) + "\r\nexit\r\n");
        return Run("cmd.exe", ["/d", "/k", script]);
    }

    string Run(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = _root };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["SPRIG_STORE"] = Path.Combine(_root, "store");
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "shell did not exit");
        return output.Result;
    }

    [Fact]
    public void Cmd_shim_moves_a_lasting_window_and_passes_the_exit_code()
    {
        if (!OperatingSystem.IsWindows()) return;
        var worktree = SeedWorkspace();

        var output = InLastingCmd(
            $"call \"{_shims}\\sprig.cmd\" cd w1",
            "echo code=%ERRORLEVEL%",
            "echo now=\"%CD%\"",
            "echo leaked=[%SPRIG_SHELL_DIRECTIVES%][%sprig_target%]",
            $"call \"{_shims}\\sprig.cmd\" cd nope",
            "echo badcode=%ERRORLEVEL%");

        Assert.Contains("code=0", output);
        Assert.Contains($"now=\"{worktree}\"", output);
        Assert.Contains("leaked=[][]", output);
        Assert.Contains("badcode=1", output);
    }

    [Fact]
    public void Cmd_shim_offers_no_move_to_a_cmd_started_with_slash_c()
    {
        if (!OperatingSystem.IsWindows()) return;
        var probe = Path.Combine(_root, "probe.cmd");
        File.WriteAllText(probe, "@if defined SPRIG_SHELL_DIRECTIVES (echo offered) else (echo not-offered)\r\n");

        // How PowerShell runs a .cmd when there's no .ps1: a cmd that exits as soon as the command is done.
        Assert.Contains("not-offered", Run("cmd.exe", ["/d", "/c", Path.Combine(_fakeShims, "sprig.cmd"), "/d", "/c", probe]));
        Assert.Contains("offered", InLastingCmd($"call \"{_fakeShims}\\sprig.cmd\" /d /c \"{probe}\""));
    }

    [Theory]
    [InlineData("quotes", "cd C:\\Windows\"&echo PWNED&\"")]
    [InlineData("ampersand", "cd C:\\Windows&echo PWNED")]
    [InlineData("relative", "cd Windows")]
    [InlineData("missing", "cd C:\\does\\not\\exist")]
    [InlineData("unknown", "run calc")]
    public void Cmd_shim_refuses_a_hostile_directive(string name, string directive)
    {
        if (!OperatingSystem.IsWindows()) return;
        var output = InLastingCmd($"call \"{_fakeShims}\\sprig.cmd\" /d /c \"\"{_emit}\" \"{Line(name, directive)}\"\"", "echo now=\"%CD%\"");
        Assert.Contains($"now=\"{_root}\"", output);
        Assert.DoesNotContain("PWNED", output);
    }

    [Fact]
    public void Cmd_shim_applies_a_valid_directive()
    {
        if (!OperatingSystem.IsWindows()) return;
        var output = InLastingCmd($"call \"{_fakeShims}\\sprig.cmd\" /d /c \"\"{_emit}\" \"{Line("valid", "cd " + Environment.SystemDirectory)}\"\"", "echo now=\"%CD%\"");
        // cmd reports the folder's on-disk casing (System32), .NET its own (system32).
        Assert.Contains($"now=\"{Environment.SystemDirectory}\"", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShell_shim_moves_the_session_and_refuses_hostile_directives()
    {
        if (!OperatingSystem.IsWindows()) return;
        var worktree = SeedWorkspace();
        var quotes = Line("ps-quotes", "cd C:\\Windows\"&echo PWNED&\"");
        var missing = Line("ps-missing", "cd C:\\does\\not\\exist");

        // Single quotes keep PowerShell from expanding anything in the paths; '' is a literal quote inside them.
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var script = string.Join("; ",
            "if ((Get-ExecutionPolicy) -in 'Restricted', 'AllSigned') { 'skipped: scripts are blocked here'; exit }",
            $"& {Q(Path.Combine(_shims, "sprig.ps1"))} cd w1",
            "'code=' + $LASTEXITCODE",
            "'now=' + (Get-Location).Path",
            $"Set-Location -LiteralPath {Q(_root)}",
            $"& {Q(Path.Combine(_fakeShims, "sprig.ps1"))} /d /c {Q($"\"{_emit}\" \"{quotes}\"")}",
            $"& {Q(Path.Combine(_fakeShims, "sprig.ps1"))} /d /c {Q($"\"{_emit}\" \"{missing}\"")}",
            "'after-hostile=' + (Get-Location).Path",
            "'leaked=[' + $env:SPRIG_SHELL_DIRECTIVES + ']'");

        var output = Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script]);
        if (output.Contains("skipped:")) return;

        Assert.Contains("code=0", output);
        Assert.Contains($"now={worktree}", output);
        Assert.Contains($"after-hostile={_root}", output);
        Assert.Contains("leaked=[]", output);
        Assert.DoesNotContain("PWNED", output);
    }
}
