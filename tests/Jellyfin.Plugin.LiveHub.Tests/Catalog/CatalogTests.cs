using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Sync;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Catalog;

public class CatalogTests : IClassFixture<MockServerFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 18, 0, 0, TimeSpan.Zero);
    private readonly MockServerFixture _mock;

    public CatalogTests(MockServerFixture mock) => _mock = mock;

    private static PluginConfiguration Cfg()
    {
        var c = new PluginConfiguration();
        c.EnsureDefaults();
        return c;
    }

    private static LiveEvent Ev(string provider, string id, string title, DateTimeOffset start, int priority = 100, string? category = "Calcio", params string[] streamUrls)
    {
        var ev = new LiveEvent
        {
            ProviderId = provider,
            ExternalId = id,
            Key = Keys.Event(provider, id),
            Title = title,
            Category = category,
            Start = start,
            Priority = priority,
            Participants = ItemMapper.ParticipantsFromTitle(title),
        };
        ev.SourceProviders.Add(provider);
        ev.MergedRefs.Add(provider + "\u001f" + id);
        foreach (var url in streamUrls)
        {
            ev.Streams.Add(new LiveStream { Id = Keys.Stream(provider, id, url), ProviderId = provider, Url = url, Priority = priority });
        }

        return ev;
    }

    [Fact]
    public void StatusFollowsFlagsTimeAndCategoryDurations()
    {
        var cfg = Cfg();
        var football = Ev("p", "1", "A vs B", Now.AddMinutes(-30));
        Assert.Equal(EventStatus.Live, EventStatusCalculator.Status(football, Now, cfg));
        Assert.Equal(115, EventStatusCalculator.DurationMinutes(football, cfg));
        Assert.Equal(EventStatus.Ended, EventStatusCalculator.Status(football, Now.AddMinutes(120), cfg));

        var tennis = Ev("p", "2", "Sinner vs Alcaraz", Now.AddMinutes(-150), category: "Tennis");
        Assert.Equal(EventStatus.Live, EventStatusCalculator.Status(tennis, Now, cfg));

        var upcoming = Ev("p", "3", "C vs D", Now.AddMinutes(10));
        Assert.Equal(EventStatus.Upcoming, EventStatusCalculator.Status(upcoming, Now, cfg));
        Assert.Equal(DisplayBucket.Imminent, EventStatusCalculator.Bucket(upcoming, Now, cfg));
        Assert.Equal(DisplayBucket.Soon, EventStatusCalculator.Bucket(upcoming, Now.AddMinutes(-30), cfg));
        Assert.Equal(DisplayBucket.Later, EventStatusCalculator.Bucket(upcoming, Now.AddHours(-3), cfg));

        var flaggedLive = Ev("p", "4", "E vs F", Now.AddMinutes(20));
        flaggedLive.IsLiveFlag = true;
        Assert.Equal(EventStatus.Live, EventStatusCalculator.Status(flaggedLive, Now, cfg));

        var ended = Ev("p", "5", "G vs H", Now.AddMinutes(-10));
        ended.StatusFlag = EventStatus.Ended;
        Assert.Equal(EventStatus.Ended, EventStatusCalculator.Status(ended, Now, cfg));
    }

    [Fact]
    public void ExpiredEventsAreRemovedAfterTheGracePeriod()
    {
        var cfg = Cfg();
        var ev = Ev("p", "1", "A vs B", Now.AddMinutes(-115 - 5));
        Assert.False(EventStatusCalculator.IsExpired(ev, Now, cfg));
        Assert.True(EventStatusCalculator.IsExpired(ev, Now.AddMinutes(10), cfg));
        Assert.True(EventStatusCalculator.IsExpired(Ev("p", "2", "Far", Now.AddDays(30)), Now, cfg));
    }

    [Fact]
    public void DeduplicationMergesSameEventAcrossProvidersAndUnitesStreams()
    {
        var aliases = new Dictionary<string, string>();
        var a = Ev("alpha", "1", "Inter - Milan", Now, 100, "Calcio", "https://a/1.m3u8");
        var b = Ev("beta", "x", "FC Internazionale vs AC Milan", Now.AddMinutes(5), 80, "Calcio", "https://b/1.m3u8", "https://a/1.m3u8");
        b.Participants = new List<Participant> { new() { Name = "Inter" }, new() { Name = "Milan", LogoUrl = "https://logo/milan.png" } };
        b.ThumbUrl = "https://img/b.png";
        var c = Ev("gamma", "y", "Milan vs Inter", Now.AddMinutes(40), 50, "Calcio", "https://c/1.m3u8");
        var d = Ev("alpha", "2", "Roma - Lazio", Now, 100, "Calcio", "https://a/2.m3u8");

        var merged = EventDeduplicator.Merge(new[] { b, d, a, c }, TimeSpan.FromMinutes(15), aliases);
        Assert.Equal(3, merged.Count);
        var derby = merged.Single(e => e.Key == a.Key);
        Assert.Equal("Inter - Milan", derby.Title);
        Assert.Equal(new[] { "https://a/1.m3u8", "https://b/1.m3u8" }, derby.Streams.Select(s => s.Url));
        Assert.Equal("https://img/b.png", derby.ThumbUrl);
        Assert.Equal("https://logo/milan.png", derby.Participants[1].LogoUrl);
        Assert.Equal(new[] { "alpha", "beta" }, derby.SourceProviders);
        Assert.Equal(a.Key, aliases[b.Key]);
        Assert.DoesNotContain(c.Key, aliases.Keys);
    }

    [Fact]
    public void NormalizationIgnoresAccentsCaseAndClubTokens()
    {
        Assert.Equal("atletico madrid", EventDeduplicator.Normalize("Atlético Madrid"));
        Assert.Equal("inter", EventDeduplicator.Normalize("FC Inter"));
        Assert.Equal(EventDeduplicator.MatchKey(Ev("p", "1", "Inter vs Milan", Now)), EventDeduplicator.MatchKey(Ev("q", "2", "AC Milan - Inter", Now)));
    }

    [Fact]
    public void CatalogKeepsLastGoodDataWhenAnEndpointFailsAndRaisesChanges()
    {
        var clock = new ManualClock(Now);
        var cfg = Cfg();
        var catalog = new CatalogService(new ConfigAccessor(() => cfg), NullLogger<CatalogService>.Instance, clock);
        var changes = new List<bool>();
        catalog.Changed += (_, e) => changes.Add(e.DataChanged);

        var ok = new EndpointResult { EndpointId = "all", Success = true };
        ok.Events.Add(Ev("p", "1", "A vs B", Now.AddMinutes(20), streamUrls: "https://x/1.m3u8"));
        catalog.ApplyEndpointResults("p", "P", new[] { ok });
        catalog.Rebuild();
        Assert.Single(catalog.View.Events);
        Assert.Equal(new[] { true }, changes);

        // A failed run does not erase the data.
        catalog.ApplyEndpointResults("p", "P", new[] { new EndpointResult { EndpointId = "all", Success = false, Error = "HTTP 500" } });
        catalog.Rebuild();
        Assert.Single(catalog.View.Events);
        Assert.Single(changes);

        // Time passes: the status bucket changes (upcoming → live) without new data.
        clock.Advance(TimeSpan.FromMinutes(25));
        catalog.Tick();
        Assert.Equal(new[] { true, false }, changes);

        // Later the event expires.
        clock.Advance(TimeSpan.FromHours(3));
        catalog.Tick();
        Assert.Empty(catalog.View.Events);
    }

    [Fact]
    public void SnapshotsRoundTripAndServeAsFallback()
    {
        using var h = new Harness(_mock);
        var snapshot = new ProviderSnapshot { ProviderId = "p", FetchedAt = DateTimeOffset.UtcNow };
        snapshot.Events.Add(Ev("p", "1", "A vs B", DateTimeOffset.UtcNow.AddMinutes(30), streamUrls: "https://x/1.m3u8"));
        snapshot.Channels.Add(new LiveChannel { Key = "c1", ProviderId = "p", Name = "News", Streams = { new LiveStream { Id = "s1", Url = "https://x/n.m3u8", Kind = StreamKind.Hls } } });
        h.Snapshots.Save(snapshot, force: true);

        var loaded = h.Snapshots.Load("p");
        Assert.NotNull(loaded);
        Assert.Equal("A vs B", loaded!.Events[0].Title);
        Assert.Equal(StreamKind.Hls, loaded.Channels[0].Streams[0].Kind);

        h.Catalog.ApplySnapshot("p", "P", loaded);
        h.Catalog.Rebuild();
        Assert.True(h.Catalog.IsFromSnapshot("p"));
        Assert.Single(h.Catalog.View.Events);
        Assert.Single(h.Catalog.View.Channels);
    }

    [Fact]
    public async Task SchedulerSyncsAllSixMockApisIntoOneDeduplicatedCatalog()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);

        var view = h.Catalog.View;
        Assert.Equal(6, view.Channels.Count);
        Assert.NotEmpty(view.Programs);

        // mock1 (3 not expired) + mock2 (5) + mock3 (2) + mock4 (2), with "Inter vs Milan" from mock1 and mock2 merged.
        Assert.Equal(11, view.Events.Count);
        var derby = view.Events.Single(e => e.Title.Contains("Inter", StringComparison.Ordinal) && e.Title.Contains("Milan", StringComparison.Ordinal));
        Assert.Equal(new[] { "mock-flat", "mock-nested" }, derby.SourceProviders);
        Assert.Equal(3, derby.Streams.Count);
        Assert.DoesNotContain(view.Events, e => e.Title == "Old Match");
        Assert.True(view.Events.Single(e => e.ExternalId == "m1").HasLazyStreams);

        foreach (var id in new[] { "mock-flat", "mock-nested", "mock-multilevel", "mock-embed", "mock-m3u", "mock-xtream" })
        {
            var stats = h.Engine.GetGateway(id).Stats;
            Assert.NotNull(stats.LastSuccessAt);
            Assert.Null(stats.LastError);
        }

        Assert.True(File.Exists(Path.Combine(h.Paths.SnapshotsDir, "mock-flat.json")));
    }

    [Fact]
    public async Task ABrokenProviderDoesNotAffectTheOthers()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        var broken = Fixtures.Definition("mock2-nested.json");
        broken.Id = "broken";
        broken.Secrets!["apiKey"] = "wrong";
        Assert.True(h.Store.Save(broken).IsValid);

        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);
        Assert.NotNull(h.Engine.GetGateway("broken").Stats.LastError);
        Assert.Equal(1, h.Engine.GetGateway("broken").Stats.ConsecutiveFailures);
        Assert.Equal(11, h.Catalog.View.Events.Count);
    }

    [Fact]
    public void IntervalsFollowScopeDefaultsAndAdaptivePolling()
    {
        var cfg = Cfg();
        var def = new ProviderDefinition { Id = "x", Name = "x", Kind = ProviderKind.Rest };
        Assert.Equal(TimeSpan.FromSeconds(45), SyncScheduler.Interval(def, new EndpointSpec { Scope = EndpointScope.Live }, true, cfg));
        Assert.Equal(TimeSpan.FromSeconds(135), SyncScheduler.Interval(def, new EndpointSpec { Scope = EndpointScope.Live }, false, cfg));
        Assert.Equal(TimeSpan.FromSeconds(300), SyncScheduler.Interval(def, new EndpointSpec { Scope = EndpointScope.Today }, false, cfg));
        Assert.Equal(TimeSpan.FromSeconds(1800), SyncScheduler.Interval(def, new EndpointSpec { Scope = EndpointScope.Upcoming }, true, cfg));
        Assert.Equal(TimeSpan.FromSeconds(45), SyncScheduler.Interval(def, new EndpointSpec { Scope = EndpointScope.All }, true, cfg));
        Assert.Equal(TimeSpan.FromSeconds(20), SyncScheduler.Interval(def, new EndpointSpec { RefreshSeconds = 20 }, false, cfg));
        def.Refresh = new RefreshSpec { LiveSeconds = 30, Adaptive = false };
        Assert.Equal(TimeSpan.FromSeconds(30), SyncScheduler.Interval(def, new EndpointSpec { Scope = EndpointScope.Live }, false, cfg));
        var m3u = new ProviderDefinition { Id = "m", Name = "m", Kind = ProviderKind.M3u, M3u = new M3uSpec { Url = "https://a/b.m3u", RefreshMinutes = 60 } };
        Assert.Equal(TimeSpan.FromMinutes(60), SyncScheduler.Interval(m3u, null, false, cfg));
    }

    [Fact]
    public void BackoffIsExponentialWithJitterAndCapped()
    {
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var d = Backoff.Delay(attempt, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(15));
            var cap = Math.Min(15 * Math.Pow(2, attempt - 1), 900);
            Assert.InRange(d.TotalSeconds, cap / 2 - 0.01, cap + 0.01);
        }
    }
}
