using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Sprig.Engine.Authoring;

/// <summary>An app or container the GUI found in a definition. <see cref="Editable"/> is false when it's declared in
/// a way the GUI can't safely change (a loop, a lambda, a computed name), so the GUI shows it read-only.</summary>
public sealed record FoundResource(string Name, string Kind, string? Variable, bool Editable, int Line);

public sealed record EditResult(bool Ok, string Text, string? Error = null)
{
    public static EditResult Fail(string text, string error) => new(false, text, error);
}

/// <summary>
/// Makes the small edits the GUI offers (add an env var, pin a port, add an import) directly in a definition's C#.
/// Roslyn finds where the edit goes, then the edit is plain text inserted at that spot, so every other character
/// of the file, comments and layout included, is left exactly as written.
/// </summary>
public static class DefinitionEditor
{
    static readonly HashSet<string> ResourceMethods = ["DotNet", "Node", "Container"];

    /// <summary>Without the FileBasedProgram feature Roslyn reports every <c>#:</c> line as an error.</summary>
    public static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Preview)
        .WithFeatures([new KeyValuePair<string, string>("FileBasedProgram", "true")]);

    public static IReadOnlyList<FoundResource> Inspect(string text)
    {
        var root = Parse(text);
        return Declarations(root)
            .Select(d => new FoundResource(
                d.Name ?? "(computed)",
                d.Method,
                d.Variable,
                d.Editable,
                d.Create.GetLocation().GetLineSpan().StartLinePosition.Line + 1))
            .ToArray();
    }

    /// <summary>Add <c>.Env("key", value)</c> to an app's chain, after its last env var, or after its endpoints.</summary>
    /// <param name="valueCode">The C# for the value, e.g. <c>"\"literal\""</c>, <c>"smtp"</c> or <c>"$\"http://{api.Port}\""</c>.</param>
    public static EditResult AddEnv(string text, string app, string key, string valueCode)
    {
        if (Find(text, app) is not { } decl) return EditResult.Fail(text, $"no editable app named '{app}'");
        if (decl.Chain.Any(c => c.Method == "Env" && FirstStringArg(c.Invocation) == key))
            return EditResult.Fail(text, $"'{app}' already sets {key}");

        var anchor = decl.Chain.LastOrDefault(c => c.Method is "Env" or "EnvFile")
            ?? decl.Chain.LastOrDefault(c => c.Method == "Http")
            ?? decl.Chain[^1];
        var line = $".Env({Literal(key)}, {valueCode})";
        return Apply(text, InsertionPointAfter(text, anchor.Invocation), NewLine(text) + ChainIndent(text, decl) + line);
    }

    /// <summary>Set, change or (with a null <paramref name="pin"/>) remove the <c>pin:</c> on an app's endpoint.</summary>
    public static EditResult SetPin(string text, string app, string endpoint, string? pin)
    {
        if (Find(text, app) is not { } decl) return EditResult.Fail(text, $"no editable app named '{app}'");
        var http = decl.Chain.FirstOrDefault(c => c.Method == "Http" && (FirstStringArg(c.Invocation) ?? "http") == endpoint);
        if (http is null) return EditResult.Fail(text, $"'{app}' has no endpoint named '{endpoint}'");

        var args = http.Invocation.ArgumentList;
        var existing = args.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "pin");

        if (existing is not null)
        {
            if (pin is null)
            {
                // Remove the argument with the separator before it, so "Http("http", pin: "x")" becomes "Http("http")".
                var index = args.Arguments.IndexOf(existing);
                var start = index > 0 ? args.Arguments.GetSeparator(index - 1).SpanStart : existing.SpanStart;
                var end = index == 0 && args.Arguments.Count > 1 ? args.Arguments.GetSeparator(0).Span.End : existing.Span.End;
                return Apply(text, new TextSpan(start, end - start), "");
            }
            return Apply(text, existing.Expression.Span, Literal(pin));
        }

        if (pin is null) return new EditResult(true, text);
        var prefix = args.Arguments.Count == 0 ? $"{Literal(endpoint)}, " : ", ";
        return Apply(text, args.CloseParenToken.SpanStart, $"{prefix}pin: {Literal(pin)}");
    }

    /// <summary>Add <c>var variable = repo.Import&lt;Type&gt;("name");</c> after the last import, or after
    /// <c>Repo.Describe</c>.</summary>
    public static EditResult AddImport(string text, string name, string type, string variable)
    {
        var root = Parse(text);
        var statements = root.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement).ToList();

        var describe = statements.FirstOrDefault(s => s.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Describe" }));
        if (describe is null) return EditResult.Fail(text, "no Repo.Describe(...) to add the import after");

        var repoVariable = (describe as LocalDeclarationStatementSyntax)?.Declaration.Variables[0].Identifier.Text ?? "repo";
        var imports = statements.Where(s => s.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Any(i => i.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "Import" } })).ToList();

        if (imports.Any(s => s.DescendantNodes().OfType<LiteralExpressionSyntax>().Any(l => l.Token.ValueText == name)))
            return EditResult.Fail(text, $"the definition already imports '{name}'");
        if (statements.OfType<LocalDeclarationStatementSyntax>().Any(s => s.Declaration.Variables.Any(v => v.Identifier.Text == variable)))
            return EditResult.Fail(text, $"a variable named '{variable}' already exists");

        var after = imports.LastOrDefault() ?? describe;
        var nl = NewLine(text);
        var statement = $"var {variable} = {repoVariable}.Import<{type}>({Literal(name)});";
        // Directly under the last import, or as its own paragraph under Describe.
        var insert = imports.Count > 0 ? nl + statement : nl + nl + statement;
        return Apply(text, InsertionPointAfter(text, after), insert);
    }

    /// <summary>Turn a trellis's <c>Repo("path")</c> into <c>UseRepo("id")</c>, so the repo comes from its entry in
    /// the Repos list. The variable it's assigned to, and everything else on the line, stays.</summary>
    public static EditResult RepoToUseRepo(string text, string repoPath, string id)
    {
        var call = Parse(text).DescendantNodes().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Repo" } && FirstStringArg(i) == repoPath);
        if (call is null) return EditResult.Fail(text, $"no trellis.Repo(\"{repoPath}\") to change");
        var name = ((MemberAccessExpressionSyntax)call.Expression).Name;
        return Apply(text, TextSpan.FromBounds(name.SpanStart, call.ArgumentList.Span.End), $"UseRepo({Literal(id)})");
    }

    /// <summary>Change which Repos-list entry a trellis's <c>UseRepo("id")</c> names.</summary>
    public static EditResult SetUseRepo(string text, string from, string to)
    {
        var call = Parse(text).DescendantNodes().OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "UseRepo" } && FirstStringArg(i) == from);
        if (call is null) return EditResult.Fail(text, $"no trellis.UseRepo(\"{from}\") to change");
        return Apply(text, call.ArgumentList.Arguments[0].Expression.Span, Literal(to));
    }

    /// <summary>Where to insert a new line after <paramref name="node"/>: at the end of its line when the code carries
    /// on below, so a trailing comment stays with the line it describes; straight after the node when more code
    /// follows on the same line (a one-line chain ending in <c>;</c>).</summary>
    static int InsertionPointAfter(string text, SyntaxNode node)
    {
        var source = SourceText.From(text);
        var nodeLine = source.Lines.GetLineFromPosition(node.Span.End);
        var next = node.GetLastToken().GetNextToken();
        var continuesOnSameLine = next.RawKind != 0 && source.Lines.GetLineFromPosition(next.SpanStart).LineNumber == nodeLine.LineNumber;
        return continuesOnSameLine ? node.Span.End : nodeLine.End;
    }

    sealed record ChainCall(string Method, InvocationExpressionSyntax Invocation);

    sealed record Declaration(
        string? Name, string Method, string? Variable, bool Editable,
        InvocationExpressionSyntax Create, IReadOnlyList<ChainCall> Chain, StatementSyntax? Statement);

    static CompilationUnitSyntax Parse(string text)
        => (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, ParseOptions).GetRoot();

    static Declaration? Find(string text, string app)
        => Declarations(Parse(text)).FirstOrDefault(d => d.Editable && d.Name == app);

    static IEnumerable<Declaration> Declarations(CompilationUnitSyntax root)
    {
        foreach (var create in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (create.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var method } || !ResourceMethods.Contains(method))
                continue;

            var chain = new List<ChainCall> { new(method, create) };
            ExpressionSyntax top = create;
            while (top.Parent is MemberAccessExpressionSyntax access && access.Parent is InvocationExpressionSyntax next)
            {
                chain.Add(new ChainCall(access.Name.Identifier.Text, next));
                top = next;
            }

            var name = FirstStringArg(create);
            var statement = top.Parent switch
            {
                EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax local } } } => (StatementSyntax)local,
                ExpressionStatementSyntax expression => expression,
                _ => null,
            };
            var variable = (statement as LocalDeclarationStatementSyntax)?.Declaration.Variables[0].Identifier.Text;
            // Only a top-level statement with a literal name is safe to edit: inside a loop or lambda one edit
            // would apply to many apps, and a computed name can't be matched.
            var editable = name is not null && statement?.Parent is GlobalStatementSyntax;

            yield return new Declaration(name, method, variable, editable, create, chain, statement);
        }
    }

    static string? FirstStringArg(InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon is null) is { Expression: LiteralExpressionSyntax lit }
           && lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;

    /// <summary>The indent used by the chain's continuation lines, or the statement's indent plus four spaces when
    /// the whole chain is on one line.</summary>
    static string ChainIndent(string text, Declaration decl)
    {
        var source = SourceText.From(text);
        foreach (var call in decl.Chain.Skip(1))
        {
            var dot = ((MemberAccessExpressionSyntax)call.Invocation.Expression).OperatorToken;
            var line = source.Lines.GetLineFromPosition(dot.SpanStart);
            var leading = text[line.Start..dot.SpanStart];
            if (leading.Trim().Length == 0) return leading;
        }
        var statementLine = source.Lines.GetLineFromPosition(decl.Statement!.SpanStart);
        var indent = text[statementLine.Start..decl.Statement.SpanStart];
        return indent + "    ";
    }

    static EditResult Apply(string text, int position, string insert) => Apply(text, new TextSpan(position, 0), insert);

    static EditResult Apply(string text, TextSpan span, string replacement)
    {
        var updated = SourceText.From(text).WithChanges(new TextChange(span, replacement)).ToString();
        var errors = CSharpSyntaxTree.ParseText(updated, ParseOptions)
            .GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return errors.Count == 0
            ? new EditResult(true, updated)
            : EditResult.Fail(text, "the edit would not compile: " + errors[0].GetMessage());
    }

    static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
