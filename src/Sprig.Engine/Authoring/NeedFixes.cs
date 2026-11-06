using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Sprig.Engine.Authoring;

/// <summary>One way to fill a need, as the orchestration file would read after it.</summary>
/// <param name="Label">What it does, in a few words.</param>
/// <param name="Why">When it's the right choice.</param>
/// <param name="Text">The whole file with the change made; the file as it was when it can't be placed.</param>
/// <param name="Lines">The lines it adds, to show or to paste by hand.</param>
/// <param name="Error">Why it couldn't be placed in this file, in which case only <see cref="Lines"/> is any use.</param>
/// <param name="FillIn">It leaves a value for the user to fill in.</param>
public sealed record NeedFix(string Label, string Why, string Text, IReadOnlyList<string> Lines, string? Error = null, bool FillIn = false)
{
    public bool Placed => Error is null;
}

/// <summary>A need nothing in the orchestration fills, or that several repos offer.</summary>
/// <param name="Type">The value type the repo imports it as: <c>url</c>, <c>connectionstring</c>, ...</param>
/// <param name="Offers">The orchestration's repos that export it, by the path or ID the orchestration names them with.</param>
public sealed record UnfilledNeed(string Import, string Type, IReadOnlyList<string> Offers);

/// <summary>
/// The lines that would fill a need in an orchestration, written into its C# where they belong: a binding after the
/// orchestration's other bindings (or its repo lines), with infra from the presets declared just before. Repos are named
/// by the variables the file already gives them. Everything is found from the syntax alone, so it works on a file that doesn't compile
/// yet, and the rest of the file is left exactly as written.
/// </summary>
public static partial class NeedFixes
{
    sealed record Preset(string Method, string Name, Func<string, string> Value, Regex Fits);

    static readonly Preset[] Presets =
    [
        new("Mailpit", "mail", v => $"{v}.SmtpUrl", MailRule()),
        new("Postgres", "pg", v => v, DatabaseRule()),
        new("Redis", "cache", v => $"{v}.Url", CacheRule()),
        new("RabbitMq", "bus", v => $"{v}.Url", BusRule()),
    ];

    [GeneratedRegex("smtp|mail", RegexOptions.IgnoreCase)] private static partial Regex MailRule();
    [GeneratedRegex("db|database|postgres|pg|sql", RegexOptions.IgnoreCase)] private static partial Regex DatabaseRule();
    [GeneratedRegex("redis|cache", RegexOptions.IgnoreCase)] private static partial Regex CacheRule();
    [GeneratedRegex("amqp|rabbit|bus|queue", RegexOptions.IgnoreCase)] private static partial Regex BusRule();

    /// <summary>Every fix worth offering for <paramref name="need"/>, the likeliest first.</summary>
    public static IReadOnlyList<NeedFix> For(string text, UnfilledNeed need)
    {
        var fixes = new List<NeedFix>();
        if (need.Offers.Count > 1)
            foreach (var repo in need.Offers)
                fixes.Add(BindExport(text, need.Import, repo));

        foreach (var preset in Presets.Where(p => p.Fits.IsMatch(need.Import) || (p.Method == "Postgres" && need.Type == "connectionstring")))
            fixes.Add(AddInfra(text, need, preset));

        fixes.Add(Bind(text, need.Import, "\"\"", "Set it to a value", "When it's something outside the orchestration, or a fixed setting. The code opens so you can fill it in.")
            with { FillIn = true });
        return fixes;
    }

    static NeedFix BindExport(string text, string import, string repo)
    {
        var (named, variable, error) = NameRepo(text, repo);
        if (error is not null) return Unplaced($"Use {repo}'s", text, [$"orchestration.Bind({Literal(import)}, <{repo}>.Export({Literal(import)}));"], error);
        return Bind(named, import, $"{variable}.Export({Literal(import)})", $"Use {repo}'s",
            $"{repo} is one of the repos that offer {import}; this picks it.");
    }

    static NeedFix AddInfra(string text, UnfilledNeed need, Preset preset)
    {
        var root = Parse(text);
        var orchestration = OrchestrationVariable(root);
        var existing = Statements(root).OfType<LocalDeclarationStatementSyntax>()
            .FirstOrDefault(s => s.Declaration.Variables[0].Initializer?.Value is InvocationExpressionSyntax
                { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var m, Expression: IdentifierNameSyntax { Identifier.Text: var on } } }
                && m == preset.Method && on == orchestration);
        var label = $"Add {preset.Method} to the orchestration";
        var why = $"One {preset.Method} per workspace, run by the orchestration for every repo that needs it.";
        if (existing is not null)
        {
            var v = existing.Declaration.Variables[0].Identifier.Text;
            return Bind(text, need.Import, Value(preset, v, need.Import), $"Use the orchestration's {v}", $"The orchestration already runs a {preset.Method} as {v}.");
        }

