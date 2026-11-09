using Sprig.App.Authoring;
using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>Drives the real sprig-langhost process, so these take a second or two.</summary>
[Trait("Category", "Integration")]
public sealed class LanguageHostClientTests : IDisposable
{
    readonly LanguageHostClient _client = new();

    public void Dispose() => _client.Dispose();

    static readonly string Sample = Sprig.App.Authoring.SampleDefinition.Text;

    [Fact]
    public async Task Completion_after_a_dot_lists_the_sdk_members()
    {
        var text = Sample + "\ncatalog.";

        var response = await _client.CompleteAsync(text, text.Length);

        var labels = response.Items!.Select(i => i.Label).ToHashSet();
        Assert.Superset(new HashSet<string> { "AppSettings", "EnvFile", "LaunchSettings", "Http", "Port", "Url", "WaitFor" }, labels);
        Assert.DoesNotContain("Env", labels);
    }

    [Fact]
    public async Task Typing_a_word_offers_the_variables_in_scope_from_where_the_word_starts_with_docs_on_request()
    {
        var text = Sample.Replace("return repo.Emit(args);", "cat");
        var caret = text.LastIndexOf("cat", StringComparison.Ordinal) + 3;

        var response = await _client.CompleteAsync(text, caret);

        Assert.Equal(caret - 3, response.SpanStart);
        Assert.False(response.Suggestion);
        var catalog = response.Items!.Select((item, index) => (item, index)).Single(x => x.item.Label == "catalog");
        Assert.Equal("Local", catalog.item.Kind);
        Assert.NotNull(catalog.item.Sort);
        var described = await _client.DescribeAsync(catalog.index);
        Assert.Contains("AppResource", described.Text);
    }

    [Fact]
    public async Task Hovering_an_sdk_method_shows_its_signature_and_doc_comment()
    {
        var at = Sample.IndexOf(".Http(", StringComparison.Ordinal) + 2;

        var hover = (await _client.HoverAsync(Sample, at)).Hover!;

        Assert.Contains("Http(", hover.Signature);
        Assert.Contains("An HTTP endpoint on a port sprig allocates", hover.Docs);
        Assert.True(hover.Start <= at && at <= hover.End);
        Assert.Null((await _client.HoverAsync(Sample, 0)).Hover);
    }

    [Fact]
    public async Task Formatting_lays_the_file_out_and_leaves_the_package_line_alone()
    {
        var messy = Sample.Replace("var orders = repo.DotNet", "var   orders=repo.DotNet");

        var formatted = (await _client.FormatAsync(messy)).Text!;

        Assert.Contains("var orders = repo.DotNet", formatted);
        Assert.StartsWith("#:package Sprig.Sdk@0.1.0-exp", formatted);
    }

    [Fact]
    public async Task The_sample_has_no_problems_and_a_typo_is_reported_on_its_line()
    {
        Assert.Empty((await _client.DiagnoseAsync(Sample)).Diagnostics!);

        var broken = Sample.Replace(".Http(\"http\")", ".Htp(\"http\")");
        var problem = Assert.Single((await _client.DiagnoseAsync(broken)).Diagnostics!.Take(1));

        Assert.Equal("CS1061", problem.Id);
        Assert.Equal(13, problem.Line);
    }

    [Fact]
    public async Task After_the_host_is_killed_requests_fail_cleanly_and_the_next_one_starts_a_new_host()
    {
        await _client.StatsAsync();
        var first = _client.ProcessId;
        var stopped = new TaskCompletionSource<int>();
        _client.Stopped += code => stopped.TrySetResult(code);

        _client.Kill();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var response = await _client.CompleteAsync(Sample + "\nrepo.", Sample.Length + 6);
        Assert.NotEmpty(response.Items!);
        Assert.NotEqual(first, _client.ProcessId);
    }
}
