using Sprig.Core.Shell;
using Sprig.Core.Store;

namespace Sprig.Tests.Shell;

/// <summary>The user PATH edits the installer and <c>sprig shell install</c> make, worked out without touching
/// the real PATH.</summary>
public class ShellInstallTests
{
    const string Bin = @"C:\Users\u\AppData\Local\Sprig\bin";
    const string Current = @"C:\Users\u\AppData\Local\Sprig\current";

    [Fact]
    public void A_fresh_install_appends_the_shims_then_the_exe()
        => Assert.Equal([@"C:\tools", Bin, Current], ShellInstall.Register([@"C:\tools"], Bin, Current));

    [Fact]
    public void An_existing_install_gains_the_shims_just_ahead_of_the_exe()
        => Assert.Equal([@"C:\a", Bin, Current, @"C:\b"], ShellInstall.Register([@"C:\a", Current, @"C:\b"], Bin, Current));

    [Fact]
    public void Entries_in_the_wrong_order_are_put_right_where_the_first_one_was()
        => Assert.Equal([@"C:\a", Bin, Current, @"C:\b"], ShellInstall.Register([@"C:\a", Current + @"\", @"C:\b", Bin.ToUpperInvariant()], Bin, Current));

    [Fact]
    public void Registering_again_changes_nothing()
    {
        string[] once = ShellInstall.Register([@"C:\a"], Bin, Current);
        Assert.Equal(once, ShellInstall.Register(once, Bin, Current));
    }

    [Fact]
    public void Without_shims_only_the_exe_stays()
        => Assert.Equal([@"C:\a", Current], ShellInstall.Register(ShellInstall.Unregister([@"C:\a", Bin, Current], Bin), Current));

    [Fact]
    public void Uninstall_removes_both_and_keeps_the_rest()
        => Assert.Equal([@"C:\a", @"C:\b"], ShellInstall.Unregister([@"C:\a", Bin, @"C:\b", Current], Bin, Current));

    [Fact]
    public void The_shims_sit_beside_current()
        => Assert.Equal(Bin, ShellInstall.ShimDirectory(Current + @"\"));

    [Fact]
    public void Only_a_Velopack_current_folder_counts_as_installed()
    {
        using var s = new TempStore();
        var current = Path.Combine(s.Root, "current");
        var build = Path.Combine(s.Root, "net10.0");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(build);
        Assert.False(ShellInstall.IsInstalled(current)); // no Update.exe beside it yet

        File.WriteAllText(Path.Combine(s.Root, "Update.exe"), "");
        Assert.True(ShellInstall.IsInstalled(current));
        Assert.False(ShellInstall.IsInstalled(build));
    }
}

// The process-wide policy variable is set by one test, so these run apart from the rest.
[Collection("cli-console")]
public class ScriptPolicyTests
{
    [Fact]
    public void Windows_PowerShell_is_always_among_the_shells_asked()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Contains(ScriptPolicy.InstalledShells(), s => s.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Asking_every_shell_fits_inside_the_install_hook_budget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var policies = ScriptPolicy.Ask();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(12), $"took {timer.Elapsed}");
        Assert.NotEmpty(policies);
    }

    [Fact]
    public void A_policy_inherited_from_the_calling_session_is_ignored()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previous = Environment.GetEnvironmentVariable(ScriptPolicy.ProcessScopeVariable);
        try
        {
            Environment.SetEnvironmentVariable(ScriptPolicy.ProcessScopeVariable, null);
            var plain = ScriptPolicy.Ask();
            Environment.SetEnvironmentVariable(ScriptPolicy.ProcessScopeVariable, "Bypass");
            Assert.Equal(plain, ScriptPolicy.Ask());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScriptPolicy.ProcessScopeVariable, previous);
        }
    }

    [Theory]
    [InlineData("RemoteSigned", true)]
    [InlineData("Unrestricted", true)]
    [InlineData("Bypass", true)]
    [InlineData("Restricted", false)]
    [InlineData("AllSigned", false)]
    [InlineData("", false)]
    public void Only_policies_that_run_an_unsigned_local_script_count(string policy, bool runs)
        => Assert.Equal(runs, new ScriptPolicy.ShellPolicy("powershell.exe", policy).RunsLocalScripts);

    [Fact]
    public void Every_shell_must_allow_it()
    {
        Assert.False(ScriptPolicy.AllowLocalScripts([]));
        Assert.False(ScriptPolicy.AllowLocalScripts([new("powershell.exe", "Restricted"), new("pwsh.exe", "RemoteSigned")]));
        Assert.True(ScriptPolicy.AllowLocalScripts([new("powershell.exe", "RemoteSigned"), new("pwsh.exe", "RemoteSigned")]));
    }
}
