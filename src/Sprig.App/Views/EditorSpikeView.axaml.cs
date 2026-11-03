using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using Sprig.App.Authoring;
using TextMateSharp.Grammars;
using Sprig.App.ViewModels;
using Sprig.Engine.Authoring;

namespace Sprig.App.Views;

public partial class EditorSpikeView : UserControl
{
    readonly DispatcherTimer _diagnosticsDelay = new() { Interval = TimeSpan.FromMilliseconds(400) };
    CompletionWindow? _completion;
    bool _loaded;

    public EditorSpikeView()
    {
        InitializeComponent();
        // AvaloniaEdit's built-in C# colours are made for a light background; a TextMate grammar with a dark theme
        // keeps method names and strings readable on the app's dark panels.
        var textMate = new RegistryOptions(ThemeName.DarkPlus);
        Editor.InstallTextMate(textMate).SetGrammar(textMate.GetScopeByLanguageId(textMate.GetLanguageByExtension(".cs").Id));
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.KeyDown += OnKeyDown;
        Editor.TextChanged += (_, _) => { _diagnosticsDelay.Stop(); _diagnosticsDelay.Start(); };
        _diagnosticsDelay.Tick += async (_, _) =>
        {
            _diagnosticsDelay.Stop();
            await RunDiagnostics();
        };

        AddEnvButton.Click += (_, _) => ApplyEdit(t => DefinitionEditor.AddEnv(t, "catalog-api", "Bus__Url", "\"amqp://localhost:5672\""), "Added Bus__Url.");
        PinButton.Click += (_, _) => ApplyEdit(t => DefinitionEditor.SetPin(t, "catalog-api", "http", "8100-8103"), "Pinned the http endpoint.");
        ImportButton.Click += (_, _) => ApplyEdit(t => DefinitionEditor.AddImport(t, "billing-url", "Url", "billing"), "Imported billing-url.");

        DataContextChanged += (_, _) =>
        {
            if (_loaded || Vm is null) return;
            Editor.Text = EditorSpikeViewModel.SampleText;
            _loaded = true;
        };
    }

    EditorSpikeViewModel? Vm => DataContext as EditorSpikeViewModel;

    internal async System.Threading.Tasks.Task RunDiagnostics()
    {
        if (Vm is not { } vm) return;
        var clock = Stopwatch.StartNew();
        try { vm.RecordDiagnostics(await vm.Host.DiagnoseAsync(Editor.Text), clock.Elapsed.TotalMilliseconds); }
        catch (Exception ex) { vm.HostStatus = $"Diagnostics failed: {ex.Message}"; }
    }

    void ApplyEdit(Func<string, EditResult> edit, string done)
    {
        if (Vm is not { } vm) return;
        var result = edit(Editor.Text);
        if (!result.Ok) { vm.EditMessage = result.Error ?? "Couldn't make that edit."; return; }
        // Replace only the changed region so the caret, scroll and undo history behave like a normal edit.
        var (start, oldLength, inserted) = Diff(Editor.Text, result.Text);
        Editor.Document.Replace(start, oldLength, inserted);
        vm.EditMessage = done;
    }

    static (int start, int oldLength, string inserted) Diff(string before, string after)
    {
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
        var endBefore = before.Length;
        var endAfter = after.Length;
        while (endBefore > start && endAfter > start && before[endBefore - 1] == after[endAfter - 1]) { endBefore--; endAfter--; }
        return (start, endBefore - start, after[start..endAfter]);
    }

    void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text == ".") _ = ShowCompletion();
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = ShowCompletion();
        }
    }

    async System.Threading.Tasks.Task ShowCompletion()
    {
        if (Vm is not { } vm) return;
        var caret = Editor.CaretOffset;
        var clock = Stopwatch.StartNew();
        HostResponse response;
        try { response = await vm.Host.CompleteAsync(Editor.Text, caret); }
        catch (Exception ex) { vm.HostStatus = $"Completion failed: {ex.Message}"; return; }
        vm.RecordCompletion(clock.Elapsed.TotalMilliseconds, response.Ms, response.Items?.Count ?? 0);

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
