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
/// A definition's C# with what a developer expects of an IDE: completion as a word is typed (and after a dot, or on
/// Ctrl+Space), docs on hover, squiggles under problems and Alt+Shift+F to format. All of it comes from the language
/// host, which runs in its own process: if it fails, this stays a plain text editor.
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
    readonly Squiggles _squiggles = new();
    readonly DispatcherTimer _diagnoseDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly DispatcherTimer _hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    CompletionWindow? _completion;
    Point _hoverAt;
    (int Start, int End)? _hoverSpan;
    bool _syncing;

    public CodeEditor()
    {
        InitializeComponent();
        // AvaloniaEdit's built-in C# colours are made for a light background; a TextMate grammar with a dark theme
        // keeps method names and strings readable on the app's dark panels.
        var textMate = new RegistryOptions(ThemeName.DarkPlus);
        Editor.InstallTextMate(textMate).SetGrammar(textMate.GetScopeByLanguageId(textMate.GetLanguageByExtension(".cs").Id));
        Editor.Options.ConvertTabsToSpaces = true;
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
        Editor.TextArea.TextView.PointerExited += (_, _) => { _hoverDelay.Stop(); HideHover(); };
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => HideHover();
        _hoverDelay.Tick += async (_, _) =>
        {
            _hoverDelay.Stop();
            await ShowHoverAsync();
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
            _squiggles.Attach(Editor.Document);
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
        if (Host is not { } host) return;
        var text = Editor.Text;
        try
        {
            var response = await host.DiagnoseAsync(text);
            // Typed on while the host answered: the offsets no longer fit, and the next check is already due.
            if (Editor.Text != text) return;
            var all = response.Diagnostics ?? [];
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
        if ((sender as Control)?.Tag is not HostDiagnostic problem) return;
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
        else if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Alt | KeyModifiers.Shift))
        {
            e.Handled = true;
            _ = FormatAsync();
        }
        else if (e.Key == Key.Escape)
        {
            HideHover();
        }
    }

    async Task FormatAsync()
    {
        if (Host is not { } host) return;
        var before = Editor.Text;
        string? formatted;
        try { formatted = (await host.FormatAsync(before)).Text; }
        catch (Exception) { return; }
        if (formatted is null || formatted == before || Editor.Text != before) return;

        // Only the changed stretch is replaced, as one step, so the caret stays put and one undo reverts it.
        var (start, length, inserted) = Diff(before, formatted);
        var caret = Editor.CaretOffset;
        Editor.Document.Replace(start, length, inserted);
        Editor.CaretOffset = Math.Clamp(caret <= start ? caret : caret + inserted.Length - length, 0, Editor.Document.TextLength);
    }

    static (int Start, int Length, string Inserted) Diff(string before, string after)
    {
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
        var endBefore = before.Length;
        var endAfter = after.Length;
        while (endBefore > start && endAfter > start && before[endBefore - 1] == after[endAfter - 1]) { endBefore--; endAfter--; }
        return (start, endBefore - start, after[start..endAfter]);
    }

    // ---- completion ----

    static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>A character that can't be part of a name accepts the highlighted item first, as in an IDE: typing
    /// <c>(</c> after <c>Ht</c> gives <c>Http(</c>. A space or an operator just closes the list.</summary>
    void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (_completion is null || e.Text is not { Length: > 0 } typed || IsWordChar(typed[0])) return;
        if (typed[0] is '(' or '.' or ';' or ',' or ')' or '<' or '[') _completion.CompletionList.RequestInsertion(e);
        else _completion.Close();
    }

    void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: 1 } typed) return;
        if (typed == ".")
        {
            _ = ShowCompletionAsync(asked: false);
            return;
        }
        // The first letter of a word opens the list; the letters after it narrow the one that's open.
        var at = Editor.CaretOffset - 1;
        var startsWord = (char.IsLetter(typed[0]) || typed[0] == '_') && (at == 0 || !IsWordChar(Editor.Document.GetCharAt(at - 1)));
        if (_completion is null && startsWord) _ = ShowCompletionAsync(asked: false);
    }

    /// <param name="asked">Ctrl+Space: show the list even where a new name is probably being typed.</param>
    async Task ShowCompletionAsync(bool asked)
    {
        if (Host is not { } host) return;
        var caret = Editor.CaretOffset;
        HostResponse response;
        try { response = await host.CompleteAsync(Editor.Text, caret); }
        catch (Exception) { return; }

        if (response.Items is not { Count: > 0 } items || (response.Suggestion == true && !asked)) return;
        var start = response.SpanStart ?? caret;
        // Typed on, or moved away, while the host answered.
        if (Editor.CaretOffset < start || Editor.CaretOffset - start > 64 || start > Editor.Document.TextLength) return;

        _completion?.Close();
        var window = new CompletionWindow(Editor.TextArea) { StartOffset = start, CloseWhenCaretAtBeginning = !asked };
        foreach (var (item, index) in items.Select((item, index) => (item, index)).OrderBy(x => x.item.Sort ?? x.item.Label, StringComparer.Ordinal))
            window.CompletionList.CompletionData.Add(new CompletionItem(item, index, host));
        var typed = Editor.Document.GetText(start, Editor.CaretOffset - start);
        if (typed.Length > 0) window.CompletionList.SelectItem(typed);
        if (typed.Length > 0 && window.CompletionList.CurrentList?.Count == 0) return;
        window.Closed += (_, _) => { if (_completion == window) _completion = null; };
        _completion = window;
        window.Show();
    }

    /// <summary>One completion item. Its signature and docs are fetched when it's highlighted, not for the whole list.</summary>
    sealed class CompletionItem(HostCompletionItem item, int index, LanguageHostClient host) : ICompletionData
    {
        TextBlock? _description;

        public IImage? Image => null;
        public string Text => item.Label;
        public double Priority => 0;

        public object Content => new TextBlock
        {
            Inlines =
            [
                new Avalonia.Controls.Documents.Run(item.Label),
                new Avalonia.Controls.Documents.Run(item.Suffix ?? "") { Foreground = Brushes.Gray },
                new Avalonia.Controls.Documents.Run(item.Kind is { Length: > 0 } kind ? $"   {kind}" : "") { Foreground = Brushes.Gray, FontSize = 11 },
            ],
        };

        public object? Description
        {
            get
            {
                if (_description is not null) return _description;
                _description = new TextBlock { Text = item.Detail ?? "…", TextWrapping = TextWrapping.Wrap, MaxWidth = 480 };
                _ = Fetch(_description);
                return _description;
            }
        }

        async Task Fetch(TextBlock target)
        {
            try
            {
                if ((await host.DescribeAsync(index)).Text is { Length: > 0 } text) target.Text = text;
                else if (item.Detail is null) target.Text = item.Label;
            }
            catch (Exception) { target.Text = item.Label; }
        }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            => textArea.Document.Replace(completionSegment, item.Label);
    }

    // ---- hover ----

    void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _hoverAt = e.GetPosition(Editor);
        // Still over what's shown: leave it up rather than flicker.
        if (HoverPopup.IsOpen && OffsetAt(_hoverAt) is { } offset && _hoverSpan is { } span && offset >= span.Start && offset <= span.End)
            return;
        HideHover();
        _hoverDelay.Stop();
        _hoverDelay.Start();
    }

    int? OffsetAt(Point point)
        => Editor.GetPositionFromPoint(point) is { } position && !position.IsAtEndOfLine
            ? Editor.Document.GetOffset(position.Location)
            : null;

    async Task ShowHoverAsync()
    {
        if (Host is not { } host || _completion is not null || OffsetAt(_hoverAt) is not { } offset) return;
        var text = Editor.Text;
        var problems = _squiggles.At(offset);
        HostHover? hover = null;
        try { hover = (await host.HoverAsync(text, offset)).Hover; }
        catch (Exception) { /* the problems alone are still worth showing */ }
        if (Editor.Text != text || OffsetAt(_hoverAt) != offset || (hover is null && problems.Count == 0)) return;

        HoverProblem.IsVisible = problems.Count > 0;
        HoverProblem.Text = string.Join("\n", problems.Select(p => $"{p.Id}: {p.Message}"));
        HoverProblem.Foreground = problems.Any(p => p.Severity == "Error") ? Brush("DangerBrush") : Brush("WarnBrush");
        HoverSignature.IsVisible = hover is not null;
        HoverSignature.Text = hover?.Signature;
        HoverDocs.IsVisible = hover?.Docs is not null;
        HoverDocs.Text = hover?.Docs;
        _hoverSpan = hover is not null ? (hover.Start, hover.End)
            : (problems.Min(p => p.Start), problems.Max(p => Math.Max(p.End, p.Start + 1)));
        HoverPopup.IsOpen = true;
    }

    void HideHover()
    {
        HoverPopup.IsOpen = false;
        _hoverSpan = null;
    }
}
