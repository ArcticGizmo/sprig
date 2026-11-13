using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Sprig.App.Controls;

/// <summary>
/// The one control for any path field: a folder button on the right opens the OS picker, typing offers
/// terminal-style completions (Tab or Enter takes the highlighted one, or the first), and paths always use forward
/// slashes.
/// </summary>
public partial class PathBox : UserControl
{
    public static readonly StyledProperty<string?> PathProperty =
        AvaloniaProperty.Register<PathBox, string?>(nameof(Path), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<PathBox, string?>(nameof(Placeholder));

    /// <summary>A file-name suffix to offer and pick, e.g. <c>.orchestration.yaml</c>. Null offers every file.</summary>
    public static readonly StyledProperty<string?> FileSuffixProperty =
        AvaloniaProperty.Register<PathBox, string?>(nameof(FileSuffix));

    /// <summary>Pick a folder instead of a file.</summary>
    public static readonly StyledProperty<bool> PickFolderProperty =
        AvaloniaProperty.Register<PathBox, bool>(nameof(PickFolder));

    public static readonly StyledProperty<string> PickerTitleProperty =
        AvaloniaProperty.Register<PathBox, string>(nameof(PickerTitle), "Choose");

    public string? Path { get => GetValue(PathProperty); set => SetValue(PathProperty, value); }
    public string? Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public string? FileSuffix { get => GetValue(FileSuffixProperty); set => SetValue(FileSuffixProperty, value); }
    public bool PickFolder { get => GetValue(PickFolderProperty); set => SetValue(PickFolderProperty, value); }
    public string PickerTitle { get => GetValue(PickerTitleProperty); set => SetValue(PickerTitleProperty, value); }

    bool _syncing;

    public PathBox()
    {
        InitializeComponent();
        Box.AsyncPopulator = (text, _) => Task.FromResult<IEnumerable<object>>(
            PathCompletion.Suggest(text ?? "", PickFolder ? (_ => false) : Matches));
        Box.TextChanged += (_, _) => OnTyped();
        Box.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        BrowseButton.Click += Browse;
    }

    public void FocusInput() => Box.Focus();

    bool Matches(string fileName) => FileSuffix is not { Length: > 0 } suffix || fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PathProperty && !_syncing && Box.Text != Path) Box.Text = Path;
        if (change.Property == PlaceholderProperty) Box.PlaceholderText = Placeholder;
    }

    // Backslashes become forward slashes as they're typed or pasted, keeping the caret where it was.
    void OnTyped()
    {
        var text = Box.Text ?? "";
        if (text.Contains('\\'))
        {
            Box.Text = PathCompletion.ToPosix(text);
            return;
        }
        _syncing = true;
        Path = text;
        _syncing = false;
    }

    // Handled here, ahead of the dialog: its Enter and Escape bindings would otherwise submit or cancel the whole form
    // while the list is what the key was meant for.
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!Box.IsDropDownOpen || e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Box.IsDropDownOpen = false;
            return;
        }
        if (e.Key is not (Key.Tab or Key.Enter)) return;
        var choice = Box.SelectedItem as string
            ?? PathCompletion.Suggest(Box.Text ?? "", PickFolder ? (_ => false) : Matches).FirstOrDefault();
        if (choice is null) return;

        if (e.Key == Key.Enter)
        {
            // Enter picks rather than descends, so the folder is taken without the slash that would open its list.
            choice = choice.Length > 1 ? choice.TrimEnd('/') : choice;
            Box.IsDropDownOpen = false;
            // Already typed in full: the Enter is for the form.
            if (string.Equals(choice, (Box.Text ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return;
            e.Handled = true;
            Box.Text = choice;
            Box.CaretIndex = choice.Length;
            return;
        }

        e.Handled = true;
        Box.Text = choice;
        Box.CaretIndex = choice.Length;
        // A folder keeps the list open so the next Tab goes one level deeper, like a shell.
        Box.IsDropDownOpen = choice.EndsWith('/');
    }

    async void Browse(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        IStorageItem? picked;
        if (PickFolder)
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = PickerTitle, AllowMultiple = false });
            picked = folders.FirstOrDefault();
        }
        else
        {
            var filters = FileSuffix is { Length: > 0 } suffix
                ? new[] { new FilePickerFileType(suffix) { Patterns = [$"*{suffix}"] } }
                : null;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = PickerTitle, AllowMultiple = false, FileTypeFilter = filters });
            picked = files.FirstOrDefault();
        }
        if (picked?.TryGetLocalPath() is { } local) Box.Text = PathCompletion.ToPosix(local);
    }
}
