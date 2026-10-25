using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Formats;
using Jellyfin.Plugin.LiveHub.Locale;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Formats;

public class ParserTests
{
    [Fact]
    public void M3uAttributesNamesHeadersAndGroups()
    {
        const string text = """
        #EXTM3U url-tvg="https://epg.example.com/guide.xml.gz" x-tvg-url="ignored"
        #EXTINF:-1 tvg-id="rai1.it" tvg-name="Rai 1" tvg-logo="https://l/rai1.png" group-title="Generalisti, IT" tvg-language="Italian" tvg-country="IT" tvg-chno="1",Rai 1 HD
        #EXTVLCOPT:http-referrer=https://ref.example/
        #EXTVLCOPT:http-user-agent=UA/1.0
        https://cdn.example.com/rai1/index.m3u8
        #EXTINF:0,Channel With, Comma
        #EXTGRP:Sport
        https://cdn.example.com/s.ts|User-Agent=Kodi%2F20&Referer=https%3A%2F%2Fk%2F
        # a comment
        #EXTINF:-1 tvg-name='Single Quoted' group-title=NoQuotes,
        http://x.example/y.mpd
        """;
        var p = M3uParser.Parse(text);
        Assert.Equal("https://epg.example.com/guide.xml.gz", p.GuideUrl);
        Assert.Equal(3, p.Entries.Count);

        var rai = p.Entries[0];
        Assert.Equal("Rai 1 HD", rai.Name);
        Assert.Equal("rai1.it", rai.Attr("tvg-id"));
        Assert.Equal("Generalisti, IT", rai.Attr("group-title"));
        Assert.Equal("1", rai.Attr("tvg-chno"));
        Assert.Equal("https://ref.example/", rai.Headers["Referer"]);
        Assert.Equal("UA/1.0", rai.Headers["User-Agent"]);

        var comma = p.Entries[1];
        Assert.Equal("Channel With, Comma", comma.Name);
        Assert.Equal("Sport", comma.Attr("group-title"));
        Assert.Equal("https://cdn.example.com/s.ts", comma.Url);
        Assert.Equal("Kodi/20", comma.Headers["User-Agent"]);
        Assert.Equal("https://k/", comma.Headers["Referer"]);
        Assert.Empty(comma.Headers.Keys.Except(new[] { "User-Agent", "Referer" }));

        var single = p.Entries[2];
        Assert.Equal("Single Quoted", single.Name);
        Assert.Equal("NoQuotes", single.Attr("group-title"));
    }

    [Fact]
    public void HlsPlaylistsAreNotChannelLists()
    {
        Assert.True(M3uParser.IsHlsPlaylist("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\na.m3u8"));
        Assert.True(M3uParser.IsHlsPlaylist("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\ns.ts"));
        Assert.False(M3uParser.IsHlsPlaylist("#EXTM3U\n#EXTINF:-1,Ch\nhttp://a/b.m3u8"));
    }

    [Fact]
    public void XmlTvWindowFilterAndSafety()
    {
        const string xml = """
        <?xml version="1.0"?>
        <!DOCTYPE tv SYSTEM "xmltv.dtd">
        <tv>
          <channel id="a"><display-name>Alpha</display-name><display-name>A</display-name><icon src="https://i/a.png"/></channel>
          <channel id="b"><display-name>Beta</display-name></channel>
          <programme start="20261025180000 +0000" stop="20261025190000 +0000" channel="a"><title>Now</title><desc>Mixed <b>bold</b> text</desc><category>Sports</category><icon src="https://i/p.png"/></programme>
          <programme start="20261025190000 +0000" stop="20261025200000 +0000" channel="a"><title>Next</title><sub-title>Ep 2</sub-title></programme>
          <programme start="20261001000000 +0000" stop="20261001010000 +0000" channel="a"><title>Old</title></programme>
          <programme start="20261025180000 +0000" stop="20261025190000 +0000" channel="b"><title>B show</title></programme>
        </tv>
        """;
        var from = new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.Zero);
        var guide = XmlTvParser.Parse(xml, from, from.AddDays(1));
        Assert.Equal(2, guide.Channels.Count);
        Assert.Equal(new[] { "Alpha", "A" }, guide.Channels[0].DisplayNames);
        Assert.Equal("https://i/a.png", guide.Channels[0].Icon);
        Assert.Equal(3, guide.Programmes.Count);
        var now = guide.Programmes[0];
        Assert.Equal("Now", now.Title);
        Assert.Equal("Sports", now.Categories.Single());
        Assert.Equal("Mixed bold text", now.Description);
        Assert.Equal("https://i/p.png", now.Icon);
        Assert.Equal("Ep 2", guide.Programmes[1].SubTitle);
        Assert.Null(guide.Error);

        var filtered = XmlTvParser.Parse(xml, from, from.AddDays(1), new HashSet<string> { "b" });
        Assert.Single(filtered.Channels);
        Assert.Equal("B show", filtered.Programmes.Single().Title);
    }

    [Fact]
    public void XmlTvExternalEntitiesAreNeverExpanded()
    {
        const string xml = """
        <?xml version="1.0"?>
        <!DOCTYPE tv [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
        <tv>
          <channel id="a"><display-name>Alpha</display-name></channel>
          <programme start="20261025180000 +0000" stop="20261025190000 +0000" channel="a"><title>Now</title><desc>&xxe;</desc></programme>
        </tv>
        """;
        var from = new DateTimeOffset(2026, 10, 25, 17, 0, 0, TimeSpan.Zero);
        var guide = XmlTvParser.Parse(xml, from, from.AddDays(1));
        Assert.NotNull(guide.Error);
        Assert.Single(guide.Channels);
        Assert.DoesNotContain(guide.Programmes, p => (p.Description ?? string.Empty).Contains("root", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ITA", "it")]
    [InlineData("Italian", "it")]
    [InlineData("italiano", "it")]
    [InlineData("it-IT", "it")]
    [InlineData("eng", "en")]
    [InlineData("English", "en")]
    [InlineData("Español", "es")]
    [InlineData("castellano", "es")]
    [InlineData("deu", "de")]
    [InlineData("fra", "fr")]
    [InlineData("UK", "en")]
    [InlineData("xx-unknown", null)]
    public void LanguagesNormalizeToIso6391(string raw, string? expected)
    {
        Assert.Equal(expected, LocaleNormalizer.NormalizeLanguage(raw));
    }

    [Theory]
    [InlineData("UK", "GB")]
    [InlineData("Italia", "IT")]
    [InlineData("it-IT", "IT")]
    [InlineData("us", "US")]
    [InlineData("ESP", "ES")]
    [InlineData("Germany", "DE")]
    public void CountriesNormalizeToIso3166(string raw, string expected)
    {
        Assert.Equal(expected, LocaleNormalizer.NormalizeCountry(raw));
    }

    [Theory]
    [InlineData("Sky Sport Uno [ITA] HD", "it")]
    [InlineData("Premier League (ENG)", "en")]
    [InlineData("DAZN 1 | ES", "es")]
    [InlineData("Inter vs Milan", null)]
    public void LanguageHeuristicsOnTitles(string title, string? expected)
    {
        Assert.Equal(expected, LocaleNormalizer.GuessLanguage(title));
    }
}
