using System.Collections;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace Sprig.App.Controls;

/// <summary>
/// A field for <c>${sprig.*}</c> templates. While idle it shows the value with each token coloured —
/// green when it names a known variable, red when it doesn't (the same rule the config validator
/// applies on save). Click or tab in and it becomes a plain text box that offers inline completion
/// from <see cref="Variables"/> (the repo's <c>workspace</c> + declared inputs) as you type an open
/// <c>${…</c>. Matching/splicing lives in <see cref="SprigTokenCompletion"/>.
/// </summary>
public partial class SprigTokenBox : UserControl
{
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<SprigTokenBox, string?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<IEnumerable?> VariablesProperty =
        AvaloniaProperty.Register<SprigTokenBox, IEnumerable?>(nameof(Variables));

    public static readonly StyledProperty<IEnumerable?> OpenCapabilitiesProperty =
        AvaloniaProperty.Register<SprigTokenBox, IEnumerable?>(nameof(OpenCapabilities));

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<SprigTokenBox, string?>(nameof(Watermark));

    public string? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IEnumerable? Variables { get => GetValue(VariablesProperty); set => SetValue(VariablesProperty, value); }

    /// <summary>Capability heads (needs/aliases) whose outputs live in another repo — a dotted
    /// <c>${sprig.&lt;head&gt;.&lt;anything&gt;}</c> is coloured known when its head is one of these, since the
    /// output is only knowable at map-resolve time (mirrors the config validator).</summary>
    public IEnumerable? OpenCapabilities { get => GetValue(OpenCapabilitiesProperty); set => SetValue(OpenCapabilitiesProperty, value); }
    public string? Watermark { get => GetValue(WatermarkProperty); set => SetValue(WatermarkProperty, value); }

    private static readonly Regex TokenPattern = new(@"\$\{[^}]*\}", RegexOptions.Compiled);
    private static readonly IBrush KnownBrush = new SolidColorBrush(Color.Parse("#4ADE80"));   // Ok
    private static readonly IBrush UnknownBrush = new SolidColorBrush(Color.Parse("#F87171")); // Danger
    private const string SprigPrefix = "${sprig.";

    private bool _editing;

    public SprigTokenBox()
    {
        InitializeComponent();

        Display.GotFocus += (_, _) => BeginEdit();
        Display.AddHandler(PointerPressedEvent, OnDisplayPointerPressed, RoutingStrategies.Tunnel);

        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        Editor.PropertyChanged += OnEditorPropertyChanged;
        Editor.LostFocus += (_, _) => EndEdit();

        CompletionList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);

        // A box that was laid out while hidden (e.g. a collapsed binding row, or a not-yet-opened
        // flyout) can have a stale/empty text layout when it first becomes visible. Re-render the
        // highlight when the control enters the viewport so the value shows without needing a click.
        EffectiveViewportChanged += (_, _) => { if (!_editing) RenderHighlight(); };

