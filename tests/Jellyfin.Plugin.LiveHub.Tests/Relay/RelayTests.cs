using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Relay;

public class RelayTests : IClassFixture<MockServerFixture>
{
    private static readonly Regex SegmentRef = new(@"^\.\./s/(?<id>[A-Za-z0-9_-]+)\.(ts|m4s|aac|mp4)$", RegexOptions.Multiline);
    private readonly MockServerFixture _mock;

    public RelayTests(MockServerFixture mock) => _mock = mock;

    private static string Hls(string name) => MockServerFixture.BaseUrl + "/hls/" + name + "/master.m3u8";

    /// <summary>Publishes a custom event (streams of the mock-flat provider, which allows the local mock server).</summary>
    private static LiveEvent AddEvent(Harness h, string title, params string[] streamNames) => AddEventFor(h, "mock-flat", title, streamNames);

    private static LiveEvent AddEventFor(Harness h, string provider, string title, params string[] streamNames)
    {
        var now = h.Engine.Clock.GetUtcNow();
        var ev = new LiveEvent
        {
            Key = "test-" + title.ToLowerInvariant().Replace(' ', '-'),
            ProviderId = provider,
            ExternalId = title,
            Title = title,
            Category = "Test",
            Start = now.AddMinutes(-10),
            IsLiveFlag = true,
            SourceProviders = new List<string> { provider },
        };
        var priority = 100;
        foreach (var name in streamNames)
        {
            ev.Streams.Add(new LiveStream { Id = ev.Key + "-" + name, ProviderId = provider, Url = Hls(name), Kind = StreamKind.Hls, Language = "it", Priority = priority-- });
        }

        var snapshot = new ProviderSnapshot { ProviderId = provider, FetchedAt = now };
        snapshot.Events.Add(ev);
        h.Catalog.ApplySnapshot(provider, provider, snapshot);
        h.Catalog.Rebuild();
        return h.Catalog.FindEvent(ev.Key) ?? throw new InvalidOperationException("event not published");
    }

    private static RelayClaims Claims(LiveEvent ev, int stream = 0, string variant = "auto")
        => new() { Type = "e", Key = ev.Key, StreamId = ev.Streams[stream].Id, Variant = variant };

    private static List<string> SegmentIds(string playlist) => SegmentRef.Matches(playlist).Select(m => m.Groups["id"].Value).ToList();

