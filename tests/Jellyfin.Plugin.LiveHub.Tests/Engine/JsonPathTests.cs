using System;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.LiveHub.Engine;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Engine;

public class JsonPathTests
{
    private static readonly JsonNode Doc = JsonNode.Parse("""
    {
      "data": {
        "items": [
          { "id": 1, "name": "Inter - Milan", "type": "live", "viewers": 1500, "tags": ["a","b"], "teams": [{"name":"Inter"},{"name":"Milan"}] },
          { "id": 2, "name": "Roma - Lazio", "type": "upcoming", "viewers": 50, "url": null },
          { "id": 3, "name": "Juve - Napoli", "type": "live", "viewers": 300, "meta": { "deep": { "name": "hidden" } } }
        ],
        "next": "c2",
        "weird key": "x"
      }
    }
    """)!;

    private static string?[] Texts(string path) => JsonPath.Parse(path).Select(Doc).Select(JsonPath.ToText).ToArray();

    [Theory]
    [InlineData("$.data.next", "c2")]
    [InlineData("$['data']['next']", "c2")]
    [InlineData("$.data['weird key']", "x")]
    [InlineData("$.data.items[0].name", "Inter - Milan")]
    [InlineData("$.data.items[-1].id", "3")]
    [InlineData("$.data.items[0].teams[1].name", "Milan")]
    [InlineData("$.data.items.length", "3")]
    public void SelectsSingleValues(string path, string expected)
    {
        Assert.Equal(expected, Texts(path).Single());
    }

    [Fact]
    public void WildcardsUnionsAndSlices()
    {
        Assert.Equal(new[] { "1", "2", "3" }, Texts("$.data.items[*].id"));
        Assert.Equal(new[] { "1", "3" }, Texts("$.data.items[0,2].id"));
        Assert.Equal(new[] { "2", "3" }, Texts("$.data.items[1:].id"));
        Assert.Equal(new[] { "3", "2", "1" }, Texts("$.data.items[::-1].id"));
        Assert.Equal(new[] { "a", "b" }, Texts("$.data.items[0].tags[*]"));
    }

    [Fact]
    public void RecursiveDescent()
    {
        var names = Texts("$..name");
        Assert.Contains("hidden", names);
        Assert.Contains("Inter", names);
        Assert.Equal(6, names.Length);
    }

    [Fact]
    public void Filters()
    {
        Assert.Equal(new[] { "1", "3" }, Texts("$.data.items[?(@.type == 'live')].id"));
        Assert.Equal(new[] { "1" }, Texts("$.data.items[?(@.type == 'live' && @.viewers > 1000)].id"));
        Assert.Equal(new[] { "1", "2" }, Texts("$.data.items[?(@.viewers >= 1000 || @.type != 'live')].id"));
        Assert.Equal(new[] { "3" }, Texts("$.data.items[?(@.meta)].id"));
        Assert.Equal(new[] { "2", "3" }, Texts("$.data.items[?(!@.tags)].id"));
        Assert.Equal(new[] { "2" }, Texts("$.data.items[?(@.name =~ /roma/i)].id"));
    }

    [Fact]
    public void RelativePathUsesCurrentNode()
    {
        var item = Doc["data"]!["items"]![0];
        Assert.Equal("Inter", JsonPath.ToText(JsonPath.Parse("@.teams[0].name").SelectFirst(Doc, item)));
    }

    [Theory]
    [InlineData("data.items")]
    [InlineData("$.data.items[")]
    [InlineData("$.data.items[?(@.a == )]")]
    [InlineData("$.data.items[?(@.a =~ /[/)]")]
    public void InvalidExpressionsAreRejected(string path)
    {
        Assert.False(JsonPath.TryParse(path, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void MissingPathsReturnEmpty()
    {
        Assert.Empty(Texts("$.nope.deeper"));
        Assert.Null(JsonPath.Parse("$.data.items[10]").SelectFirst(Doc));
    }
}