        RenderHighlight();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
            RenderHighlight();
        else if (change.Property == VariablesProperty || change.Property == OpenCapabilitiesProperty)
        {
            // Follow live edits to the variable/capability set (inputs, provides, needs edited above) so
            // colours stay current.
            if (change.OldValue is INotifyCollectionChanged oldCol) oldCol.CollectionChanged -= OnVariablesChanged;
            if (change.NewValue is INotifyCollectionChanged newCol) newCol.CollectionChanged += OnVariablesChanged;
            RenderHighlight();
        }
    }

    private void OnVariablesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RenderHighlight();

    // -- edit-mode toggle -----------------------------------------------------

    // Land the caret where the click fell, not at the end. Hit-test the coloured display's layout
    // (its glyphs line up 1:1 with the editor's, same font/size), then edit from that index.
    private void OnDisplayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        int? caret = null;
        try
        {
            var hit = Highlight.TextLayout.HitTestPoint(e.GetPosition(Highlight));
            caret = hit.CharacterHit.FirstCharacterIndex + hit.CharacterHit.TrailingLength;
        }
        catch { /* no layout yet → fall back to end-of-text */ }
        BeginEdit(caret);
    }

    private void BeginEdit(int? caret = null)
    {
        if (_editing)
            return;
        _editing = true;
        Display.IsVisible = false;
        Editor.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            Editor.Focus();
            var len = Editor.Text?.Length ?? 0;
            Editor.CaretIndex = caret is int c ? Math.Clamp(c, 0, len) : len;
        });
    }

    private void EndEdit()
    {
        if (!_editing)
            return;
        Close();
        _editing = false;
        Editor.IsVisible = false;
        Display.IsVisible = true;
        RenderHighlight();
    }

    // -- idle colouring -------------------------------------------------------

    private void RenderHighlight()
    {
        if (Highlight.Inlines is null)
            return;
        Highlight.Inlines.Clear();

        var value = Value ?? string.Empty;
        var known = new HashSet<string>(Names(), StringComparer.Ordinal);
        var open = new HashSet<string>(Names(OpenCapabilities), StringComparer.Ordinal);

        var index = 0;
        foreach (Match match in TokenPattern.Matches(value))
        {
            if (match.Index > index)
                Highlight.Inlines.Add(new Run(value[index..match.Index]));

            var brush = TokenBrush(match.Value, known, open);
            var run = new Run(match.Value);
            if (brush is not null) { run.Foreground = brush; run.FontWeight = FontWeight.SemiBold; }
            Highlight.Inlines.Add(run);

            index = match.Index + match.Length;
        }
        if (index < value.Length)
            Highlight.Inlines.Add(new Run(value[index..]));
    }

    /// <summary>Green/red for a <c>${sprig.*}</c> token; null (default colour) for a passthrough <c>${…}</c>.
    /// A dotted reference whose head is an open capability (a need/alias) is known too — its output can't be
    /// enumerated here, so the head alone greens it, matching the config validator.</summary>
    private static IBrush? TokenBrush(string token, HashSet<string> known, HashSet<string> open)
    {
        if (!token.StartsWith(SprigPrefix, StringComparison.Ordinal) || !token.EndsWith('}'))
            return null;
        var name = token[SprigPrefix.Length..^1].Trim();
        return Sprig.Core.Config.ConfigReferences.IsReferenceKnown(name, known, open) ? KnownBrush : UnknownBrush;
    }

    // -- completion -----------------------------------------------------------

    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty || e.Property == TextBox.CaretIndexProperty)
            UpdateCompletion();
    }

    private void UpdateCompletion()
    {
        var text = Editor.Text ?? string.Empty;
        var caret = Math.Clamp(Editor.CaretIndex, 0, text.Length);
        var search = text[..caret];

        if (SprigTokenCompletion.TrailingFragment(search) is null)
        {
            Close();
            return;
        }

        var matches = SprigTokenCompletion.Tokens(Names())
            .Where(token => SprigTokenCompletion.Matches(search, token))
            .ToList();

        if (matches.Count == 0)
        {
            Close();
            return;
        }

        CompletionList.ItemsSource = matches;
        CompletionList.SelectedIndex = 0;
        CompletionPopup.IsOpen = true;
    }

    private IReadOnlyList<string> Names() => Names(Variables);

    private static IReadOnlyList<string> Names(IEnumerable? source)
    {
        var names = new List<string>();
        if (source is IEnumerable items)
            foreach (var item in items)
                if (item is string s && !string.IsNullOrWhiteSpace(s))
                    names.Add(s);
        return names;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (!CompletionPopup.IsOpen)
            return;

        switch (e.Key)
        {
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.Enter or Key.Tab: AcceptSelected(); e.Handled = true; break;
            case Key.Escape: Close(); e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        var count = CompletionList.ItemCount;
        if (count == 0)
            return;
        var next = CompletionList.SelectedIndex + delta;
        CompletionList.SelectedIndex = (next % count + count) % count;
    }

    // Accept on press (before focus can leave the editor and dismiss the popup).
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((e.Source as StyledElement)?.DataContext is string token)
        {
            Accept(token);
            e.Handled = true;
        }
    }

    private void AcceptSelected()
    {
        if (CompletionList.SelectedItem is string token)
            Accept(token);
    }

    private void Accept(string token)
    {
        var (text, caret) = SprigTokenCompletion.Replace(Editor.Text, Editor.CaretIndex, token);
        Editor.Text = text;
        Editor.CaretIndex = caret;
        Close();
    }

    private void Close() => CompletionPopup.IsOpen = false;
}
