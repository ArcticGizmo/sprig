using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Updates;

namespace Sprig.App.ViewModels;

/// <summary>
/// The "About" page (pinned to the bottom of the nav): app version, links to the repo and its issue
/// tracker, and a manual check-for-updates / install flow over the same feed the launch banner uses.
/// </summary>
public partial class AboutViewModel : PageViewModel
{
    public const string RepoUrl = "https://github.com/ArcticGizmo/sprig";
    public const string IssuesUrl = RepoUrl + "/issues";

    public override string Title => "About";

    /// <summary>The running version, e.g. "0.1.0" (from the assembly's informational version).</summary>
    public string Version => Current;

    /// <summary>The running version — shared so the launch-time changelog check reads the same value.</summary>
    public static string Current { get; } = ResolveVersion();

    // Bindable copies so the URLs can be shown as text as well as opened.
    public string RepoUrlText => RepoUrl;
    public string IssuesUrlText => IssuesUrl;

    /// <summary>Result of the last check — held so <see cref="ApplyUpdateCommand"/> can install it.</summary>
    UpdateCheckResult? _lastCheck;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _updateStatus = "";

    /// <summary>True once a check found an installable update — reveals the install button.</summary>
    [ObservableProperty] private bool _updateAvailable;

    [RelayCommand] private void OpenRepo() => OpenUrl(RepoUrl);
    [RelayCommand] private void OpenIssues() => OpenUrl(IssuesUrl);

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task CheckForUpdates()
    {
        IsBusy = true;
        UpdateAvailable = false;
        UpdateStatus = "Checking for updates…";
        try
        {
            _lastCheck = await UpdateChecker.CheckDetailedAsync();
            UpdateStatus = _lastCheck.Availability switch
            {
                UpdateAvailability.Available =>
                    $"Version {_lastCheck.AvailableVersion} is available (you have {_lastCheck.CurrentVersion}).",
                UpdateAvailability.UpToDate => "You're on the latest version.",
                UpdateAvailability.NotApplicable =>
                    "Updates aren't available in this build (no release feed configured).",
                _ => "Couldn't check for updates — try again later.",
            };
            UpdateAvailable = _lastCheck.Availability == UpdateAvailability.Available;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ApplyUpdate()
    {
        if (_lastCheck is not { Availability: UpdateAvailability.Available })
            return;

        IsBusy = true;
        UpdateStatus = $"Downloading version {_lastCheck.AvailableVersion}…";
        try
        {
            // On success the app downloads, installs, and restarts — this does not return.
            await UpdateChecker.ApplyAsync(_lastCheck);
        }
        catch
        {
            UpdateStatus = "Update failed to install — try again later.";
            IsBusy = false;
        }
    }

    bool NotBusy => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }

    static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Opening a browser is best-effort; never crash the app over it.
        }
    }

    static string ResolveVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
            return info.Split('+')[0]; // strip any "+<sourcelink-sha>" build metadata

        var v = asm.GetName().Version;
        return v is null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
