using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Playback;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Jellyfin.Plugin.LiveHub.Users;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Playback;

public class QualityLanguageTests : IClassFixture<MockServerFixture>
{
    private const string Master = "#EXTM3U\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=1920x1080,CODECS=\"hvc1.1.6.L120.90,ec-3\"\n1080.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1280x720,CODECS=\"avc1.4d401f,mp4a.40.2\"\n720.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=2800000,RESOLUTION=1280x720,CODECS=\"avc1.4d401f,mp4a.40.2\"\n720b.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=640x360,CODECS=\"avc1.42c01e,mp4a.40.2\"\n360.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=416x234,CODECS=\"avc1.42c00d,mp4a.40.2\"\n234.m3u8\n";

    private readonly MockServerFixture _mock;

    public QualityLanguageTests(MockServerFixture mock) => _mock = mock;

    private static ResolvedStream Resolved() => new() { Kind = Model.StreamKind.Hls, Playlist = HlsPlaylist.Parse(Master, new Uri("https://cdn.example/master.m3u8")) };

    [Theory]
    [InlineData("ITA", "it")]
    [InlineData("Italiano", "it")]
    [InlineData("italian", "it")]
    [InlineData("it-IT", "it")]
    [InlineData("eng", "en")]
    [InlineData("Español", "es")]
    [InlineData("deu", "de")]
    [InlineData("pt_BR", "pt")]
    [InlineData("klingon", null)]
    public void LanguagesAreNormalizedToIso6391(string raw, string? expected) => Assert.Equal(expected, LocaleNormalizer.NormalizeLanguage(raw));

    [Theory]
    [InlineData("UK", "GB")]
    [InlineData("Italia", "IT")]
    [InlineData("it-IT", "IT")]
    [InlineData("ESP", "ES")]
    [InlineData("de", "DE")]
    [InlineData("Narnia", null)]
    public void CountriesAreNormalizedToIso3166(string raw, string? expected) => Assert.Equal(expected, LocaleNormalizer.NormalizeCountry(raw));

    [Theory]
    [InlineData("Serie A: Inter vs Milan [ITA]", "it")]
    [InlineData("LaLiga | ES | Real Madrid - Barcelona", "es")]
    [InlineData("Premier League (ENG) Arsenal vs Chelsea", "en")]
    [InlineData("Inter vs Milan HD", null)]
    public void LanguagesAreGuessedFromTitles(string title, string? expected) => Assert.Equal(expected, LocaleNormalizer.GuessLanguage(title));

    [Fact]
    public void JellyfinStreamLanguagesUseIso6392()
    {
        Assert.Equal("ita", LocaleNormalizer.ToIso6392("it"));
        Assert.Equal("eng", LocaleNormalizer.ToIso6392("en"));
        Assert.Null(LocaleNormalizer.ToIso6392(null));
        Assert.Equal("Italiano", LocaleNormalizer.LanguageName("it", "it"));
        Assert.Equal("Inglese", LocaleNormalizer.LanguageName("en", "it"));
        Assert.Equal("Italian", LocaleNormalizer.LanguageName("it", "en"));
    }

    [Fact]
    public void EveryDistinctHeightBecomesAChoiceOrderedByPreference()
    {
        var resolved = Resolved();
        Assert.Equal(new[] { "h1080", "h720", "h360", "h234" }, MediaSourceBuilder.VariantChoices(resolved, new UserPreferences(), VariantSelector.Auto));
        Assert.Equal(new[] { "h360", "h720", "h1080", "h234" }, MediaSourceBuilder.VariantChoices(resolved, new UserPreferences { Quality = QualityPreference.PreferSd }, new VariantSelector { PreferLow = true }));
        Assert.Equal(new[] { "h1080", "h720" }, MediaSourceBuilder.VariantChoices(resolved, new UserPreferences { Quality = QualityPreference.HdOnly }, VariantSelector.Auto));
        var max = new UserPreferences { Quality = QualityPreference.MaxBitrate, MaxBitrateKbps = 3000 };
        Assert.Equal("h720", MediaSourceBuilder.VariantChoices(resolved, max, MediaSourceBuilder.SelectorFor(max))[0]);
        Assert.Equal(new[] { "auto" }, MediaSourceBuilder.VariantChoices(null, new UserPreferences(), VariantSelector.Auto));
    }

