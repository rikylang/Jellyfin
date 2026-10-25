using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using LiveHub.MockServer;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Engine;

/// <summary>End-to-end engine tests against the six mock APIs (in-process, no Internet).</summary>
public class MockApiEngineTests : IClassFixture<MockServerFixture>
{
    private readonly MockServerFixture _mock;

    public MockApiEngineTests(MockServerFixture mock) => _mock = mock;

    [Fact]
    public async Task Api1_FlatArrayWithUnixTimestamps()
    {
        var engine = _mock.CreateEngine();
        var result = await engine.RunAsync(Fixtures.Definition("mock1-flat.json"), new RunOptions(), CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Errors));
        var events = result.Snapshot.Events;
        Assert.Equal(4, events.Count);
        var derby = events.Single(e => e.ExternalId == "101");
        Assert.Equal("Inter vs Milan", derby.Title);
        Assert.Equal("Calcio", derby.Category);
        Assert.Equal("Serie A", derby.Competition);
        Assert.True(derby.IsLiveFlag);
        Assert.Equal(15000, derby.Popularity);
        Assert.Equal("it", derby.Language);
        Assert.Equal("IT", derby.Country);
        Assert.Equal("http://localhost/img/inter-milan.png", derby.ThumbUrl);
        Assert.Equal(new[] { "Inter", "Milan" }, derby.Participants.Select(p => p.Name));
        Assert.Equal(2, derby.Streams.Count);
        Assert.Equal(StreamKind.Hls, derby.Streams[0].Kind);
        Assert.Equal(720, derby.Streams[0].Height);
        Assert.Equal("it", derby.Streams[0].Language);
        Assert.Equal("en", derby.Streams[1].Language);
        Assert.Equal(480, derby.Streams[1].Height);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - derby.Start).TotalMinutes - 20) < 2);
    }

    [Fact]
    public async Task Api2_NestedJsonCursorPaginationIsoDatesApiKeyAndEtag()
    {
        var engine = _mock.CreateEngine();
        var def = Fixtures.Definition("mock2-nested.json");
        var result = await engine.RunAsync(def, new RunOptions(), CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Errors));
        Assert.Equal(2, result.Snapshot.Categories.Count);
        Assert.Equal(5, result.Snapshot.Events.Count);
        Assert.Equal(3, result.Diagnostics.Requests.Count(r => r.Endpoint == "schedule"));

        var lakers = result.Snapshot.Events.Single(e => e.ExternalId == "evt-1");
        Assert.Equal(EventStatus.Live, lakers.StatusFlag);
        Assert.Equal("NBA", lakers.Competition);
        Assert.Equal("US", lakers.Country);
        Assert.Equal(TimeSpan.Zero, lakers.Start.Offset);
        Assert.NotNull(lakers.End);
        Assert.Equal("http://localhost/img/lakers.png", lakers.Participants[0].LogoUrl);
        Assert.Equal(new[] { "en", "es" }, lakers.Streams.Select(s => s.Language));
        Assert.Equal(1080, lakers.Streams[0].Height);
        Assert.Equal(5_000_000, lakers.Streams[0].Bitrate);

        // Second run: every page answers 304 thanks to ETag and the body is reused.
        var second = await engine.RunAsync(def, new RunOptions(), CancellationToken.None);
        Assert.True(second.Success);
        Assert.Equal(5, second.Snapshot.Events.Count);
        Assert.All(second.Diagnostics.Requests.Where(r => r.Endpoint == "schedule"), r => Assert.True(r.NotModified));
    }

    [Fact]
    public async Task Api2_WrongApiKeyFailsCleanly()
    {
        var engine = _mock.CreateEngine();
        var def = Fixtures.Definition("mock2-nested.json");
        def.Id = "mock-nested-wrong";
        def.Secrets!["apiKey"] = "wrong";
        var result = await engine.RunAsync(def, new RunOptions(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Errors, e => e.Contains("401", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics.Errors, e => e.Contains("wrong", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Api3_MultiLevelStreamsOAuth2AndCustomDatePattern()
    {
        var engine = _mock.CreateEngine();
        var def = Fixtures.Definition("mock3-multilevel.json");
        var result = await engine.RunAsync(def, new RunOptions(), CancellationToken.None);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Errors));
        Assert.Equal(2, result.Snapshot.Events.Count);
        var juve = result.Snapshot.Events.Single(e => e.ExternalId == "m1");
        Assert.Equal("Juventus vs Napoli", juve.Title);
        Assert.Equal("Calcio", juve.Category);
        Assert.True(juve.HasLazyStreams);
        Assert.Empty(juve.Streams);
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - juve.Start).TotalMinutes - 30) < 2, "kickoff parsed in Europe/Rome");

        var (streams, diag) = await engine.FetchLazyStreamsAsync(def, juve, CancellationToken.None);
        Assert.Empty(diag.Errors);
        Assert.Equal(4, streams.Count);
        var alpha = streams.Where(s => s.Label == "Alpha").ToList();
        Assert.Equal(2, alpha.Count);
        Assert.All(alpha, s => Assert.Equal("it", s.Language));
        Assert.Equal(MockServerApp.RequiredReferer, alpha[0].Headers["Referer"]);
        Assert.Equal(720, alpha[0].Height);
        Assert.All(streams.Where(s => s.Label == "Beta"), s => Assert.Equal("en", s.Language));
    }

    [Fact]
    public async Task Api4_EmbedPagesBecomeEmbedStreams()
    {
        var engine = _mock.CreateEngine();
        var result = await engine.RunAsync(Fixtures.Definition("mock4-embed.json"), new RunOptions(), CancellationToken.None);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Errors));
        Assert.Equal(2, result.Snapshot.Events.Count);
        Assert.All(result.Snapshot.Events, e =>
        {
            Assert.Single(e.Streams);
            Assert.Equal(StreamKind.Embed, e.Streams[0].Kind);
            Assert.Equal("en", e.Language);
        });
        var monza = result.Snapshot.Events.Single(e => e.ExternalId == "e1");
        Assert.True(Math.Abs((DateTimeOffset.UtcNow - monza.Start).TotalMinutes - 5) < 2);
    }

    [Fact]
    public async Task Api5_M3uAndGzippedXmlTv()
    {
        var engine = _mock.CreateEngine();
        var result = await engine.RunAsync(Fixtures.Definition("mock5-m3u.json"), new RunOptions(), CancellationToken.None);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Errors));
        var channels = result.Snapshot.Channels;
        Assert.Equal(3, channels.Count);
        Assert.DoesNotContain(channels, c => c.Name.Contains("Hidden", StringComparison.Ordinal));

        var news = channels.Single(c => c.EpgId == "news24.it");
        Assert.Equal("it", news.Language);
        Assert.Equal("IT", news.Country);
        Assert.Equal("News", news.Category);
        var sport = channels.Single(c => c.EpgId == "sport1.uk");
        Assert.Equal("en", sport.Language);
        Assert.Equal("GB", sport.Country);
        Assert.Equal(720, sport.Streams[0].Height);
        Assert.Equal("Mozilla/5.0 LiveHubMock", sport.Streams[0].Headers["User-Agent"]);
        Assert.Equal("fr", channels.Single(c => c.EpgId == "musique.fr").Language);

        Assert.NotEmpty(result.Snapshot.Programs);
        Assert.All(channels, c => Assert.Contains(result.Snapshot.Programs, p => p.ChannelKey == c.Key));
        var now = DateTimeOffset.UtcNow;
        Assert.Contains(result.Snapshot.Programs, p => p.ChannelKey == news.Key && p.Start <= now && p.End > now);
    }

    [Fact]
    public async Task Api6_XtreamCodes()
    {
        var engine = _mock.CreateEngine();
        var result = await engine.RunAsync(Fixtures.Definition("mock6-xtream.json"), new RunOptions(), CancellationToken.None);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Errors));
        var channels = result.Snapshot.Channels;
        Assert.Equal(3, channels.Count);
        Assert.DoesNotContain(channels, c => c.Name.Contains("Adult", StringComparison.Ordinal));
        var sport = channels.Single(c => c.ExternalId == "1001");
        Assert.Equal("Sport IT", sport.Category);
        Assert.Equal("sportitalia.it", sport.EpgId);
        Assert.Equal("http://localhost/xtream/live/user/pass/1001.m3u8", sport.Streams[0].Url);
        Assert.Equal(StreamKind.Hls, sport.Streams[0].Kind);
        Assert.Equal("it", sport.Language);
        Assert.Contains(result.Snapshot.Programs, p => p.ChannelKey == sport.Key);

        // Credentials never appear in diagnostics.
        Assert.All(result.Diagnostics.Requests, r => Assert.DoesNotContain("password=pass", r.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Api6_WrongCredentials()
    {
        var engine = _mock.CreateEngine();
        var def = Fixtures.Definition("mock6-xtream.json");
        def.Id = "mock-xtream-bad";
        def.Secrets!["password"] = "nope";
        var result = await engine.RunAsync(def, new RunOptions(), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics.Errors, e => e.Contains("invalid credentials", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewUsesPastedSampleInsteadOfNetwork()
    {
        var engine = _mock.CreateEngine();
        var def = Fixtures.Definition("mock1-flat.json");
        var sample = """[ { "id": 9, "title": "A - B", "sport": "Calcio", "start": 1792957500, "live": 0, "streams": [ { "url": "https://cdn.example.com/x/master.m3u8", "q": "1080p" } ] } ]""";
        var result = await engine.RunAsync(def, new RunOptions { Preview = true, SampleBody = sample }, CancellationToken.None);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Errors));
        var ev = Assert.Single(result.Snapshot.Events);
        Assert.Equal("A vs B", ev.Title);
        Assert.Equal(1080, ev.Streams[0].Height);
        Assert.Single(result.Diagnostics.Samples["all"]);
    }
}
