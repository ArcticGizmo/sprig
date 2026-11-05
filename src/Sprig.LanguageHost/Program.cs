using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sprig.LanguageHost;

// One request per stdin line, one response per stdout line. Anything else on stdout would corrupt the protocol,
// so diagnostics about the host itself go to stderr.
Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

var workspace = new DefinitionWorkspace();
var stdout = Console.Out;

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    if (line.Length == 0) continue;
    var clock = Stopwatch.StartNew();
    Response response;
    Request? request = null;
    try
    {
        request = JsonSerializer.Deserialize(line, ProtocolJson.Default.Request)!;
        response = request.Op switch
        {
            "complete" => await workspace.CompleteAsync(request.Id, request.Text ?? "", request.Position),
            "describe" => new Response(request.Id, 0, Text: await workspace.DescribeAsync(request.Position)),
            "hover" => new Response(request.Id, 0, Hover: await workspace.HoverAsync(request.Text ?? "", request.Position)),
            "format" => new Response(request.Id, 0, Text: await workspace.FormatAsync(request.Text ?? "")),
            "diagnostics" => new Response(request.Id, 0, Diagnostics: await workspace.DiagnoseAsync(request.Text ?? "")),
            "stats" => Stats(request.Id),
            _ => new Response(request.Id, 0, Error: $"unknown op '{request.Op}'"),
        };
    }
    catch (Exception ex)
    {
        response = new Response(request?.Id ?? -1, 0, Error: ex.Message);
    }

    response = response with { Ms = clock.Elapsed.TotalMilliseconds };
    await stdout.WriteLineAsync(JsonSerializer.Serialize(response, ProtocolJson.Default.Response));
    await stdout.FlushAsync();
}

static Response Stats(int id)
{
    using var self = Process.GetCurrentProcess();
    return new Response(id, 0,
        WorkingSetMb: Math.Round(self.WorkingSet64 / 1048576.0, 1),
        ManagedHeapMb: Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1));
}
