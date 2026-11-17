using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Definitions;
using Sprig.Engine.Shell;
using Sprig.Engine.Workspaces;

namespace Sprig.App.ViewModels;

/// <summary>The selected workspace's root, opened where the user works on it: VS Code, a terminal, or the file
/// browser. The root is what <c>sprig cd</c> calls root (see <see cref="WorkspacePlaces.Root"/>).</summary>
public partial class WorkspacesViewModel
{
    /// <summary>Null when the workspace has no folder yet, or it has gone.</summary>
    string? RootFolder()
    {
        if (Selected?.Record is not { } record) return null;
        var fallback = new StoredDefinitions(_services.Paths).Repo(record.Orchestration)?.Folder
                       ?? (File.Exists(record.OrchestrationPath) ? Path.GetDirectoryName(Path.GetFullPath(record.OrchestrationPath)) : null);
        return WorkspacePlaces.Root(record, fallback) is { } root && Directory.Exists(root) ? root : null;
    }

    /// <summary>Opens a workspace in a window of its own, by its key; wired by the window.</summary>
    public Action<string>? OpenInNewWindow { get; set; }

    [RelayCommand]
    private void OpenSelectedInNewWindow()
    {
        if (Selected?.Record.Key is { } key) OpenInNewWindow?.Invoke(key);
    }

    [RelayCommand]
    private void OpenRootInCode() => OpenRoot("VS Code", root =>
    {
        // `code` is a .cmd on Windows, which needs a shell to run; without it on PATH, VS Code's own link opens it.
        if (TerminalWindow.Which(OperatingSystem.IsWindows() ? "code.cmd" : "code") is { } code)
            Process.Start(new ProcessStartInfo(code) { ArgumentList = { root }, UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        else
            Process.Start(new ProcessStartInfo($"vscode://file/{root.Replace('\\', '/')}") { UseShellExecute = true })?.Dispose();
    });

    [RelayCommand]
    private void OpenRootInTerminal() => OpenRoot("a terminal", root =>
        TerminalWindow.Open(root, title: Selected is { } s ? $"{s.Title} · {s.Branch}" : null));

    [RelayCommand]
    private void OpenRootFolder() => OpenRoot("the folder", root =>
        Process.Start(new ProcessStartInfo(root) { UseShellExecute = true })?.Dispose());

    void OpenRoot(string what, Action<string> open)
    {
        if (RootFolder() is not { } root)
        {
            SayApps("It has no folder yet: start it, or make it ready, first.", bad: true);
            return;
        }
        try
        {
            open(root);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            SayApps($"Couldn't open {what}: {e.Message}", bad: true);
        }
    }
}
