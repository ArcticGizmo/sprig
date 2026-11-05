using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Settings;

namespace Sprig.App.ViewModels;

/// <summary>
/// The Settings page (pinned to the bottom of the nav): the port-allocation range, restricted ports and the
/// changelog toggle.
/// </summary>
public partial class SettingsViewModel : PageViewModel
{
    readonly AppServices _services;

    public override string Title => "Settings";

    // Strings, so partial or invalid input is reported on save instead of rejected while typing.
    [ObservableProperty] private string _startText = "";
    [ObservableProperty] private string _endText = "";       // inclusive, for humans
    [ObservableProperty] private string _restrictedText = "";

    [ObservableProperty] private string _saveMessage = "";
    [ObservableProperty] private bool _saveFailed;

    [ObservableProperty] private bool _showChangelogOnUpdate;
    bool _loadingSettings;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        LoadFromStore();
    }

    protected override void OnActivated() => LoadFromStore();

    void LoadFromStore()
    {
        var s = _services.Settings.Get();
        StartText = s.PortRangeStart.ToString(CultureInfo.InvariantCulture);
        EndText = (s.PortRangeEndExclusive - 1).ToString(CultureInfo.InvariantCulture);
        RestrictedText = string.Join(", ", s.RestrictedPorts);

        _loadingSettings = true;
        ShowChangelogOnUpdate = s.ShowChangelogOnUpdate;
        _loadingSettings = false;
    }

    // Persists immediately; it's independent of the port-policy Save button.
    partial void OnShowChangelogOnUpdateChanged(bool value)
    {
        if (_loadingSettings) return;
        var s = _services.Settings.Get();
        s.ShowChangelogOnUpdate = value;
        _services.Settings.Save(s);
    }

    [RelayCommand]
    private void Save()
    {
        if (!TryParsePort(StartText, out var start))
        {
            Fail($"“{StartText}” isn’t a valid start port.");
            return;
        }
        if (!TryParsePort(EndText, out var endInclusive))
        {
            Fail($"“{EndText}” isn’t a valid end port.");
            return;
        }
        if (!TryParseRestricted(RestrictedText, out var restricted, out var badToken))
        {
            Fail($"“{badToken}” in restricted ports isn’t a valid port number.");
            return;
        }

        // Start from what's stored so settings this form doesn't edit are kept.
        var settings = _services.Settings.Get();
        settings.PortRangeStart = start;
        settings.PortRangeEndExclusive = endInclusive + 1;
        settings.RestrictedPorts = restricted;

        try
        {
            _services.Settings.Save(settings);
        }
        catch (System.ArgumentException ex)
        {
            Fail(ex.Message);
            return;
        }

        // Re-read so the fields show the normalised (deduped, sorted) values.
        LoadFromStore();
        SaveFailed = false;
        SaveMessage = $"Saved. New sprouts will use ports {start}–{endInclusive}" +
            (restricted.Count > 0 ? $", skipping {restricted.Count} restricted." : ".");
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        var defaults = _services.Settings.Defaults();
        StartText = defaults.PortRangeStart.ToString(CultureInfo.InvariantCulture);
        EndText = (defaults.PortRangeEndExclusive - 1).ToString(CultureInfo.InvariantCulture);
        RestrictedText = "";
        SaveMessage = "Defaults filled in. Click Save to apply.";
        SaveFailed = false;
    }

    void Fail(string message)
    {
        SaveFailed = true;
        SaveMessage = message;
    }

    static bool TryParsePort(string text, out int port)
    {
        port = 0;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
            return false;
        if (p < SprigSettings.MinPort || p > SprigSettings.MaxPort)
            return false;
        port = p;
        return true;
    }

    static bool TryParseRestricted(string text, out List<int> ports, out string badToken)
    {
        ports = new List<int>();
        badToken = "";
        var tokens = text.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
            System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        foreach (var token in tokens)
        {
            if (!TryParsePort(token, out var p))
            {
                badToken = token;
                return false;
            }
            ports.Add(p);
        }
        ports = ports.Distinct().OrderBy(p => p).ToList();
        return true;
    }
}
