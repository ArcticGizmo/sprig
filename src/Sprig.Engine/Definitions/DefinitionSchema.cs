using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sprig.Engine.Definitions;

/// <summary>
/// The JSON Schema for the current schema, for IDEs to complete and check a definition outside sprig (with a
/// <c># yaml-language-server: $schema=…</c> line). Built from <see cref="DefinitionKeys"/>, so it names exactly the keys
/// the reader takes, with the editor's own descriptions. It can't check references; sprig does that when it reads.
/// </summary>
public static class DefinitionSchema
{
    public static string Repo() => Write(Root("repo", "A repo's sprig.yaml.", DefinitionKeys.Repo, ["schema", "repo"], new()
    {
        ["repo"] = Text(),
        ["copy-into-worktrees"] = Strings(),
        ["compose"] = Named(Object(DefinitionKeys.ComposeFile, new()
        {
            ["services"] = Named(Object(DefinitionKeys.ComposeService, new()
            {
                ["ports"] = Named(Port(DefinitionKeys.Port)),
                ["health"] = Text(),
                ["role"] = Choice(DefinitionReader.InfraRoles),
                ["values"] = Values(),
            })),
            ["set"] = Values(),
        })),
        ["containers"] = Named(Container()),
        ["inputs"] = Named(Object(DefinitionKeys.Input, new()
        {
            ["type"] = Choice(DefinitionReader.ValueTypes.Keys),
            ["default"] = Value(),
            ["role"] = Choice(DefinitionReader.Roles),
        })),
        ["apps"] = Named(Object(DefinitionKeys.App, new()
        {
            ["run"] = Choice(["dotnet", "node", "command"]),
            ["path"] = Text(),
            ["command"] = Text(),
            ["ports"] = Named(Port(DefinitionKeys.AppPort)),
            ["health"] = new JsonObject { ["type"] = "string", ["pattern"] = "^/" },
            ["ready-log"] = Text(),
            ["restart"] = Choice(["never", "on-failure", "always"]),
            ["max-restarts"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 },
            ["once"] = new JsonObject { ["type"] = "boolean" },
            ["retry"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
            ["wait-for"] = Strings(),
            ["links"] = Values(),
            ["role"] = Choice(["web", "api", "worker"]),
            ["values"] = Values(),
        }, required: ["run"])),
        ["files"] = Named(Object(DefinitionKeys.File, new()
        {
            ["read-by"] = Strings(),
            ["values"] = new JsonObject { ["$ref"] = "#/definitions/fileValues" },
            ["format"] = Choice(["dotenv", "json", "launch-settings"]),
            ["profile"] = Text(),
        }, required: ["read-by", "values"])),
        ["outputs"] = Named(Object(DefinitionKeys.Output, new()
        {
            ["type"] = Choice(DefinitionReader.ValueTypes.Keys),
            ["value"] = Value(),
        }, required: ["value"])),
        ["groups"] = Named(Strings()),
    }));

    public static string Orchestration() => Write(Root("orchestration", "An orchestration file.", DefinitionKeys.Orchestration, ["schema", "orchestration", "repos"], new()
    {
        ["orchestration"] = Text(),
        ["repos"] = Named(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = Properties(DefinitionKeys.RepoRef, new() { ["path"] = Text(), ["use"] = Text() }),
            ["oneOf"] = new JsonArray(new JsonObject { ["required"] = new JsonArray("path") }, new JsonObject { ["required"] = new JsonArray("use") }),
            ["additionalProperties"] = false,
        }),
        ["containers"] = Named(Container()),
        ["inputs"] = Values(),
        ["groups"] = Named(Strings()),
        ["links"] = Values(),
    }));

    /// <summary>Where the schema for <paramref name="kind"/> (<c>repo</c> or <c>orchestration</c>) is published, from
    /// docs/schema by the Pages workflow.</summary>
    public static string Url(string kind) => $"https://arcticgizmo.github.io/sprig/schema/v{DefinitionReader.CurrentSchema}/{kind}.json";

