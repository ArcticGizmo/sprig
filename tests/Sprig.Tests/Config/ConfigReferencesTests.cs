using Sprig.Core.Config;

namespace Sprig.Tests.Config;

public class ConfigReferencesTests
{
    [Fact]
    public void Required_stack_vars_excludes_ports_workspace_provides()
    {
        var config = SprigConfigLoader.Parse("""
            {
              "schema": 1, "name": "vue",
              "ports": [ { "name": "frontend" } ],
              "env": [ { "file": ".env", "set": {
                  "PORT": "${sprig.ports.frontend}",
                  "VITE_API_URL": "${sprig.apiUrl}",
                  "NAME": "app--${sprig.workspace}"
              } } ],
              "provides": { "self": "http://x:${sprig.ports.frontend}" }
            }
            """);

        var vars = ConfigReferences.RequiredStackVars(config);

        Assert.Equal(["apiUrl"], vars);              // only the bare var
        Assert.Contains("ports.frontend", ConfigReferences.ReferencedPaths(config));
        Assert.Contains("workspace", ConfigReferences.ReferencedPaths(config));
    }

    [Fact]
    public void Provides_references_are_not_stack_vars()
    {
        var config = SprigConfigLoader.Parse("""
            { "schema":1, "name":"web",
              "env":[ { "file":".env", "set": { "U": "${sprig.provides.api.baseUrl}" } } ] }
            """);
        Assert.Empty(ConfigReferences.RequiredStackVars(config));
    }

    [Fact]
    public void Scans_compose_templates_too()
    {
        var config = SprigConfigLoader.Parse("""
            { "schema":1, "name":"api",
              "compose": { "file":"docker-compose.yml", "overrides":[
                  { "path":["services","x","image"], "template":"${sprig.imageTag}" } ] } }
            """);
        Assert.Equal(["imageTag"], ConfigReferences.RequiredStackVars(config));
    }

    [Fact]
    public void No_refs_yields_empty()
    {
        var config = SprigConfigLoader.Parse("""{ "schema":1, "name":"x" }""");
        Assert.Empty(ConfigReferences.RequiredStackVars(config));
    }
}
