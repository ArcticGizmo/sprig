using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Authoring;

namespace Sprig.App.ViewModels;

/// <summary>A line of the change, as a diff shows it: <c>+</c> added, <c>-</c> taken out, or a line around them.</summary>
public sealed record DiffLine(string Mark, string Text)
{
    public bool IsAdded => Mark == "+";
    public bool IsRemoved => Mark == "-";
}

/// <summary>One way to fill the need, offered in the dialog.</summary>
public sealed partial class FixOption(NeedFix fix) : ObservableObject
{
    public NeedFix Fix { get; } = fix;
    public string Label => Fix.Label;
    public string Why => Fix.Placed ? Fix.Why : $"{Fix.Why} ({Fix.Error})";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// The dialog behind the checklist's "What the repos need": one unfilled need at a time, the ways to fill it, and the
/// change each makes to the trellis's code, shown as a diff before anything is written.
/// </summary>
public sealed partial class FillNeedsViewModel : ViewModelBase
{
    string _file = "";
    string _text = "";

    /// <summary>Writes the chosen change and plans again.</summary>
    public Func<string, string, Task>? Apply { get; init; }

    /// <summary>Opens the trellis's code instead, for a change sprig can't place.</summary>
    public Action? OpenCode { get; init; }

    public ObservableCollection<FixOption> Options { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _more = "";
    [ObservableProperty] private IReadOnlyList<DiffLine> _diff = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply), nameof(Unplaced))]
    private FixOption? _selected;
    [ObservableProperty] private string _message = "";

    public bool CanApply => Selected?.Fix.Placed == true;
    public bool Unplaced => Selected is { Fix.Placed: false };

    /// <summary>Show the first of <paramref name="needs"/>; the rest are counted, and come next once it's filled.</summary>
    public void Open(string file, IReadOnlyList<(string Repo, UnfilledNeed Need)> needs)
    {
        if (needs.Count == 0) return;
        _file = file;
        try { _text = File.ReadAllText(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"The trellis couldn't be read: {e.Message}";
            return;
        }

        var (repo, need) = needs[0];
        Title = $"Fill {need.Import}";
        Subtitle = need.Offers.Count > 1
            ? $"{repo} needs {need.Import}, and more than one repo offers it. Pick which, or set it another way."
            : $"{repo} needs {need.Import} (a {need.Type}), and nothing in the trellis offers it.";
        More = needs.Count > 1 ? $"{needs.Count - 1} more after this one." : "";
        Message = "";
        Options.Clear();
        foreach (var fix in NeedFixes.For(_text, need)) Options.Add(new FixOption(fix));
        Pick(Options.FirstOrDefault(o => o.Fix.Placed) ?? Options.FirstOrDefault());
        IsOpen = true;
    }

    [RelayCommand]
    private void Pick(FixOption? option)
    {
        foreach (var o in Options) o.IsSelected = o == option;
        Selected = option;
        Diff = option is null ? [] : option.Fix.Placed ? Changes(_text, option.Fix.Text) : option.Fix.Lines.Select(l => new DiffLine("+", l)).ToArray();
    }

    [RelayCommand]
    private async Task AddToCode()
    {
        if (Selected?.Fix is not { Placed: true } fix || Apply is null) return;
        try
        {
            await Apply(_file, fix.Text);
            IsOpen = false;
            if (fix.FillIn) OpenCode?.Invoke();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"It couldn't be saved: {e.Message}";
        }
    }

    [RelayCommand]
    private void ShowCode()
    {
        IsOpen = false;
        OpenCode?.Invoke();
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>What changed between two versions of a file, with two lines either side. The fixes only insert, or name a
    /// repo line, so the lines that differ sit between what's the same at the start and at the end.</summary>
    public static IReadOnlyList<DiffLine> Changes(string before, string after)
    {
        var a = before.ReplaceLineEndings("\n").Split('\n');
        var b = after.ReplaceLineEndings("\n").Split('\n');
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        var end = 0;
        while (end < a.Length - start && end < b.Length - start && a[^(end + 1)] == b[^(end + 1)]) end++;

        const int context = 2;
        var lines = new List<DiffLine>();
        for (var i = Math.Max(0, start - context); i < start; i++) lines.Add(new DiffLine(" ", a[i]));
        for (var i = start; i < a.Length - end; i++) lines.Add(new DiffLine("-", a[i]));
        for (var i = start; i < b.Length - end; i++) lines.Add(new DiffLine("+", b[i]));
        for (var i = b.Length - end; i < Math.Min(b.Length, b.Length - end + context); i++) lines.Add(new DiffLine(" ", b[i]));
        return lines;
    }
}