    /// <summary>The first line of a file sprig drafts, so an IDE with the YAML language server completes and checks it.</summary>
    public static string Comment(string kind) => $"# yaml-language-server: $schema={Url(kind)}\n";

    static JsonObject Root(string kind, string title, DefinitionKeys.Key[] keys, string[] required, Dictionary<string, JsonObject> values)
    {
        values["schema"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = DefinitionReader.CurrentSchema };
        var root = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["$id"] = Url(kind),
            ["title"] = title,
        };
        foreach (var (name, node) in Object(keys, values, required).ToArray()) root[name] = node!.DeepClone();
        // A JSON file's values nest as the file does; any other file's are flat.
        root["definitions"] = new JsonObject
        {
            ["fileValues"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = new JsonObject { ["anyOf"] = new JsonArray(Value(), new JsonObject { ["$ref"] = "#/definitions/fileValues" }) },
            },
        };
        return root;
    }

    static JsonObject Container() => Object(DefinitionKeys.Container, new()
    {
        ["preset"] = Choice(DefinitionReader.Presets.Keys),
        ["image"] = Text(),
        ["ports"] = Named(Port(DefinitionKeys.Port)),
        ["env"] = Values(),
        ["volumes"] = Named(Text()),
        ["health"] = Text(),
        ["role"] = Choice(DefinitionReader.InfraRoles),
        ["values"] = Values(),
    });

    /// <summary>Where on this machine a port may be (any, a port, a list of ports and ranges), or a map that says more.</summary>
    static JsonObject Port(DefinitionKeys.Key[] keys)
    {
        var host = new JsonObject
        {
            ["anyOf"] = new JsonArray(
                new JsonObject { ["const"] = "any" },
                PortNumber(),
                new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = PortNumber() }),
        };
        var values = new Dictionary<string, JsonObject>
        {
            ["host"] = host.DeepClone().AsObject(),
            ["scheme"] = Text(),
            ["container"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 65535 },
            ["health"] = new JsonObject { ["type"] = "string", ["pattern"] = "^/" },
            ["main"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 65535 },
        };
        return new JsonObject { ["anyOf"] = new JsonArray(host, Object(keys, values)) };
    }

    static JsonObject PortNumber() => new()
    {
        ["anyOf"] = new JsonArray(
            new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 65535 },
            new JsonObject { ["type"] = "string", ["pattern"] = @"^\d{1,5}(-\d{1,5})?$" }),
    };

    static JsonObject Object(DefinitionKeys.Key[] keys, Dictionary<string, JsonObject> values, string[]? required = null)
    {
        var obj = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = Properties(keys, values),
            ["additionalProperties"] = false,
        };
        if (required is { Length: > 0 }) obj["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]);
        return obj;
    }

    static JsonObject Properties(DefinitionKeys.Key[] keys, Dictionary<string, JsonObject> values)
    {
        var properties = new JsonObject();
        foreach (var key in keys)
        {
            var value = values.TryGetValue(key.Name, out var v) ? v : throw new InvalidOperationException($"the schema has no value for '{key.Name}'");
            value["description"] = key.Doc;
            properties[key.Name] = value;
        }
        return properties;
    }

    /// <summary>Names the author chooses (apps, files, ports…), each holding <paramref name="value"/>.</summary>
    static JsonObject Named(JsonObject value) => new() { ["type"] = "object", ["additionalProperties"] = value };

    static JsonObject Values() => Named(Value());

    static JsonObject Text() => new() { ["type"] = "string" };

    /// <summary>Read as written, so a number or true is as good as text.</summary>
    static JsonObject Value() => new() { ["type"] = new JsonArray("string", "number", "boolean", "null") };

    static JsonObject Strings() => new() { ["type"] = "array", ["items"] = Text() };

    static JsonObject Choice(IEnumerable<string> choices) => new() { ["enum"] = new JsonArray([.. choices.Select(c => (JsonNode)c)]) };

    static string Write(JsonObject schema)
        => schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
}
