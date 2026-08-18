using Sprig.Core.Config;

namespace Sprig.Tests.Config;

public class ConfigReferencesTests
{
    [Fact]
    public void Undeclared_references_flag_refs_that_are_not_provided_or_workspace()
    {
        var config = SprigConfigLoader.Parse("""
            {
              "schema": 1, "name": "vue",
              "modules": [ { "name": "app",
                "provides": [ { "capability": "frontend", "outputs": { "port": { "port": true } } } ],
                "env": [ { "file": ".env", "set": {
                    "PORT": "${sprig.frontend.port}",
                    "NAME": "app--${sprig.workspace}",
                    "OOPS": "${sprig.apiUrl}"
                } } ] } ]
            }
            """);

        // frontend.port (self-provided) and workspace are fine; apiUrl is undeclared.
        Assert.Equal(["apiUrl"], ConfigReferences.UndeclaredReferences(config));
        Assert.Contains("frontend.port", ConfigReferences.ReferencedPaths(config));
        Assert.Contains("workspace", ConfigReferences.ReferencedPaths(config));
    }

    [Fact]
    public void Scans_compose_templates_too()
    {
        var config = SprigConfigLoader.Parse("""
            { "schema":1, "name":"api",
              "compose": [ { "file":"docker-compose.yml", "overrides":[
                  { "path":["services","x","image"], "template":"${sprig.imageTag}" } ] } ] }
            """);
        Assert.Equal(["imageTag"], ConfigReferences.UndeclaredReferences(config));
    }

    [Fact]
    public void A_needed_capabilitys_output_is_accepted()
    {
        var config = SprigConfigLoader.Parse("""
            { "schema":1, "name":"api",
              "modules": [ { "name":"app",
                "needs": [ { "capability": "db" } ],
                "compose": [ { "file":"docker-compose.yml", "overrides":[
                    { "path":["services","x","image"], "template":"${sprig.db.image}" } ] } ] } ] }
            """);
        Assert.Empty(ConfigReferences.UndeclaredReferences(config));
    }

    [Fact]
    public void No_refs_yields_empty()
    {
        var config = SprigConfigLoader.Parse("""{ "schema":1, "name":"x" }""");
        Assert.Empty(ConfigReferences.UndeclaredReferences(config));
    }
}
