using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;

namespace Shop.Diagnostics;

public sealed record ConfigValue(string Key, string? Value, string Source, string? Target, bool? Reachable);

/// <summary>
/// <c>/health</c> and <c>/config</c> for every app in the testbed. <c>/config</c> lists each setting the app relies
/// on, the value it got, where that value came from, and whether anything answers at the address it names.
/// </summary>
public static class DiagnosticsEndpoints
{
    public static WebApplication MapDiagnostics(this WebApplication app, string name, params string[] keys)
    {
        var config = (IConfigurationRoot)app.Configuration;

        app.MapGet("/health", async () =>
        {
            var values = await ReportAsync(config, keys);
            var deps = values.Where(v => v.Target is not null).ToArray();
            return Results.Json(new
            {
                app = name,
                status = deps.All(d => d.Reachable == true) ? "ok" : "degraded",
                dependencies = deps.Select(d => new { key = d.Key, target = d.Target, reachable = d.Reachable }),
            });
        });

        app.MapGet("/config.json", async () => Results.Json(new { app = name, values = await ReportAsync(config, keys) }));

        app.MapGet("/config", async () => Results.Content(Html(name, await ReportAsync(config, keys)), "text/html"));

        return app;
    }

    public static async Task<IReadOnlyList<ConfigValue>> ReportAsync(IConfigurationRoot config, IEnumerable<string> keys)
    {
        var rows = new List<ConfigValue>();
        foreach (var key in keys)
        {
            var value = config[key];
            var target = Probe.Find(value);
            bool? reachable = target is null ? null : await Probe.ReachableAsync(target, TimeSpan.FromMilliseconds(600));
            rows.Add(new ConfigValue(key, value is null ? null : Probe.Mask(value), SourceOf(config, key), target?.ToString(), reachable));
        }
        return rows;
    }

    /// <summary>The provider that supplied the winning value: the last one, in registration order, that has the key.</summary>
    static string SourceOf(IConfigurationRoot config, string key)
    {
        foreach (var provider in config.Providers.Reverse())
        {
            if (!provider.TryGet(key, out _)) continue;
            return provider switch
            {
                EnvironmentVariablesConfigurationProvider => "environment variable",
                CommandLineConfigurationProvider => "command line",
                JsonConfigurationProvider json => json.Source.Path ?? "json file",
                _ => provider.GetType().Name,
            };
        }
        return "not set";
    }

    static string Html(string name, IReadOnlyList<ConfigValue> values)
    {
        var rows = new StringBuilder();
        foreach (var v in values)
        {
            var status = v.Reachable switch { true => "<td class=ok>reachable</td>", false => "<td class=bad>nothing answering</td>", _ => "<td class=na>–</td>" };
            rows.Append($"<tr><td><code>{E(v.Key)}</code></td><td><code>{E(v.Value ?? "(not set)")}</code></td><td>{E(v.Source)}</td>{status}</tr>");
        }
        return $$"""
            <!doctype html><meta charset=utf-8><title>{{E(name)}} config</title>
            <style>
              body { font: 14px system-ui, sans-serif; margin: 32px; background: #181820; color: #e1e1eb }
              h1 { font-size: 20px } table { border-collapse: collapse } td, th { padding: 6px 14px; border-bottom: 1px solid #2d2d3c; text-align: left }
              th { color: #8c8ca0; font-size: 12px; text-transform: uppercase; letter-spacing: .08em } code { font-size: 13px }
              .ok { color: #4ade80 } .bad { color: #f87171 } .na { color: #8c8ca0 }
            </style>
            <h1>{{E(name)}}</h1>
            <table><tr><th>Setting</th><th>Value</th><th>Came from</th><th>Address</th></tr>{{rows}}</table>
            """;
    }

    static string E(string s) => WebUtility.HtmlEncode(s);
}
