using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Json.Schema;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Definitions;

public class DefinitionTests
{
    private static readonly JsonSchema Schema = JsonSchema.FromText(Fixtures.Read("provider.schema.json"));

    public static TheoryData<string> AllExampleFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Fixtures.DefinitionFiles().Concat(Fixtures.TemplateFiles()))
        {
            data.Add(Path.GetRelativePath(Fixtures.Dir, f));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllExampleFiles))]
    public void ExamplesValidateAgainstTheJsonSchema(string relative)
    {
        var json = JsonNode.Parse(Fixtures.Read(relative));
        var result = Schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, string.Join("\n", result.Details.Where(d => d.HasErrors).SelectMany(d => d.Errors!.Select(e => d.InstanceLocation + ": " + e.Value))));
    }

    [Theory]
    [MemberData(nameof(AllExampleFiles))]
    public void ExamplesParseValidateAndRoundTrip(string relative)
    {
        var text = Fixtures.Read(relative);
        var def = DefinitionSerializer.TryParse(text, out var error);
        Assert.True(def is not null, error);
        var result = DefinitionValidator.Validate(def!);
        Assert.True(result.IsValid, string.Join("\n", result.Errors));

        // The C# model serializes back to a schema-valid document with the same meaning.
        var back = DefinitionSerializer.Serialize(def!);
        var again = DefinitionSerializer.TryParse(back, out _)!;
        Assert.Equal(DefinitionSerializer.Serialize(again), back);
        var schemaResult = Schema.Evaluate(JsonNode.Parse(back));
        Assert.True(schemaResult.IsValid);
    }

    [Fact]
    public void UnknownPropertiesAreRejected()
    {
        var def = DefinitionSerializer.TryParse("""{ "schemaVersion": 1, "id": "abc", "name": "x", "kind": "direct", "direct": { "url": "https://a.b/c.m3u8" }, "evil": true }""", out var error);
        Assert.Null(def);
        Assert.Contains("evil", error);
    }

    [Fact]
    public void SemanticErrorsCarryJsonPaths()
    {
        var def = DefinitionSerializer.TryParse("""
        {
          "schemaVersion": 1, "id": "Bad Id", "name": "", "kind": "rest",
          "connection": { "baseUrl": "ftp://x" },
          "events": [ {
            "request": { "url": "{baseUrl}/e" },
            "root": "$.data[",
            "fields": { "title": { "path": "$.t", "transforms": [ { "op": "eval" }, { "op": "replace", "pattern": "([" } ] }, "bogus": "$.x" },
            "pagination": { "type": "cursor" }
          } ]
        }
        """, out var error);
        Assert.NotNull(def);
        var result = DefinitionValidator.Validate(def!);
        var paths = result.Errors.Select(e => e.Path).ToList();
        Assert.Contains("id", paths);
        Assert.Contains("name", paths);
        Assert.Contains("connection.baseUrl", paths);
        Assert.Contains("events[0].root", paths);
        Assert.Contains("events[0].fields.title.transforms[0].op", paths);
        Assert.Contains("events[0].fields.title.transforms[1].pattern", paths);
        Assert.Contains("events[0].fields.bogus", paths);
        Assert.Contains("events[0].pagination.cursorPath", paths);
        Assert.Contains("events[0].fields.start", paths);
    }

    [Fact]
    public void NonHttpSchemesAreRejected()
    {
        var def = DefinitionSerializer.TryParse("""{ "schemaVersion": 1, "id": "abc", "name": "x", "kind": "direct", "direct": { "url": "file:///etc/passwd" } }""", out _);
        var result = DefinitionValidator.Validate(def!);
        Assert.Contains(result.Errors, e => e.Path == "direct.url");
    }

    [Fact]
    public void InlineCredentialsProduceAWarning()
    {
        var def = DefinitionSerializer.TryParse("""
        { "schemaVersion": 1, "id": "abc", "name": "x", "kind": "rest",
          "connection": { "baseUrl": "https://a.b", "auth": { "type": "bearer", "value": "plain-token-123" } },
          "events": [ { "request": { "url": "{baseUrl}/e" }, "root": "$[*]", "fields": { "id": "$.id", "title": "$.t", "start": "$.s" } } ] }
        """, out _);
        var result = DefinitionValidator.Validate(def!);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Path == "connection.auth.value");
    }
}
