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
using Sprig.Engine.Authoring;
using TextMateSharp.Grammars;

namespace Sprig.App.Controls;

/// <summary>
/// A definition's YAML with what a developer expects of an IDE: completion as a word is typed (and after a dot in a
/// reference, a colon, or on Ctrl+Space), what a key or reference means on hover, and squiggles under problems. All of
/// it comes from <see cref="Language"/>; without one, this is a plain text editor.
/// </summary>
public partial class CodeEditor : UserControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<CodeEditor, string>(nameof(Text), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<DefinitionLanguage?> LanguageProperty =
        AvaloniaProperty.Register<CodeEditor, DefinitionLanguage?>(nameof(Language));

    /// <summary>The file being edited, which problems are reported against.</summary>
    public static readonly StyledProperty<string> FilePathProperty =
        AvaloniaProperty.Register<CodeEditor, string>(nameof(FilePath), "sprig.yaml");

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public DefinitionLanguage? Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    public string FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    readonly ObservableCollection<EditorProblem> _problems = [];
    readonly Squiggles _squiggles = new();
    readonly DispatcherTimer _diagnoseDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer _hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer _hideDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    CompletionWindow? _completion;
    Point _hoverAt;
    (int Start, int End)? _hoverSpan;
    bool _syncing;

    public CodeEditor()
    {
        InitializeComponent();
        // AvaloniaEdit's built-in colours are made for a light background; a TextMate grammar with a dark theme keeps
        // keys and strings readable on the app's dark panels.
        var textMate = new RegistryOptions(ThemeName.DarkPlus);
        Editor.InstallTextMate(textMate).SetGrammar(textMate.GetScopeByLanguageId(textMate.GetLanguageByExtension(".yaml").Id));
        Editor.Options.ConvertTabsToSpaces = true;
        Editor.Options.IndentationSize = 2;
        // Links are drawn in a blue that can't be read on the dark panels, and a url in a definition is a value to
        // edit, not a page to visit, so it keeps the string colour.
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.TextArea.TextView.BackgroundRenderers.Add(_squiggles);
        _squiggles.Attach(Editor.Document);

        Editor.TextArea.TextEntering += OnTextEntering;
        Editor.TextArea.TextEntered += OnTextEntered;
        // Tunnelled, so the editor's own key handling doesn't swallow the shortcuts first.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Editor.TextChanged += (_, _) =>
        {
            if (!_syncing)
            {
                _syncing = true;
                try { Text = Editor.Text; }
                finally { _syncing = false; }
            }
            HideHover();
            _diagnoseDelay.Stop();
            _diagnoseDelay.Start();
        };
        _diagnoseDelay.Tick += async (_, _) =>
        {
            _diagnoseDelay.Stop();
            await DiagnoseAsync();
        };

        Editor.TextArea.TextView.PointerMoved += OnPointerMoved;
        // Leaving the text may be on the way into the card below it, so hiding waits a moment.
        Editor.TextArea.TextView.PointerExited += (_, _) => { _hoverDelay.Stop(); _hideDelay.Start(); };
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => HideHover();
        HoverCard.PointerEntered += (_, _) => _hideDelay.Stop();
        HoverCard.PointerExited += (_, _) => _hideDelay.Start();
        _hoverDelay.Tick += async (_, _) =>
        {
            _hoverDelay.Stop();
            await ShowHoverAsync();
        };
        _hideDelay.Tick += (_, _) => HideHover();

        ProblemList.ItemsSource = _problems;
        ProblemsSummary.Text = "Checking…";
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LanguageProperty)
        {
            _diagnoseDelay.Stop();
            _diagnoseDelay.Start();
            return;
        }
        if (change.Property != TextProperty || _syncing) return;
        var text = change.GetNewValue<string>() ?? "";
        if (Editor.Text == text) return;
        _syncing = true;
        try
        {
            // Refilled rather than replaced: a new document makes TextMate stop its tokenizer and start another, and
            // a file opened while that hand-over races can stay uncoloured. Refilling is the path typing takes.
            Editor.Document.Text = text;
            Editor.Document.UndoStack.ClearAll();
            _squiggles.Show(Editor.Document, []);
            Editor.CaretOffset = 0;
            Editor.ScrollToHome();
        }
        finally { _syncing = false; }
        _diagnoseDelay.Stop();
        _diagnoseDelay.Start();
    }

    IBrush Brush(string key) => this.TryFindResource(key, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    // ---- problems ----

    /// <summary>Check for problems now rather than after the typing pause; the headless renderer's timers don't tick.</summary>
    internal async Task DiagnoseAsync()
    {
        if (Language is not { } language) return;
        var text = Editor.Text;
        var path = FilePath;
        try
        {
            var all = await Task.Run(() => language.Diagnose(text, path));
            // Typed on while it was checked: the offsets no longer fit, and the next check is already due.
            if (Editor.Text != text) return;
            _squiggles.Show(Editor.Document, all);
            Editor.TextArea.TextView.InvalidateLayer(_squiggles.Layer);

            _problems.Clear();
            foreach (var d in all.Where(d => d.Severity == "Error")) _problems.Add(d);
            var warnings = all.Count - _problems.Count;
            ProblemsSummary.Text = (_problems.Count, warnings) switch
            {
                (0, 0) => "No problems.",
                (0, var w) => $"No errors, {w} warning{(w == 1 ? "" : "s")} (hover the amber underlines).",
                (var e, 0) => $"{e} error{(e == 1 ? "" : "s")}",
                (var e, var w) => $"{e} error{(e == 1 ? "" : "s")}, {w} warning{(w == 1 ? "" : "s")}",
            };
            ProblemsSummary.Foreground = _problems.Count > 0 ? Brush("DangerBrush") : warnings > 0 ? Brush("WarnBrush") : Brush("OkBrush");
        }
        catch (Exception e)
        {
            ProblemsSummary.Text = $"Couldn't check for problems: {e.Message}";
            ProblemsSummary.Foreground = Brush("MutedBrush");
        }
    }

    void OnProblemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not EditorProblem problem) return;
        var offset = Math.Clamp(problem.Start, 0, Editor.Document.TextLength);
        Editor.CaretOffset = offset;
        Editor.ScrollTo(Editor.Document.GetLineByOffset(offset).LineNumber, 0);
        Editor.TextArea.Focus();
    }

    // ---- keys ----

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = ShowCompletionAsync(asked: true);
        }
        else if (e.Key == Key.Escape)
        {
            HideHover();
        }
    }

    // ---- completion ----

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

    /// <summary>A character that ends what's being completed accepts the highlighted item first, as in an IDE: typing
    /// <c>:</c> after <c>ru</c> gives <c>run:</c>, and <c>.</c> after <c>${cat</c> gives <c>${catalog-api.</c>. A space
    /// just closes the list.</summary>
    void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completion is null || e.Text is not { Length: > 0 } typed || IsWordChar(typed[0]) || typed[0] == '/') return;
        if (typed[0] is '.' or ':' or '}' or ',' or ']') _completion.CompletionList.RequestInsertion(e);
        else _completion.Close();
    }

    void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: 1 } typed) return;
        var at = Editor.CaretOffset - 1;
        char Before(int back) => at - back >= 0 ? Editor.Document.GetCharAt(at - back) : '\n';

        // Where something new can start: a reference's next part, a value after its key, a list's next item, a path's
        // next folder.
        var opens = typed[0] switch
        {
            '.' or '/' or '[' => true,
            '{' => Before(1) == '$',
            ' ' => Before(1) is ':' or ',',
            _ => false,
        };
        if (opens)
        {
            _ = ShowCompletionAsync(asked: false);
            return;
        }
        // The first letter of a word opens the list; the letters after it narrow the one that's open.
        var startsWord = char.IsLetter(typed[0]) && !IsWordChar(Before(1));
        if (_completion is null && startsWord) _ = ShowCompletionAsync(asked: false);
    }

    /// <param name="asked">Ctrl+Space: show the list even with nothing typed.</param>
    async Task ShowCompletionAsync(bool asked)
    {
        if (Language is not { } language) return;
        var caret = Editor.CaretOffset;
        var text = Editor.Text;
        EditorCompletions? response;
        try { response = await Task.Run(() => language.Complete(text, caret)); }
        catch (Exception) { return; }

        if (response is not { Items.Count: > 0 } || Editor.Text != text) return;
        var start = response.SpanStart;
        // Typed on, or moved away, while it was asked.
        if (Editor.CaretOffset < start || Editor.CaretOffset - start > 200 || start > Editor.Document.TextLength) return;

        _completion?.Close();
        var window = new CompletionWindow(Editor.TextArea) { StartOffset = start, CloseWhenCaretAtBeginning = !asked };
        foreach (var item in response.Items) window.CompletionList.CompletionData.Add(new CompletionItem(item));
        var typed = Editor.Document.GetText(start, Editor.CaretOffset - start);
        if (typed.Length > 0) window.CompletionList.SelectItem(typed);
        if (typed.Length > 0 && window.CompletionList.CurrentList?.Count == 0) return;
        window.Closed += (_, _) => { if (_completion == window) _completion = null; };
        _completion = window;
        window.Show();
    }

    sealed class CompletionItem(EditorCompletion item) : ICompletionData
    {
        public IImage? Image => null;
        public string Text => item.Label;
        public double Priority => 0;

        public object Content => new TextBlock
        {
            Inlines =
            [
                new Avalonia.Controls.Documents.Run(item.Label),
                new Avalonia.Controls.Documents.Run($"   {item.Kind}") { Foreground = Brushes.Gray, FontSize = 11 },
            ],
        };

        public object? Description => item.Detail is { Length: > 0 } detail
            ? new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }
            : null;

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            => textArea.Document.Replace(completionSegment, item.Label);
    }

    // ---- hover ----

    void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _hoverAt = e.GetPosition(Editor);
        // Still over what's shown: leave it up rather than flicker.
        if (HoverPopup.IsOpen && OffsetAt(_hoverAt) is { } offset && _hoverSpan is { } span && offset >= span.Start && offset <= span.End)
        {
            _hideDelay.Stop();
            return;
        }
        // Moved off it, perhaps on the way down into the card: give it a moment before it goes.
        if (HoverPopup.IsOpen) _hideDelay.Start();
        _hoverDelay.Stop();
        _hoverDelay.Start();
    }

    int? OffsetAt(Point point)
        => Editor.GetPositionFromPoint(point) is { } position && !position.IsAtEndOfLine
            ? Editor.Document.GetOffset(position.Location)
            : null;

    async Task ShowHoverAsync()
    {
        if (_completion is not null || OffsetAt(_hoverAt) is not { } offset) return;
        var text = Editor.Text;
        var problems = _squiggles.At(offset);
        EditorHover? hover = null;
        if (Language is { } language)
        {
            try { hover = await Task.Run(() => language.Hover(text, offset)); }
            catch (Exception) { /* the problems alone are still worth showing */ }
        }
        if (Editor.Text != text || OffsetAt(_hoverAt) != offset || (hover is null && problems.Count == 0)) return;

        HoverProblem.IsVisible = problems.Count > 0;
        HoverProblem.Text = string.Join("\n", problems.Select(p => p.Message));
        HoverProblem.Foreground = problems.Any(p => p.Severity == "Error") ? Brush("DangerBrush") : Brush("WarnBrush");
        HoverSignature.IsVisible = hover is not null;
        HoverSignature.Text = hover?.Title;
        HoverDocs.IsVisible = hover?.Docs is not null;
        HoverDocs.Text = hover?.Docs;
        var span = hover is not null ? (hover.Start, hover.End)
            : (problems.Min(p => p.Start), problems.Max(p => Math.Max(p.End, p.Start + 1)));
        _hideDelay.Stop();
        HoverPopup.IsOpen = false;
        AnchorBelow(span.Item1);
        _hoverSpan = span;
        HoverPopup.IsOpen = true;
    }

    /// <summary>Under the line of <paramref name="offset"/>, from where the hovered word starts.</summary>
    void AnchorBelow(int offset)
    {
        var view = Editor.TextArea.TextView;
        var position = new AvaloniaEdit.TextViewPosition(Editor.Document.GetLocation(Math.Clamp(offset, 0, Editor.Document.TextLength)));
        var top = view.GetVisualPosition(position, AvaloniaEdit.Rendering.VisualYPosition.LineTop) - view.ScrollOffset;
        var bottom = view.GetVisualPosition(position, AvaloniaEdit.Rendering.VisualYPosition.LineBottom) - view.ScrollOffset;
        HoverPopup.PlacementTarget = view;
        HoverPopup.PlacementRect = new Rect(top.X, top.Y, 1, Math.Max(1, bottom.Y - top.Y));
    }

    void HideHover()
    {
        _hideDelay.Stop();
        HoverPopup.IsOpen = false;
        _hoverSpan = null;
    }
}
