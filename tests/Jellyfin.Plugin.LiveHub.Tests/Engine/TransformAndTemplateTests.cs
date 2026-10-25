using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Engine;

public class TransformAndTemplateTests
{
    private static readonly ManualClock Clock = new(new DateTimeOffset(2026, 10, 25, 18, 30, 0, TimeSpan.Zero));

    private static TemplateContext Ctx()
    {
        var t = new TemplateContext(Clock, DateParser.FindZone("Europe/Rome")!);
        t["baseUrl"] = "https://api.example.com/v1";
        t["event.id"] = "42";
        return t;
    }

    private static string? Apply(string? input, params TransformSpec[] transforms)
        => TransformEngine.Apply(MappedValue.Of(input), transforms, Ctx()).Text;

    [Fact]
    public void BasicStringTransforms()
    {
        Assert.Equal("ita", Apply(" ITA ", new TransformSpec { Op = "trim" }, new TransformSpec { Op = "lowercase" }));
        Assert.Equal("ENG", Apply("eng", new TransformSpec { Op = "uppercase" }));
        Assert.Equal("it", Apply(null, new TransformSpec { Op = "default", Value = "it" }));
        Assert.Equal("x", Apply("x", new TransformSpec { Op = "default", Value = "it" }));
        Assert.Equal("[HD]", Apply("HD", new TransformSpec { Op = "prefix", Value = "[" }, new TransformSpec { Op = "suffix", Value = "]" }));
    }

    [Fact]
    public void MapTable()
    {
        var map = new TransformSpec { Op = "map", Table = new Dictionary<string, string?> { ["ENG"] = "en", ["ITA"] = "it" } };
        Assert.Equal("en", Apply("eng", map));
        Assert.Equal("fr", Apply("fr", map));
        map.Strict = true;
        Assert.Null(Apply("fr", map));
        map.Strict = false;
        map.Default = "und";
        Assert.Equal("und", Apply("fr", map));
    }

    [Fact]
    public void RegexReplaceAndExtract()
    {
        Assert.Equal("Inter vs Milan", Apply("Inter - Milan", new TransformSpec { Op = "replace", Pattern = "\\s+-\\s+", Replacement = " vs " }));
        Assert.Equal("1080", Apply("Stream 1080p HD", new TransformSpec { Op = "extract", Pattern = "(\\d{3,4})p" }));
        Assert.Equal("Milan", Apply("Inter - Milan", new TransformSpec { Op = "extract", Pattern = "(?<home>.+) - (?<away>.+)", Group = "away" }));
        Assert.Null(Apply("nothing", new TransformSpec { Op = "extract", Pattern = "(\\d+)" }));
    }

    [Fact]
    public void SplitJoinConcatTemplate()
    {
        Assert.Equal("en", Apply("it, en, es", new TransformSpec { Op = "split", Separator = ",", Index = 1 }));
        Assert.Equal("es", Apply("it, en, es", new TransformSpec { Op = "split", Separator = ",", Index = -1 }));
        Assert.Equal("it|en", Apply("it,en", new TransformSpec { Op = "split" }, new TransformSpec { Op = "join", Separator = "|" }));
        Assert.Equal("Inter · 42", Apply("Inter", new TransformSpec { Op = "concat", Values = new List<string> { "{value}", " · ", "{event.id}" } }));
        Assert.Equal("https://img.example.com/abc.png", Apply("abc", new TransformSpec { Op = "template", Value = "https://img.example.com/{value}.png" }));
    }

    [Fact]
    public void NumbersBooleansUrlsHtml()
    {
        Assert.Equal("1500", Apply("1,500 viewers", new TransformSpec { Op = "number" }));
        Assert.Equal("2.5", Apply("2,5", new TransformSpec { Op = "number" }));
        Assert.Equal("true", Apply("LIVE", new TransformSpec { Op = "boolean" }));
        Assert.Equal("false", Apply("0", new TransformSpec { Op = "boolean" }));
        Assert.Equal("true", Apply("yes-please", new TransformSpec { Op = "boolean", TrueValues = new List<string> { "yes-please" } }));
        Assert.Equal("https://api.example.com/img/x.png", Apply("/img/x.png", new TransformSpec { Op = "absoluteUrl" }));
        Assert.Equal("https://cdn.example.com/x.png", Apply("//cdn.example.com/x.png", new TransformSpec { Op = "absoluteUrl" }));
        Assert.Equal("Bold & text", Apply("<b>Bold</b> &amp; text", new TransformSpec { Op = "stripHtml" }));
    }

    [Fact]
    public void CatastrophicRegexDoesNotHang()
    {
        var input = new string('a', 40) + "!";
        var started = DateTime.UtcNow;
        var output = Apply(input, new TransformSpec { Op = "replace", Pattern = "^(a+)+$", Replacement = "x" });
        Assert.Equal(input, output);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void TemplatesResolveVariablesPathsFiltersAndEscapes()
    {
        var t = Ctx();
        t.Current = JsonNode.Parse("""{ "home": "Inter", "away": "Milan", "q": "a b" }""");
        Assert.Equal("Inter vs Milan", t.Render("{$.home} vs {$.away}"));
        Assert.Equal("https://api.example.com/v1/events/42", t.Render("{baseUrl}/events/{event.id}"));
        Assert.Equal("a%20b", t.Render("{$.q|urlencode}"));
        Assert.Equal("INTER", t.Render("{$.home|upper}"));
        Assert.Equal("{literal}", t.Render("{{literal}}"));
        Assert.Equal("2026-10-25", t.Render("{date}"));
        Assert.Equal("20261025", t.Render("{date:yyyyMMdd}"));
        Assert.Equal(Clock.Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), t.Render("{now}"));
        var missing = t.TrackMissing();
        Assert.Equal("x=", t.Render("x={unknown.var}"));
        Assert.Contains("unknown.var", missing);
    }

    [Fact]
    public void FieldShorthandRules()
    {
        Assert.Equal("$.title", FieldSpec.FromShorthand("$.title").Path);
        Assert.Equal("{$.a} vs {$.b}", FieldSpec.FromShorthand("{$.a} vs {$.b}").Template);
        Assert.Equal("Embed", FieldSpec.FromShorthand("Embed").Value);
    }
}
