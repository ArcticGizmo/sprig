using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit.Document;
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
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty) return;
        Editor.Document = new TextDocument(change.GetNewValue<string>() ?? "");
        Editor.ScrollToHome();
    }
}
