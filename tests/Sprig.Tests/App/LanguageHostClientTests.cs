using Sprig.App.Authoring;
using Sprig.App.ViewModels;

namespace Sprig.Tests.App;

/// <summary>Drives the real sprig-langhost process, so these take a second or two.</summary>
[Trait("Category", "Integration")]
public sealed class LanguageHostClientTests : IDisposable
{
    readonly LanguageHostClient _client = new();

    public void Dispose() => _client.Dispose();

    static readonly string Sample = EditorSpikeViewModel.SampleText;

    [Fact]
    public async Task Completion_after_a_dot_lists_the_sdk_members()
    {
        var text = Sample + "\ncatalog.";

        var response = await _client.CompleteAsync(text, text.Length);

        var labels = response.Items!.Select(i => i.Label).ToHashSet();
        Assert.Superset(new HashSet<string> { "Env", "EnvFile", "Http", "Port", "Url", "WaitFor" }, labels);
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
