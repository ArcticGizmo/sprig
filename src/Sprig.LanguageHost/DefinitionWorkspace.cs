using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace Sprig.LanguageHost;

/// <summary>
/// One sprig.cs in a Roslyn workspace set up the way <c>dotnet run</c> compiles it: top-level statements,
/// the default implicit usings, the FileBasedProgram parser feature for <c>#:</c> lines, and the SDK referenced.
/// </summary>
public sealed class DefinitionWorkspace
{
    const string ImplicitUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    readonly AdhocWorkspace _workspace;
    readonly DocumentId _documentId;

    public DefinitionWorkspace()
    {
        var assemblies = MefHostServices.DefaultAssemblies
            .Add(Assembly.Load("Microsoft.CodeAnalysis.Features"))
            .Add(Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features"));
        _workspace = new AdhocWorkspace(MefHostServices.Create(assemblies));

        var parse = new CSharpParseOptions(LanguageVersion.Preview)
            .WithFeatures([new KeyValuePair<string, string>("FileBasedProgram", "true")]);
        var project = ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Create(), "sprig", "sprig", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: parse,
            metadataReferences: References());
        var solution = _workspace.CurrentSolution.AddProject(project);

        solution = solution.AddDocument(DocumentId.CreateNewId(project.Id), "Usings.g.cs", SourceText.From(ImplicitUsings));
        _documentId = DocumentId.CreateNewId(project.Id);
        solution = solution.AddDocument(_documentId, "sprig.cs", SourceText.From(""));
        _workspace.TryApplyChanges(solution);
    }

    // Kept so a description can be fetched for one item when it's highlighted, rather than for every item up front.
    Document? _completedIn;
    IReadOnlyList<CompletionItem> _completed = [];

    public async Task<Response> CompleteAsync(int id, string text, int position)
    {
        var document = WithText(text);
        var service = CompletionService.GetService(document)!;
        var list = await service.GetCompletionsAsync(document, Math.Clamp(position, 0, text.Length));
        _completedIn = document;
        _completed = list.ItemsList.ToArray();
        var items = _completed
            .Select(i => new CompletionItemDto(i.DisplayText, i.Tags.FirstOrDefault() ?? "",
                i.InlineDescription is { Length: > 0 } d ? d : null,
                i.DisplayTextSuffix is { Length: > 0 } s ? s : null, i.SortText))
            .ToArray();
        return new Response(id, 0, Items: items, SpanStart: list.Span.Start, Suggestion: list.SuggestionModeItem is not null);
    }

    /// <summary>The description of item <paramref name="index"/> of the last completion list: its signature and docs.</summary>
    public async Task<string?> DescribeAsync(int index)
    {
        if (_completedIn is not { } document || index < 0 || index >= _completed.Count) return null;
        var description = await CompletionService.GetService(document)!.GetDescriptionAsync(document, _completed[index]);
        return description?.Text is { Length: > 0 } text ? text : null;
    }

    public async Task<HoverDto?> HoverAsync(string text, int position)
    {
        var document = WithText(text);
        var info = await QuickInfoService.GetService(document)!.GetQuickInfoAsync(document, Math.Clamp(position, 0, text.Length));
        if (info is null) return null;
        string Section(string kind) => string.Join("", info.Sections.Where(s => s.Kind == kind).Select(s => s.Text)).Trim();
        var signature = Section(QuickInfoSectionKinds.Description);
        if (signature.Length == 0) return null;
        var docs = string.Join("\n\n", new[] { QuickInfoSectionKinds.DocumentationComments, QuickInfoSectionKinds.RemarksDocumentationComments,
                QuickInfoSectionKinds.ReturnsDocumentationComments, QuickInfoSectionKinds.TypeParameters, QuickInfoSectionKinds.AnonymousTypes }
            .Select(Section).Where(s => s.Length > 0));
        return new HoverDto(info.Span.Start, info.Span.End, signature, docs.Length > 0 ? docs : null);
    }

    /// <summary>The whole file laid out the way the IDE's Format Document would.</summary>
    public async Task<string> FormatAsync(string text)
    {
        var formatted = await Formatter.FormatAsync(WithText(text));
        return (await formatted.GetTextAsync()).ToString();
    }

    public async Task<IReadOnlyList<DiagnosticDto>> DiagnoseAsync(string text)
    {
        var document = WithText(text);
        var compilation = await document.Project.GetCompilationAsync();
        var tree = await document.GetSyntaxTreeAsync();
        return compilation!.GetDiagnostics()
            .Where(d => d.Location.SourceTree == tree && d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => new DiagnosticDto(
                d.Location.SourceSpan.Start, d.Location.SourceSpan.End,
                d.Location.GetLineSpan().StartLinePosition.Line + 1,
                d.Severity.ToString(), d.Id, d.GetMessage()))
            .ToArray();
    }

    Document WithText(string text)
    {
        var solution = _workspace.CurrentSolution.WithDocumentText(_documentId, SourceText.From(text));
        _workspace.TryApplyChanges(solution);
        return _workspace.CurrentSolution.GetDocument(_documentId)!;
    }

    /// <summary>The running framework's assemblies plus the SDK, with its XML docs for completion descriptions.</summary>
    static ImmutableArray<MetadataReference> References()
    {
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is var f && (f.StartsWith("System.", StringComparison.Ordinal) || f is "mscorlib.dll" or "netstandard.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

        var sdk = typeof(Sprig.Repo).Assembly.Location;
        var xml = Path.ChangeExtension(sdk, ".xml");
        var sdkReference = MetadataReference.CreateFromFile(sdk,
            documentation: File.Exists(xml) ? XmlDocumentationProvider.CreateFromFile(xml) : null);

        return [.. framework, sdkReference];
    }
}
