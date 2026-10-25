using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Localization;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Playback;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Jellyfin.Plugin.LiveHub.Users;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Playback;

public class PlaybackTests : IClassFixture<MockServerFixture>
{
    private readonly MockServerFixture _mock;

    public PlaybackTests(MockServerFixture mock) => _mock = mock;

    private async Task<Harness> SyncedAsync()
    {
        var h = new Harness(_mock);
        h.Config.UiLanguage = "it";
        h.AddAllMockDefinitions();
        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);
        return h;
    }

    private static LiveHubMediaSourceProvider Provider(Harness h)
        => new(h.Events, h.Views, h.Builder, h.Relay, h.ConfigAccessor, new HttpContextAccessor(), NullLogger<LiveHubMediaSourceProvider>.Instance);

    [Fact]
    public async Task MediaSourcesFollowTheUserLanguageAndPointToTheRelay()
    {
        using var h = await SyncedAsync();
        var derby = h.Catalog.View.Events.Single(e => e.Title == "Inter vs Milan");
        var english = Guid.NewGuid();
        h.Prefs.Save(english, new UserPreferences { Language = "en" });

        var sources = await Provider(h).GetSourcesAsync(ItemKind.Event, derby.Key, english, CancellationToken.None);
        Assert.Equal(3, sources.Count);
        Assert.Contains("Inglese", sources[0].Name, StringComparison.Ordinal);
        Assert.All(sources, s =>
        {
            Assert.StartsWith(Harness.RelayBase + "/LiveHub/stream/", s.Path, StringComparison.Ordinal);
            Assert.Equal(MediaProtocol.Http, s.Protocol);
            Assert.True(s.RequiresOpening);
            Assert.True(s.RequiresClosing);
            Assert.True(s.IsInfiniteStream);
            Assert.True(s.IsRemote);
            Assert.True(s.SupportsTranscoding);
            Assert.True(h.Relay.TryAuthorize(s.OpenToken, out var c) && c.UserId == english);
            Assert.True(Guid.TryParseExact(s.Id, "N", out _));
        });
        Assert.Equal(sources.Count, sources.Select(s => s.Id).Distinct().Count());
        Assert.Matches(@"^(720p|480p|HD|SD|1080p|Auto) · \w+ · .+ #\d$", sources[0].Name);

        var italian = await Provider(h).GetSourcesAsync(ItemKind.Event, derby.Key, Guid.Empty, CancellationToken.None);
        Assert.Contains("Italiano", italian[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvedVariantsBecomeMediaStreamsAndSkipProbing()
    {
        using var h = await SyncedAsync();
        var derby = h.Catalog.View.Events.Single(e => e.Title == "Inter vs Milan");
        var before = await Provider(h).GetSourcesAsync(ItemKind.Event, derby.Key, Guid.Empty, CancellationToken.None);
        Assert.All(before, s => Assert.True(s.SupportsProbing));
        foreach (var s in h.Catalog.StreamsOf(derby))
        {
            await h.Resolver.ResolveAsync(s, false, CancellationToken.None);
        }

        var after = await Provider(h).GetSourcesAsync(ItemKind.Event, derby.Key, Guid.Empty, CancellationToken.None);
        var first = after[0];
        Assert.False(first.SupportsProbing);
        var video = first.MediaStreams.Single(m => m.Type == MediaStreamType.Video);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(1080, video.Height);
        Assert.Equal("High", video.Profile);
        var audio = first.MediaStreams.Single(m => m.Type == MediaStreamType.Audio);
        Assert.Equal("aac", audio.Codec);
        Assert.Equal("ita", audio.Language);
        Assert.EndsWith("/master.m3u8", first.Path, StringComparison.Ordinal);
        Assert.Equal("hls", first.Container);
    }

    [Fact]
    public async Task OpeningSharesTheLiveStream()
    {
        using var h = await SyncedAsync();
        var derby = h.Catalog.View.Events.Single(e => e.Title == "Inter vs Milan");
        var provider = Provider(h);
        var sources = await provider.GetSourcesAsync(ItemKind.Event, derby.Key, Guid.Empty, CancellationToken.None);

        var live = await provider.OpenMediaSource(sources[0].OpenToken, new List<ILiveStream>(), CancellationToken.None);
        Assert.IsType<RelayLiveStream>(live);
        Assert.True(live.EnableStreamSharing);
        Assert.Equal(live.UniqueId, live.MediaSource.LiveStreamId); // required by MediaSourceManager.OpenLiveStreamInternal
        Assert.EndsWith("/master.m3u8", live.MediaSource.Path, StringComparison.Ordinal);
        var session = Assert.Single(h.Relay.Sessions);
        Assert.Equal(1, session.Holders);
        Assert.NotNull(session.Master);

        var again = await provider.OpenMediaSource(sources[0].OpenToken, new List<ILiveStream> { live }, CancellationToken.None);
        Assert.Same(live, again);
        Assert.Equal(2, live.ConsumerCount);

        await live.Close();
        Assert.Equal(0, session.Holders);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenMediaSource("forged.token", new List<ILiveStream>(), CancellationToken.None));
    }

    [Fact]
    public async Task PerUserStreamLimitIsEnforced()
    {
        using var h = await SyncedAsync();
        h.Config.MaxStreamsPerUser = 1;
        var user = Guid.NewGuid();
        var provider = Provider(h);
        var derby = h.Catalog.View.Events.Single(e => e.Title == "Inter vs Milan");
        var lakers = h.Catalog.View.Events.Single(e => e.Title.StartsWith("Lakers", StringComparison.Ordinal));
        var a = await provider.GetSourcesAsync(ItemKind.Event, derby.Key, user, CancellationToken.None);
        var b = await provider.GetSourcesAsync(ItemKind.Event, lakers.Key, user, CancellationToken.None);
        await provider.OpenMediaSource(a[0].OpenToken, new List<ILiveStream>(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenMediaSource(b[0].OpenToken, new List<ILiveStream>(), CancellationToken.None));
    }

    [Fact]
    public async Task ChannelItemsHaveSourcesToo()
    {
        using var h = await SyncedAsync();
        var news = h.Catalog.View.Channels.Single(c => c.Name == "News 24");
        var sources = await Provider(h).GetSourcesAsync(ItemKind.Channel, news.Key, Guid.Empty, CancellationToken.None);
        var source = Assert.Single(sources);
        Assert.True(h.Relay.TryAuthorize(source.OpenToken, out var claims));
        Assert.Equal(ItemKind.Channel, claims.Kind);
    }

    [Fact]
    public void QualityPreferencesOrderStreams()
    {
        var streams = new List<LiveStream>
        {
            new() { Id = "sd-it", Height = 480, Language = "it", Bitrate = 1_500_000 },
            new() { Id = "fhd-it", Height = 1080, Language = "it", Bitrate = 6_000_000 },
            new() { Id = "hd-en", Height = 720, Language = "en", Bitrate = 3_000_000 },
            new() { Id = "low-it", Height = 240, Language = "it", Bitrate = 400_000 },
        };
        var cfg = new Configuration.PluginConfiguration();
        UserView View(UserPreferences p) => new(Guid.NewGuid(), p, _ => true, cfg, DateTimeOffset.UtcNow);

        Assert.Equal(new[] { "fhd-it", "sd-it", "low-it", "hd-en" }, MediaSourceBuilder.Order(streams, View(new UserPreferences { Language = "it" })).Select(s => s.Id));
        Assert.Equal(new[] { "sd-it", "fhd-it", "low-it", "hd-en" }, MediaSourceBuilder.Order(streams, View(new UserPreferences { Language = "it", Quality = QualityPreference.PreferSd })).Select(s => s.Id));
        Assert.Equal(new[] { "fhd-it", "hd-en" }, MediaSourceBuilder.Order(streams, View(new UserPreferences { Language = "it", Quality = QualityPreference.HdOnly })).Select(s => s.Id));
        Assert.Equal("sd-it", MediaSourceBuilder.Order(streams, View(new UserPreferences { Language = "it", Quality = QualityPreference.MaxBitrate, MaxBitrateKbps = 2000 }))[0].Id);
        Assert.Equal("hd-en", MediaSourceBuilder.Order(streams, View(new UserPreferences { Language = "en" }))[0].Id);
        Assert.Equal("max2000000", MediaSourceBuilder.SelectorFor(new UserPreferences { Quality = QualityPreference.MaxBitrate, MaxBitrateKbps = 2000 }).ToString());
        Assert.Equal("low", MediaSourceBuilder.SelectorFor(new UserPreferences { Quality = QualityPreference.PreferSd }).ToString());
    }

    [Fact]
    public void FailoverOrderPrefersSameLanguageThenClosestQuality()
    {
        var primary = new LiveStream { Id = "p", ProviderId = "a", Height = 720, Language = "it" };
        var all = new List<LiveStream>
        {
            new() { Id = "en-720", ProviderId = "a", Height = 720, Language = "en", Priority = 200 },
            primary,
            new() { Id = "it-1080-b", ProviderId = "b", Height = 1080, Language = "it" },
            new() { Id = "it-720-b", ProviderId = "b", Height = 720, Language = "it" },
        };
        Assert.Equal(new[] { "p", "it-720-b", "it-1080-b", "en-720" }, StreamOrdering.FailoverOrder(primary, all).Select(s => s.Id));
    }

    [Fact]
    public async Task TestPlaybackDiagnosesEmbedsHeadersAndFailures()
    {
        using var h = new Harness(_mock);
        h.Config.UiLanguage = "it";
        h.AddAllMockDefinitions();
        var tester = new PlaybackTester(h.Engine, h.Store, h.Resolver, h.Http, new LocalizationService(), h.ConfigAccessor);

        var embed = await tester.TestAsync(new PlaybackTestRequest { Definition = Fixtures.Definition("mock4-embed.json") }, CancellationToken.None);
        Assert.True(embed.Playable, string.Join("\n", embed.Steps));
        Assert.Equal("extract", embed.Strategy);
        Assert.True(embed.FromEmbed);
        Assert.Equal("HLS · MPEG-TS", embed.Format);
        Assert.Equal(2, embed.Variants.Count);
        Assert.Contains("avc1.640028", embed.Codecs);
        Assert.StartsWith("Riproducibile", embed.Summary, StringComparison.Ordinal);

        var multilevel = await tester.TestAsync(new PlaybackTestRequest { ProviderId = "mock-multilevel" }, CancellationToken.None);
        Assert.True(multilevel.Playable, string.Join("\n", multilevel.Steps));
        Assert.Contains(multilevel.Steps, s => s.StartsWith("stream chain:", StringComparison.Ordinal));

        var forbidden = await tester.TestAsync(new PlaybackTestRequest { ProviderId = "mock-flat", Url = MockServerFixture.BaseUrl + "/hls/forbidden-z/master.m3u8" }, CancellationToken.None);
        Assert.False(forbidden.Playable);
        Assert.StartsWith("Accesso negato", forbidden.Summary, StringComparison.Ordinal);

        var ssrf = await tester.TestAsync(new PlaybackTestRequest { Url = "http://192.168.1.1/x.m3u8" }, CancellationToken.None);
        Assert.False(ssrf.Playable);
        Assert.StartsWith("Destinazione bloccata", ssrf.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PreResolveTargetsFavouritesAndPopularEventsStartingSoon()
    {
        var now = DateTimeOffset.UtcNow;
        var cfg = new Configuration.PluginConfiguration { PreResolveMinutes = 10, PreResolveTopPopular = 1 };
        var events = new List<LiveEvent>
        {
            new() { Key = "fav", Title = "Juventus vs Roma", Start = now.AddMinutes(5), Popularity = 1 },
            new() { Key = "pop", Title = "Big match", Start = now.AddMinutes(-20), IsLiveFlag = true, Popularity = 100 },
            new() { Key = "later", Title = "Juventus vs Inter", Start = now.AddHours(3), Popularity = 1000 },
            new() { Key = "small", Title = "Small", Start = now.AddMinutes(2), Popularity = 3 },
        };
        var targets = PreResolveService.Targets(events, e => e.Title.Contains("Juventus", StringComparison.Ordinal), now, cfg);
        Assert.Equal(new[] { "fav", "pop" }, targets.Select(e => e.Key));
    }
}
