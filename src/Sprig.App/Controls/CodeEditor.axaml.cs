using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using Sprig.App.Authoring;
using TextMateSharp.Grammars;

namespace Sprig.App.Controls;

/// <summary>
/// A definition's C# with completion (after a dot, or Ctrl+Space) and compile errors from the language host, which
/// runs in its own process: if it fails, this stays a plain text editor.
/// </summary>
public partial class CodeEditor : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<LanguageHostClient?> HostProperty =
        AvaloniaProperty.Register<CodeEditor, LanguageHostClient?>(nameof(Host));

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public LanguageHostClient? Host
    {
        get => GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    readonly ObservableCollection<HostDiagnostic> _problems = [];
    readonly DispatcherTimer _diagnoseDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    CompletionWindow? _completion;
    bool _syncing;

    public CodeEditor()
    {
        InitializeComponent();
        // AvaloniaEdit's built-in C# colours are made for a light background; a TextMate grammar with a dark theme
        // keeps method names and strings readable on the app's dark panels.
        var textMate = new RegistryOptions(ThemeName.DarkPlus);
        Editor.InstallTextMate(textMate).SetGrammar(textMate.GetScopeByLanguageId(textMate.GetLanguageByExtension(".cs").Id));
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.KeyDown += OnKeyDown;
        Editor.TextChanged += (_, _) =>
        {
            if (!_syncing)
            {
                _syncing = true;
                try { Text = Editor.Text; }
                finally { _syncing = false; }
            }
            _diagnoseDelay.Stop();
            _diagnoseDelay.Start();
        };
        _diagnoseDelay.Tick += async (_, _) =>
        {
            _diagnoseDelay.Stop();
            await DiagnoseAsync();
        };
        ProblemList.ItemsSource = _problems;
        ProblemsSummary.Text = "Checking…";
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty || _syncing) return;
        var text = change.GetNewValue<string>() ?? "";
        if (Editor.Text == text) return;
        _syncing = true;
        try
        {
            Editor.Document = new TextDocument(text);
            Editor.ScrollToHome();
        }
        finally { _syncing = false; }
        _diagnoseDelay.Stop();
        _diagnoseDelay.Start();
    }

    async Task DiagnoseAsync()
    {
        if (Host is not { } host) return;
        try
        {
            var response = await host.DiagnoseAsync(Editor.Text);
            _problems.Clear();
            foreach (var d in (response.Diagnostics ?? []).Where(d => d.Severity == "Error")) _problems.Add(d);
            ProblemsSummary.Text = _problems.Count switch
            {
                0 => "No compile errors.",
                1 => "1 compile error",
                var n => $"{n} compile errors",
            };
            ProblemsSummary.Foreground = _problems.Count == 0 ? Brush("OkBrush") : Brush("DangerBrush");
        }
        catch (Exception e)
        {
            ProblemsSummary.Text = $"Couldn't check for errors: {e.Message}";
            ProblemsSummary.Foreground = Brush("MutedBrush");
        }
    }

    IBrush Brush(string key) => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    void OnProblemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not HostDiagnostic problem) return;
        var offset = Math.Clamp(problem.Start, 0, Editor.Document.TextLength);
        Editor.CaretOffset = offset;
        Editor.ScrollTo(Editor.Document.GetLineByOffset(offset).LineNumber, 0);
        Editor.TextArea.Focus();
    }

    void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text == ".") _ = ShowCompletionAsync();
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = ShowCompletionAsync();
        }
    }

    async Task ShowCompletionAsync()
    {
        if (Host is not { } host) return;
        var caret = Editor.CaretOffset;
        HostResponse response;
        try { response = await host.CompleteAsync(Editor.Text, caret); }
        catch (Exception) { return; }

        // The user may have typed on while the host answered; only show the list if the caret hasn't moved away.
        if (response.Items is not { Count: > 0 } items || Math.Abs(Editor.CaretOffset - caret) > 3) return;

        _completion?.Close();
        _completion = new CompletionWindow(Editor.TextArea);
        foreach (var item in items.OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase))
            _completion.CompletionList.CompletionData.Add(new CompletionItem(item));
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }

    sealed class CompletionItem(HostCompletionItem item) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => item.Label;
        public object Content => item.Kind is { Length: > 0 } kind ? $"{item.Label}   ·  {kind}" : item.Label;
        public object? Description => item.Detail;
        public double Priority => 0;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            => textArea.Document.Replace(completionSegment, item.Label);
    }
}
