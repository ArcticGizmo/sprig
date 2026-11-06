using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sprig.App.ViewModels;

/// <summary>
/// One definition open in the editor. Saving approves what was saved (the user is its author), then asks the page that
/// opened it what that save means, such as whether the orchestration now plans, and says so on the status line.
/// </summary>
public partial class DefinitionEditorViewModel(AppServices services) : ViewModelBase
{
    /// <summary>What the page says once a save is written: whether it still runs, plans, and so on.</summary>
    public Func<Task<(string Message, bool Bad)>>? AfterSave { get; set; }

    /// <summary>Raised when the editor closes, so the page can show what it hid.</summary>
    public event Action? Closed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _text = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _savedText = "";

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _statusBad;
    [ObservableProperty] private bool _confirmingDiscard;

    public bool IsDirty => IsEditing && Text != SavedText;

    public Sprig.App.Authoring.LanguageHostClient Language => services.Language;

    /// <returns>Why it couldn't be opened, or null.</returns>
    public string? Open(string file, string title, string note)
    {
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
        Path = file;
        Title = title;
        Note = note;
        SavedText = text;
        Text = text;
        Say("", bad: false);
        ConfirmingDiscard = false;
        IsEditing = true;
        return null;
    }

    [RelayCommand]
    private async Task Save()
    {
        if (!IsEditing) return;
        var text = Text;
        var file = Path;
        try { await AppServices.RunAsync(() => services.Authoring.Save(file, text)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't save: {e.Message}", bad: true);
            return;
        }
        SavedText = text;
        if (AfterSave is not { } after)
        {
            Say("Saved.", bad: false);
            _ = ClearSoon();
            return;
        }
        Say("Saved. Checking…", bad: false);
        var (message, bad) = await after();
        Say(message, bad);
        if (!bad) _ = ClearSoon();
    }

    [RelayCommand]
    private void Close()
    {
        if (IsDirty && !ConfirmingDiscard)
        {
            ConfirmingDiscard = true;
            return;
        }
        ConfirmingDiscard = false;
        IsEditing = false;
        Text = "";
        SavedText = "";
        Closed?.Invoke();
    }

    [RelayCommand]
    private void KeepEditing() => ConfirmingDiscard = false;

    int _statusVersion;

    void Say(string message, bool bad)
    {
        _statusVersion++;
        Status = message;
        StatusBad = bad;
    }

    /// <summary>Good news only needs a glance; a problem stays until the next save says otherwise.</summary>
    async Task ClearSoon()
    {
        var version = _statusVersion;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (version == _statusVersion && !StatusBad) Say("", bad: false);
    }
}