    [Fact]
    public void VariantCodecsMapToJellyfinCodecs()
    {
        var p = Resolved().Playlist!;
        var hevc = MediaSourceBuilder.MediaStreamsFor(p.Variants[0], "en");
        Assert.Equal("hevc", hevc[0].Codec);
        Assert.Equal("eac3", hevc[1].Codec);
        Assert.Equal("eng", hevc[1].Language);
        var baseline = MediaSourceBuilder.MediaStreamsFor(p.Variants[3], null);
        Assert.Equal("Constrained Baseline", baseline[0].Profile);
        Assert.Equal(360, baseline[0].Height);
        Assert.Empty(MediaSourceBuilder.MediaStreamsFor(new HlsVariant { Codecs = "mp4a.40.2" }, "it"));
        Assert.Empty(MediaSourceBuilder.MediaStreamsFor(new HlsVariant(), "it"));
    }

    [Fact]
    public async Task ResolvedMastersOfferOneSourcePerQualityInTheNativeSelector()
    {
        using var h = new Harness(_mock);
        h.Config.UiLanguage = "it";
        h.AddAllMockDefinitions();
        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);
        var lakers = h.Catalog.View.Events.Single(e => e.Title.StartsWith("Lakers", StringComparison.Ordinal));
        foreach (var s in h.Catalog.StreamsOf(lakers))
        {
            await h.Resolver.ResolveAsync(s, false, CancellationToken.None);
        }

        var user = Guid.NewGuid();
        h.Prefs.Save(user, new UserPreferences { Language = "es", Quality = QualityPreference.PreferSd });
        var view = h.Views.Create(user);
        var sources = h.Builder.Build(ItemKind.Event, lakers.Key, view.VisibleStreams(h.Catalog.StreamsOf(lakers)), view);
        Assert.Equal(4, sources.Count);
        Assert.StartsWith("480p · Spagnolo", sources[0].Name, StringComparison.Ordinal);
        Assert.StartsWith("1080p · Spagnolo", sources[1].Name, StringComparison.Ordinal);
        Assert.StartsWith("480p · Inglese", sources[2].Name, StringComparison.Ordinal);
        Assert.Equal(480, sources[0].MediaStreams.Single(m => m.Type == MediaStreamType.Video).Height);
        Assert.False(sources[0].SupportsProbing);

        // Jellyfin sorts sources by video width: only the preferred one carries media streams so it stays first.
        Assert.All(sources.Skip(1), s => Assert.Empty(s.MediaStreams));
        Assert.All(sources.Skip(1), s => Assert.True(s.SupportsProbing));
        Assert.Equal(sources.Count, sources.Select(s => s.Id).Distinct().Count());
        Assert.True(h.Relay.TryAuthorize(sources[0].OpenToken, out var claims));
        Assert.Equal("h480", claims.Variant);
    }

    [Fact]
    public async Task RegionRulesFillCountryAndLanguage()
    {
        using var h = new Harness(_mock);
        h.Config.RegionRules.Add(new RegionRule { Match = "NBA", Country = "usa", Language = "English" });
        h.AddAllMockDefinitions();
        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);
        var bulls = h.Catalog.View.Events.Single(e => e.Title.StartsWith("Bulls", StringComparison.Ordinal));
        Assert.Equal("US", bulls.Country);
        Assert.Equal("en", bulls.Language);

        var user = Guid.NewGuid();
        h.Prefs.Save(user, new UserPreferences { Countries = { "it" } });
        var view = h.Views.Create(user);
        Assert.False(view.IsVisible(bulls));
    }
}
