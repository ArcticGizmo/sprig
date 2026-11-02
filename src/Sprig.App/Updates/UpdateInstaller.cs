using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sprig.App.Updates;

/// <summary>
/// Drives an update install and exposes its progress for the full-window overlay. Shared by the nav's
/// "Update now" entry and the About page so both show the same blocking screen, and so a second click
/// from either place can't start a parallel install.
/// </summary>
public partial class UpdateInstaller : ObservableObject
{
    /// <summary>True from the click until Velopack takes the process over (or the install fails).</summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private string _heading = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private int _percent;

    /// <summary>Indeterminate until the first progress tick, and again while Velopack installs.</summary>
    [ObservableProperty] private bool _isIndeterminate = true;

    /// <summary>
    /// Download, then install and restart. Returns false if the install failed (the overlay is dismissed so
    /// the caller can say so); on success it never returns because the process restarts.
    /// </summary>
    public async Task<bool> InstallAsync(UpdateCheckResult result)
    {
        if (IsActive || result is not { Availability: UpdateAvailability.Available })
            return false;

        Heading = $"Updating to v{result.AvailableVersion}";
        Detail = "Downloading…";
        Percent = 0;
        IsIndeterminate = true;
        IsActive = true;

        try
        {
            _downloading = true;
            await UpdateChecker.DownloadAsync(result, p => Dispatcher.UIThread.Post(() => OnProgress(p)));
            _downloading = false;

            Detail = "Installing — sprig will restart when it's done.";
            IsIndeterminate = true;
            // Velopack exits this process synchronously, so give the overlay a frame to show the new state.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(150);

            UpdateChecker.ApplyAndRestart(result);
            return true;
        }
        catch
        {
            _downloading = false;
            IsActive = false;
            return false;
        }
    }

    // Progress ticks are posted from Velopack's thread and can land after the download has finished; this
    // stops a late tick from overwriting the "Installing" text.
    bool _downloading;

    void OnProgress(int percent)
    {
        if (!_downloading) return;
        Percent = Math.Clamp(percent, 0, 100);
        IsIndeterminate = false;
        Detail = $"Downloading… {Percent}%";
    }
}
