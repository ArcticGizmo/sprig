using System.Collections.Generic;
using System.IO;
using Sprig.Engine.Store;

namespace Sprig.App;

/// <summary>How the app was left: what's folded away and what's pinned. Not settings, so it isn't on the Settings page.</summary>
/// <param name="Pins">Pinned apps (<c>repo/app</c>) per trellis, so a pin applies to every sprout of it.</param>
/// <param name="DetailsClosed">The app details beside the log were folded away.</param>
/// <param name="LogTimesHidden">The log's time column was turned off.</param>
/// <param name="LogWrapOff">The log's long lines run on past the edge rather than wrapping.</param>
public sealed record UiStateData(
    Dictionary<string, List<string>>? Pins = null,
    bool DetailsClosed = false,
    bool LogTimesHidden = false,
    bool LogWrapOff = false);

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

    public IReadOnlyList<string> Pins(string trellis) => Data.Pins?.GetValueOrDefault(trellis) ?? [];

    public void SetPins(string trellis, IEnumerable<string> apps)
    {
        var pins = new Dictionary<string, List<string>>(Data.Pins ?? []) { [trellis] = [.. apps] };
        if (pins[trellis].Count == 0) pins.Remove(trellis);
        Save(Data with { Pins = pins });
    }

    void Save(UiStateData data)
    {
        _data = data;
        Directory.CreateDirectory(paths.Root);
        JsonFile.Write(File, data);
    }
}