    private static long MediaSequence(string playlist) => long.Parse(Regex.Match(playlist, @"#EXT-X-MEDIA-SEQUENCE:(\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task RewritesPlaylistsAndRelaysSegments()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var h = new Harness(_mock, clock);
        h.AddAllMockDefinitions();
        var ev = AddEvent(h, "Relay Basic", "basic-a");
        var session = h.Relay.GetSession(Claims(ev));

        var master = await h.Relay.GetMasterAsync(session, CancellationToken.None);
        Assert.Contains("#EXT-X-STREAM-INF:BANDWIDTH=5000000", master, StringComparison.Ordinal);
        Assert.Contains("p/v0.m3u8", master, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", master, StringComparison.Ordinal);
        Assert.DoesNotContain("480/index", master, StringComparison.Ordinal);

        var media = await h.Relay.GetMediaPlaylistAsync(session, "v0", CancellationToken.None);
        Assert.DoesNotContain("localhost", media, StringComparison.Ordinal);
        var ids = SegmentIds(media);
        Assert.Equal(6, ids.Count);
        var seg = await h.Relay.GetSegmentAsync(session, ids[^1], CancellationToken.None);
        Assert.Equal(0x47, seg.Data[0]);
        Assert.Contains("LIVEHUB-SEG|basic-a|1080|", Encoding.ASCII.GetString(seg.Data), StringComparison.Ordinal);
        Assert.True(session.Bytes > 0);

        // Same request again: served from the shared segment cache.
        Assert.Same(seg.Data, (await h.Relay.GetSegmentAsync(session, ids[^1], CancellationToken.None)).Data);
    }

    [Fact]
    public async Task VariantSelectorPicksTheLowQuality()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        var ev = AddEvent(h, "Relay Low", "low-a");
        var session = h.Relay.GetSession(Claims(ev, variant: "low"));
        var master = await h.Relay.GetMasterAsync(session, CancellationToken.None);
        Assert.Contains("RESOLUTION=854x480", master, StringComparison.Ordinal);
        var media = await h.Relay.GetMediaPlaylistAsync(session, "v0", CancellationToken.None);
        var seg = await h.Relay.GetSegmentAsync(session, SegmentIds(media)[0], CancellationToken.None);
        Assert.Contains("|480|", Encoding.ASCII.GetString(seg.Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MediaPlaylistOriginsGetASyntheticMaster()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        var ev = AddEvent(h, "Relay Single", "single-a");
        var session = h.Relay.GetSession(Claims(ev));
        var master = await h.Relay.GetMasterAsync(session, CancellationToken.None);
        Assert.StartsWith("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-STREAM-INF:BANDWIDTH=", master, StringComparison.Ordinal);
        var media = await h.Relay.GetMediaPlaylistAsync(session, "v0", CancellationToken.None);
        var seg = await h.Relay.GetSegmentAsync(session, SegmentIds(media)[0], CancellationToken.None);
        Assert.Contains("|single-a|single|", Encoding.ASCII.GetString(seg.Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForbiddenSourcesAreSkipped()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        var ev = AddEvent(h, "Relay Forbidden", "forbidden-a", "good-b");
        var session = h.Relay.GetSession(Claims(ev));
        await h.Relay.GetMasterAsync(session, CancellationToken.None);
        Assert.Equal(1, session.CandidateIndex);
        var media = await h.Relay.GetMediaPlaylistAsync(session, "v0", CancellationToken.None);
        var seg = await h.Relay.GetSegmentAsync(session, SegmentIds(media)[0], CancellationToken.None);
        Assert.Contains("|good-b|", Encoding.ASCII.GetString(seg.Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StalledSourcesFailOverWithADiscontinuity()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var h = new Harness(_mock, clock);
        h.AddAllMockDefinitions();
        var ev = AddEvent(h, "Relay Stall", "dead-a", "live-b");
        var session = h.Relay.GetSession(Claims(ev));
        await h.Relay.GetMasterAsync(session, CancellationToken.None);
        var first = await h.Relay.GetMediaPlaylistAsync(session, "v0", CancellationToken.None);
        Assert.Equal(6, SegmentIds(first).Count);
        Assert.DoesNotContain("DISCONTINUITY", first, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromSeconds(8));
        var still = await h.Relay.GetMediaPlaylistAsync(session, "v0", CancellationToken.None);
        Assert.Equal(first, still);
        Assert.Equal(0, session.Failovers);

        clock.Advance(TimeSpan.FromSeconds(10));
        var switched = await h.Relay.GetMediaPlaylistAsync(session, "v0", CancellationToken.None);
        Assert.Equal(1, session.Failovers);
        Assert.Equal(1, session.CandidateIndex);
        Assert.Contains("#EXT-X-DISCONTINUITY\n", switched, StringComparison.Ordinal);
        Assert.True(MediaSequence(switched) >= MediaSequence(first));
        var ids = SegmentIds(switched);
        var last = await h.Relay.GetSegmentAsync(session, ids[^1], CancellationToken.None);
        Assert.Contains("|live-b|1080|", Encoding.ASCII.GetString(last.Data), StringComparison.Ordinal);

        // Output numbering continues: the old segments keep their numbers, the new ones follow.
        Assert.Equal(SegmentIds(first)[^1], ids[ids.Count - 4]);
    }

    [Fact]
    public async Task RepeatedSegmentErrorsFailOver()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        h.Config.FailoverSegmentErrors = 3;
        var ev = AddEvent(h, "Relay Broken", "brokenseg-a", "fine-b");
        var session = h.Relay.GetSession(Claims(ev));
        await h.Relay.GetMasterAsync(session, CancellationToken.None);
        var media = await h.Relay.GetMediaPlaylistAsync(session, "v0", CancellationToken.None);
        foreach (var id in SegmentIds(media).Take(3))
        {
            var ex = await Assert.ThrowsAsync<RelayException>(() => h.Relay.GetSegmentAsync(session, id, CancellationToken.None));
            Assert.Equal(502, ex.Status);
        }

        var next = await h.Relay.GetMediaPlaylistAsync(session, "v0", CancellationToken.None);
        Assert.Equal(1, session.Failovers);
        Assert.Contains("#EXT-X-DISCONTINUITY", next, StringComparison.Ordinal);
        var seg = await h.Relay.GetSegmentAsync(session, SegmentIds(next)[^1], CancellationToken.None);
        Assert.Contains("|fine-b|", Encoding.ASCII.GetString(seg.Data), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllSourcesFailingIsReported()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        var ev = AddEvent(h, "Relay None", "forbidden-x", "forbidden-y");
        var session = h.Relay.GetSession(Claims(ev));
        var ex = await Assert.ThrowsAsync<RelayException>(() => h.Relay.GetMasterAsync(session, CancellationToken.None));
        Assert.Equal(502, ex.Status);
        Assert.Equal("hint.allFailed", ex.Hint);
    }

    [Fact]
    public async Task TheRelayIsNotAnOpenProxy()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        var ev = AddEvent(h, "Relay Closed", "closed-a");
        var other = AddEventFor(h, "mock-nested", "Relay Other", "other-a");
        var session = h.Relay.GetSession(Claims(ev));
        await h.Relay.GetMasterAsync(session, CancellationToken.None);
        var ids = SegmentIds(await h.Relay.GetMediaPlaylistAsync(session, "v0", CancellationToken.None));

        // Unknown ids and ids of another session are refused.
        Assert.Equal(404, (await Assert.ThrowsAsync<RelayException>(() => h.Relay.GetSegmentAsync(session, "aGVsbG8", CancellationToken.None))).Status);
        var otherSession = h.Relay.GetSession(Claims(other));
        Assert.Equal(404, (await Assert.ThrowsAsync<RelayException>(() => h.Relay.GetSegmentAsync(otherSession, ids[0], CancellationToken.None))).Status);

        // DASH prefixes cannot escape their directory.
        var context = session.Current!;
        var prefix = session.Register(new Uri(MockServerFixture.BaseUrl + "/hls/closed-a/1080/"), context, isPrefix: true);
        Assert.Equal(403, (await Assert.ThrowsAsync<RelayException>(() => h.Relay.GetPrefixedAsync(session, prefix, "../../../api1/events.json", null, CancellationToken.None))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<RelayException>(() => h.Relay.GetPrefixedAsync(session, prefix, "http://evil.example/x", null, CancellationToken.None))).Status);
        var ok = await h.Relay.GetPrefixedAsync(session, prefix, "seg1.ts", null, CancellationToken.None);
        Assert.Equal(0x47, ok.Data[0]);

        // Tokens are required and checked.
        Assert.False(h.Relay.TryAuthorize("x.y", out _));
        var token = h.Relay.CreateToken(Claims(ev));
        Assert.True(h.Relay.TryAuthorize(token, out var claims));
        Assert.Equal(session.Key, claims.SessionKey);
        Assert.StartsWith(Harness.RelayBase + "/LiveHub/stream/" + token + "/master.m3u8", h.Relay.BuildUrl(token, RelayMode.Hls), StringComparison.Ordinal);
    }

    [Fact]
    public async Task KillSwitchGivesAClearErrorAndNeverFallsBack()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        var ev = AddEvent(h, "Relay Kill", "kill-a");
        var http = new OutboundHttp(new TestHttpClientFactory(_mock.Handler), new ClosedGate());
        var direct = new DirectResolver(http);
        var resolver = new StreamResolverService(h.Store, new FixedEgress("vpn"), direct, new PageExtractResolver(http, direct), new HeadlessResolver(http, direct, h.ConfigAccessor), NullLogger<StreamResolverService>.Instance);
        using var relay = new RelayService(http, resolver, h.Events, h.Views, h.ConfigAccessor, h.Tokens, new FixedBaseUrl(Harness.RelayBase), NullLogger<RelayService>.Instance);
        var session = relay.GetSession(Claims(ev));
        var ex = await Assert.ThrowsAsync<RelayException>(() => relay.GetMasterAsync(session, CancellationToken.None));
        Assert.Equal(503, ex.Status);
        Assert.Equal("hint.egress", ex.Hint);
        Assert.Equal(0, session.CandidateIndex);
    }

    [Fact]
    public async Task UserLimitsAndRateLimits()
    {
        using var h = new Harness(_mock);
        h.Config.RelayRequestsPerMinute = 60;
        var user = Guid.NewGuid();
        var allowed = Enumerable.Range(0, 200).Count(_ => h.Relay.CheckRate(user));
        Assert.InRange(allowed, 50, 60);
        Assert.True(h.Relay.CheckRate(Guid.NewGuid()));

        h.AddAllMockDefinitions();
        var ev = AddEvent(h, "Relay Holds", "holds-a");
        var claims = Claims(ev);
        claims.User = user.ToString("N");
        var session = h.Relay.Acquire(claims);
        Assert.Equal(1, h.Relay.HoldsOf(user));
        h.Relay.Release(session.Key, user);
        Assert.Equal(0, h.Relay.HoldsOf(user));
        await Task.CompletedTask;
    }

    [Fact]
    public void RenderKeepsKeysMapsAndDiscontinuitySequence()
    {
        var slot = new SlotState { TargetDuration = 4, Version = 6 };
        slot.Window.Add(new OutSegment(10, 4, null, "a", false, "#EXT-X-KEY:METHOD=AES-128,URI=\"../s/k\"", "#EXT-X-MAP:URI=\"../s/i\"", Array.Empty<string>()));
        slot.Window.Add(new OutSegment(11, 4.5, "t", "b", true, null, "#EXT-X-MAP:URI=\"../s/i\"", new[] { "#EXT-X-PROGRAM-DATE-TIME:2026-10-25T10:00:00Z" }));
        slot.DiscontinuitySequence = 2;
        var text = RelayService.Render(slot);
        Assert.Equal(
            "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:5\n#EXT-X-MEDIA-SEQUENCE:10\n#EXT-X-DISCONTINUITY-SEQUENCE:2\n"
            + "#EXT-X-KEY:METHOD=AES-128,URI=\"../s/k\"\n#EXT-X-MAP:URI=\"../s/i\"\n#EXTINF:4.000,\n../s/a\n"
            + "#EXT-X-DISCONTINUITY\n#EXT-X-KEY:METHOD=NONE\n#EXT-X-PROGRAM-DATE-TIME:2026-10-25T10:00:00Z\n#EXTINF:4.500,t\n../s/b\n",
            text);
    }
}
