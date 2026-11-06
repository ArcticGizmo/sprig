using System.Collections.Generic;
using System.IO;
using Sprig.Engine.Store;

namespace Sprig.App;

/// <summary>How the app was left: what's folded away and what's pinned. Not settings, so it isn't on the Settings page.</summary>
/// <param name="Pins">Pinned apps (<c>repo/app</c>) per orchestration, so a pin applies to every workspace of it.</param>
/// <param name="DetailsClosed">The app details beside the log were folded away.</param>
/// <param name="LogTimesHidden">The log's time column was turned off.</param>
/// <param name="LogWrapOff">The log's long lines run on past the edge rather than wrapping.</param>
/// <param name="WorkspaceChoices">The new-workspace form's last answers per orchestration, so the next workspace of it starts there.</param>
public sealed record UiStateData(
    Dictionary<string, List<string>>? Pins = null,
    bool DetailsClosed = false,
    bool LogTimesHidden = false,
    bool LogWrapOff = false,
    Dictionary<string, Sprig.Engine.Workspaces.WorkspaceOptions>? WorkspaceChoices = null);

/// <summary>Kept in the store beside the settings, read once and written on each change.</summary>
public sealed class UiState(ISprigPaths paths)
{
    UiStateData? _data;

    string File => Path.Combine(paths.Root, "ui.json");

    UiStateData Data => _data ??= JsonFile.Read<UiStateData>(File) ?? new UiStateData();

    public bool DetailsOpen
    {
        get => !Data.DetailsClosed;
        set => Save(Data with { DetailsClosed = !value });
    }

    public bool LogTimes
    {
        get => !Data.LogTimesHidden;
        set => Save(Data with { LogTimesHidden = !value });
    }

    public bool LogWrap
    {
        get => !Data.LogWrapOff;
        set => Save(Data with { LogWrapOff = !value });
    }

    public IReadOnlyList<string> Pins(string orchestration) => Data.Pins?.GetValueOrDefault(orchestration) ?? [];

    public void SetPins(string orchestration, IEnumerable<string> apps)
    {
        var pins = new Dictionary<string, List<string>>(Data.Pins ?? []) { [orchestration] = [.. apps] };
        if (pins[orchestration].Count == 0) pins.Remove(orchestration);
        Save(Data with { Pins = pins });
    }

    /// <summary>The last answers for <paramref name="orchestration"/>; the first time, running in place, as the CLI's
    /// <c>sprig up</c> does.</summary>
    public Sprig.Engine.Workspaces.WorkspaceOptions WorkspaceChoices(string orchestration)
        => Data.WorkspaceChoices?.GetValueOrDefault(orchestration) ?? new Sprig.Engine.Workspaces.WorkspaceOptions(InPlace: true);

    public void SetWorkspaceChoices(string orchestration, Sprig.Engine.Workspaces.WorkspaceOptions options)
        => Save(Data with { WorkspaceChoices = new(Data.WorkspaceChoices ?? []) { [orchestration] = options } });

    void Save(UiStateData data)
    {
        _data = data;
        Directory.CreateDirectory(paths.Root);
        JsonFile.Write(File, data);
    }
}
