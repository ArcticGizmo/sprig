using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Core.Ports;
using Sprig.Core.Settings;

namespace Sprig.App.ViewModels;

/// <summary>
/// The Settings page (pinned to the bottom nav): configure the port-allocation range and restricted
/// ports, see which ports are currently leased, and check the status of any single port.
/// </summary>
public partial class SettingsViewModel : PageViewModel
{
    readonly AppServices _services;

    public override string Title => "Settings";

    // --- Editable fields (strings so partial/invalid input is handled gracefully on save) ---
    [ObservableProperty] private string _startText = "";
    [ObservableProperty] private string _endText = "";       // inclusive, for humans
    [ObservableProperty] private string _restrictedText = "";

    [ObservableProperty] private string _saveMessage = "";
    [ObservableProperty] private bool _saveFailed;

    // --- Changelog ---
    [ObservableProperty] private bool _showChangelogOnUpdate;
    bool _loadingSettings;

    // --- "Ports in use" list ---
    public ObservableCollection<PortUsageItem> Leases { get; } = new();
    [ObservableProperty] private bool _hasLeases;

    // --- Single-port checker ---
    [ObservableProperty] private string _checkText = "";
    [ObservableProperty] private string _checkResult = "";
    [ObservableProperty] private bool _hasCheckResult;
    [ObservableProperty] private bool _checkIsAvailable;
    [ObservableProperty] private bool _checkIsRestricted;
    [ObservableProperty] private bool _checkIsInUse;
    [ObservableProperty] private bool _checkIsOutOfRange;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        LoadFromStore();
    }

    protected override void OnActivated()
    {
        // Re-sync on entry: leases change as workspaces come and go, and settings may have changed.
        LoadFromStore();
        RunCheck();
    }

    void LoadFromStore()
    {
        var s = _services.Settings.Get();
        StartText = s.PortRangeStart.ToString(CultureInfo.InvariantCulture);
        EndText = (s.PortRangeEndExclusive - 1).ToString(CultureInfo.InvariantCulture);
        RestrictedText = string.Join(", ", s.RestrictedPorts);

        _loadingSettings = true;
        ShowChangelogOnUpdate = s.ShowChangelogOnUpdate;
        _loadingSettings = false;

        RefreshLeases();
    }

    // The changelog toggle persists immediately (it's independent of the port-policy Save button).
    partial void OnShowChangelogOnUpdateChanged(bool value)
    {
        if (_loadingSettings) return;
        var s = _services.Settings.Get();
        s.ShowChangelogOnUpdate = value;
        _services.Settings.Save(s);
    }

    void RefreshLeases()
    {
        Leases.Clear();
        foreach (var lease in _services.Ports.ListLeases())
            Leases.Add(new PortUsageItem(lease.Port, lease.Workspace, lease.Name));
        HasLeases = Leases.Count > 0;
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

        var settings = new SprigSettings
        {
            PortRangeStart = start,
            PortRangeEndExclusive = endInclusive + 1,
            RestrictedPorts = restricted,
        };

        try
        {
            _services.Settings.Save(settings);
        }
        catch (System.ArgumentException ex)
        {
            Fail(ex.Message);
            return;
        }

        // Re-read so the fields show the normalised (deduped/sorted) values.
        LoadFromStore();
        RunCheck();
        SaveFailed = false;
        SaveMessage = $"Saved. New workspaces will use ports {start}–{endInclusive}" +
            (restricted.Count > 0 ? $", skipping {restricted.Count} restricted." : ".");
        _services.NotifyStoreChanged();
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        StartText = SprigSettings.DefaultRangeStart.ToString(CultureInfo.InvariantCulture);
        EndText = (SprigSettings.DefaultRangeEndExclusive - 1).ToString(CultureInfo.InvariantCulture);
        RestrictedText = "";
        SaveMessage = "Defaults filled in — click Save to apply.";
        SaveFailed = false;
    }

    partial void OnCheckTextChanged(string value) => RunCheck();

    void RunCheck()
    {
        ClearCheck();
        if (string.IsNullOrWhiteSpace(CheckText))
            return;

        if (!TryParsePort(CheckText, out var port))
        {
            HasCheckResult = true;
            CheckResult = $"“{CheckText}” isn’t a valid port number.";
            CheckIsOutOfRange = true; // reuse the muted style for “not applicable”
            return;
        }

        var report = _services.Ports.Describe(port);
        HasCheckResult = true;
        switch (report.Status)
        {
            case PortStatus.Available:
                CheckIsAvailable = true;
                CheckResult = $"{port} — Available";
                break;
            case PortStatus.Restricted:
                CheckIsRestricted = true;
                CheckResult = $"{port} — Restricted (sprig will never allocate it)";
                break;
            case PortStatus.InUse:
                CheckIsInUse = true;
                CheckResult = $"{port} — In use by {report.HeldBy}";
                break;
            case PortStatus.OutOfRange:
                CheckIsOutOfRange = true;
                CheckResult = $"{port} — Outside sprig’s range";
                break;
        }
    }

    void ClearCheck()
    {
        HasCheckResult = false;
        CheckResult = "";
        CheckIsAvailable = CheckIsRestricted = CheckIsInUse = CheckIsOutOfRange = false;
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

/// <summary>A row in the "ports in use" list.</summary>
public sealed record PortUsageItem(int Port, string Workspace, string Name)
{
    public string PortText => Port.ToString(CultureInfo.InvariantCulture);
    public string HeldBy => $"{Workspace} / {Name}";
}
