using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Sprig.App.Controls;

/// <summary>C# to read, not edit: the same colours as the editor, without the language host behind it.</summary>
public partial class CodeViewer : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CodeViewer, string>(nameof(Text), "");

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public CodeViewer()
    {
        InitializeComponent();
        var textMate = new RegistryOptions(ThemeName.DarkPlus);
        Editor.InstallTextMate(textMate).SetGrammar(textMate.GetScopeByLanguageId(textMate.GetLanguageByExtension(".cs").Id));
        // As in CodeEditor: urls keep the string colour rather than an unreadable link blue.
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty) return;
        // Refilled rather than replaced, as in CodeEditor: a new document restarts TextMate's tokenizer and can be left
        // uncoloured.
        Editor.Document.Text = change.GetNewValue<string>() ?? "";
        Editor.Document.UndoStack.ClearAll();
        Editor.ScrollToHome();
    }
}
