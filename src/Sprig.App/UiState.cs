using System.Collections.Generic;
using System.IO;
using Sprig.Engine.Store;

namespace Sprig.App;

/// <summary>How the app was left: what's folded away and what's pinned. Not settings, so it isn't on the Settings page.</summary>
/// <param name="CollapsedTrellises">Trellis groups folded shut in the nav.</param>
/// <param name="OpenDownTrellises">Trellis groups whose sprouts that are down are showing.</param>
/// <param name="Pins">Pinned apps (<c>repo/app</c>) per trellis, so a pin applies to every sprout of it.</param>
/// <param name="DetailsClosed">The app details beside the log were folded away.</param>
/// <param name="LogTimesHidden">The log's time column was turned off.</param>
/// <param name="LogWrapOff">The log's long lines run on past the edge rather than wrapping.</param>
public sealed record UiStateData(
    List<string>? CollapsedTrellises = null,
    List<string>? OpenDownTrellises = null,
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

    public bool IsCollapsed(string trellis) => Data.CollapsedTrellises?.Contains(trellis) == true;

    public void SetCollapsed(string trellis, bool collapsed) => Save(Data with { CollapsedTrellises = Toggle(Data.CollapsedTrellises, trellis, collapsed) });

    public bool IsDownOpen(string trellis) => Data.OpenDownTrellises?.Contains(trellis) == true;

    public void SetDownOpen(string trellis, bool open) => Save(Data with { OpenDownTrellises = Toggle(Data.OpenDownTrellises, trellis, open) });

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

    static List<string> Toggle(List<string>? list, string item, bool present)
    {
        var next = new List<string>(list ?? []);
        next.Remove(item);
        if (present) next.Add(item);
        return next;
    }

    void Save(UiStateData data)
    {
        _data = data;
        Directory.CreateDirectory(paths.Root);
        JsonFile.Write(File, data);
    }
}
