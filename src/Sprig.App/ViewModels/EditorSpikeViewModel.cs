using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.App.Authoring;

namespace Sprig.App.ViewModels;

/// <summary>
/// The M1 editor spike: a sprig.cs in an in-app editor with completion and diagnostics from the out-of-process
/// language host, plus the measurements the spike has to report.
/// </summary>
public partial class EditorSpikeViewModel : PageViewModel
{
    public override string Title => "Editor";

    public LanguageHostClient Host { get; } = new();

    public ObservableCollection<HostDiagnostic> Diagnostics { get; } = [];

    [ObservableProperty] private string _hostStatus = "Not started";
    [ObservableProperty] private string _completionTiming = "–";
    [ObservableProperty] private string _diagnosticsTiming = "–";
    [ObservableProperty] private string _hostMemory = "–";
    [ObservableProperty] private string _appMemory = "–";
    [ObservableProperty] private string _editMessage = "";
    [ObservableProperty] private bool _hasDiagnostics;

    public EditorSpikeViewModel()
    {
        Host.Stopped += code => Dispatcher.UIThread.Post(() =>
            HostStatus = $"Stopped (exit code {code}). The next request starts a new one.");
    }

    public const string SampleText = """
        #:package Sprig.Sdk@0.1.0-exp
        using Sprig;

        var repo = Repo.Describe("shop-services");

        var pg = repo.Container("pg", "postgres:17")
            .Endpoint("tcp", targetPort: 5432);

        var smtp = repo.Import<Url>("mail-smtp");                // filled in by the system

        // The catalog owns stock levels.
        var catalog = repo.DotNet("catalog-api", "src/Catalog.Api")
            .Http("http")
            .Env("ConnectionStrings__Db", $"Host=localhost;Port={pg.Port};Database=catalog")
            .WaitFor(pg);

        var orders = repo.DotNet("orders-api", "src/Orders.Api")
            .Http("http")
            .Env("Smtp__Url", smtp)
            .WaitFor(pg, catalog);

        repo.Export<Url>("catalog-url", catalog.Url);

        return repo.Emit(args);
        """;

    public void RecordCompletion(double roundTripMs, double hostMs, int count)
    {
        CompletionTiming = $"{roundTripMs:0} ms round trip ({hostMs:0} ms in the host), {count} items";
        HostStatus = $"Running (pid {Host.ProcessId})";
    }

    public void RecordDiagnostics(HostResponse response, double roundTripMs)
    {
        DiagnosticsTiming = $"{roundTripMs:0} ms round trip ({response.Ms:0} ms in the host)";
        Diagnostics.Clear();
        foreach (var d in response.Diagnostics ?? []) Diagnostics.Add(d);
        HasDiagnostics = Diagnostics.Count > 0;
        HostStatus = $"Running (pid {Host.ProcessId})";
    }

    [RelayCommand]
    private async Task RefreshMemory()
    {
        using (var self = Process.GetCurrentProcess())
            AppMemory = $"{self.WorkingSet64 / 1048576.0:0} MB working set, {GC.GetTotalMemory(false) / 1048576.0:0} MB managed";
        try
        {
            var stats = await Host.StatsAsync();
            HostMemory = $"{stats.WorkingSetMb:0} MB working set, {stats.ManagedHeapMb:0} MB managed";
        }
        catch (Exception ex)
        {
            HostMemory = $"unavailable: {ex.Message}";
        }
    }

    [RelayCommand]
    private void KillHost()
    {
        Host.Kill();
        HostStatus = "Killed on purpose. Type to bring it back.";
    }

    protected override void OnActivated() => _ = RefreshMemory();
}