        var variable = Unused(root, preset.Name);
        var declare = $"var {variable} = {orchestration}.{preset.Method}({Literal(preset.Name == variable ? preset.Name : variable)});";
        return Bind(text, need.Import, Value(preset, variable, need.Import), label, why, declare);
    }

    /// <summary>Postgres gives one database per repo that asks, named after what it's for.</summary>
    static string Value(Preset preset, string variable, string import)
        => preset.Method == "Postgres"
            ? $"{variable}.Database({Literal(Regex.Replace(Regex.Replace(import, "(?i)[-_.]?(db|database|conn(ection)?(string)?)$", ""), "[^A-Za-z0-9]+", "_").Trim('_') is { Length: > 0 } db ? db : "app")})"
            : preset.Value(variable);

    /// <summary><c>orchestration.Bind(import, value);</c>, with <paramref name="before"/> first when given, placed after the
    /// orchestration's last binding, else its last repo, else <c>Orchestration.Describe</c>.</summary>
    static NeedFix Bind(string text, string import, string valueCode, string label, string why, string? before = null)
    {
        var root = Parse(text);
        var orchestration = OrchestrationVariable(root);
        var bind = $"{orchestration}.Bind({Literal(import)}, {valueCode});";
        IReadOnlyList<string> lines = before is null ? [bind] : [before, bind];

        var statements = Statements(root).ToList();
        var describe = statements.FirstOrDefault(s => Calls(s, orchestration: null, "Describe"));
        if (describe is null) return Unplaced(label, text, lines, "there's no Orchestration.Describe(...) to add it after");
        if (statements.Any(s => Calls(s, orchestration, "Bind") && s.DescendantNodes().OfType<LiteralExpressionSyntax>().Any(l => l.Token.ValueText == import)))
            return Unplaced(label, text, lines, $"the orchestration already binds '{import}'");

        var lastBind = statements.LastOrDefault(s => Calls(s, orchestration, "Bind"));
        var lastRepo = statements.LastOrDefault(s => Calls(s, orchestration, "Repo") || Calls(s, orchestration, "UseRepo"));
        var after = lastBind ?? lastRepo ?? describe;
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        // Beside other bindings it joins their paragraph; otherwise it starts its own.
        var insert = (lastBind is not null && before is null ? nl : nl + nl) + string.Join(nl, lines);
        var updated = SourceText.From(text).WithChanges(new TextChange(new TextSpan(LineEnd(text, after), 0), insert)).ToString();
        return Compiles(updated) ? new NeedFix(label, why, updated, lines) : Unplaced(label, text, lines, "the change wouldn't compile here");
    }

    /// <summary>The file with <paramref name="repo"/>'s line given a variable when it has none, and that variable.</summary>
    static (string Text, string Variable, string? Error) NameRepo(string text, string repo)
    {
        var root = Parse(text);
        var orchestration = OrchestrationVariable(root);
        var call = root.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i =>
            i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Repo" or "UseRepo", Expression: IdentifierNameSyntax { Identifier.Text: var on } }
            && on == orchestration && FirstString(i) == repo);
        if (call is null) return (text, "", $"there's no {orchestration}.Repo or UseRepo line for '{repo}'");

        if (call.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator })
            return (text, declarator.Identifier.Text, null);
        if (call.Parent is not ExpressionStatementSyntax statement || statement.Parent is not GlobalStatementSyntax)
            return (text, "", $"{repo}'s line isn't one sprig can give a variable to");

        var variable = Unused(root, Camel(Path.GetFileName(repo.TrimEnd('/', '\\'))));
        var named = SourceText.From(text).WithChanges(new TextChange(new TextSpan(statement.SpanStart, 0), $"var {variable} = ")).ToString();
        return (named, variable, null);
    }

    static NeedFix Unplaced(string label, string text, IReadOnlyList<string> lines, string error)
        => new(label, "Couldn't be placed in this file, so paste it where it belongs.", text, lines, error);

    static string OrchestrationVariable(CompilationUnitSyntax root)
        => Statements(root).OfType<LocalDeclarationStatementSyntax>()
            .FirstOrDefault(s => s.Declaration.Variables[0].Initializer?.Value is InvocationExpressionSyntax
                { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Orchestration" }, Name.Identifier.Text: "Describe" } })
            ?.Declaration.Variables[0].Identifier.Text ?? "orchestration";

    static IEnumerable<StatementSyntax> Statements(CompilationUnitSyntax root)
        => root.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement);

    /// <summary>Whether <paramref name="statement"/> calls <paramref name="method"/> on <paramref name="orchestration"/>
    /// (any receiver when null) as its first call, as in <c>orchestration.Bind(...)</c> or <c>var x = orchestration.Repo(...)</c>.</summary>
    static bool Calls(StatementSyntax statement, string? orchestration, string method)
        => statement.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i =>
            i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: var m, Expression: var on } && m == method
            && (orchestration is null || on is IdentifierNameSyntax { Identifier.Text: var name } && name == orchestration));

    static string? FirstString(InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
           && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;

    static string Unused(CompilationUnitSyntax root, string wanted)
    {
        var taken = root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(v => v.Identifier.Text).ToHashSet();
        taken.Add("args");
        if (!taken.Contains(wanted)) return wanted;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{wanted}{n}")) return $"{wanted}{n}";
    }

    static string Camel(string name)
    {
        var words = Regex.Split(name, "[^A-Za-z0-9]+").Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) return "repo";
        var camel = words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return char.IsDigit(camel[0]) ? "repo" + camel : camel;
    }

    /// <summary>The end of the line <paramref name="node"/> ends on, so a comment after it stays with it.</summary>
    static int LineEnd(string text, SyntaxNode node) => SourceText.From(text).Lines.GetLineFromPosition(node.Span.End).End;

    static bool Compiles(string text)
        => !CSharpSyntaxTree.ParseText(text, DefinitionEditor.ParseOptions).GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);

    static CompilationUnitSyntax Parse(string text) => (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, DefinitionEditor.ParseOptions).GetRoot();

    static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
}
