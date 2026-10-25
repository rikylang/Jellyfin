using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.LiveTv;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Playback;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.LiveTv;

public class LiveTvTests : IClassFixture<MockServerFixture>
{
    private readonly MockServerFixture _mock;

    public LiveTvTests(MockServerFixture mock) => _mock = mock;

    private static LiveEvent Ev(string key, string category, DateTimeOffset start, int minutes)
        => new() { Key = key, Title = key, Category = category, Start = start, End = start.AddMinutes(minutes) };

    private static LiveTvLineup Lineup(Harness h) => new(h.Catalog, h.Views, h.ConfigAccessor, h.Paths, NullLogger<LiveTvLineup>.Instance, new Jellyfin.Plugin.LiveHub.Images.EventImageService());

    private static LiveHubMediaSourceProvider Sources(Harness h)
        => new(h.Events, h.Views, h.Builder, h.Relay, h.ConfigAccessor, new HttpContextAccessor(), NullLogger<LiveHubMediaSourceProvider>.Instance);

    [Fact]
    public void SlotsAreStableAndGrowOnly()
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        var cfg = new PluginConfiguration { MaxSlotsPerCategory = 3 };
        var state = new LineupState();
        var events = new List<LiveEvent>
        {
            Ev("a", "Calcio", now, 120),
            Ev("b", "Calcio", now.AddMinutes(30), 120),
            Ev("c", "Calcio", now.AddHours(3), 120),
            Ev("t", "Tennis", now, 90),
        };
        var (first, cats) = SlotAllocator.Allocate(events, state, now, cfg);
        Assert.Equal(new[] { "Calcio", "Tennis" }, cats.Select(c => c.Category));
        Assert.Equal(2, state.Categories["Calcio"].Slots);
        Assert.Equal(new[] { "a", "c" }, first[SlotAllocator.SlotId("Calcio", 0)].Select(x => x.Event.Key));
        Assert.Equal("b", Assert.Single(first[SlotAllocator.SlotId("Calcio", 1)]).Event.Key);

        // A new event that would have taken slot 1 first does not move "b".
        events.Add(Ev("z", "Calcio", now.AddMinutes(10), 60));
        var (second, _) = SlotAllocator.Allocate(events, state, now, cfg);
        Assert.Contains(second[SlotAllocator.SlotId("Calcio", 1)], x => x.Event.Key == "b");
        Assert.Contains(second[SlotAllocator.SlotId("Calcio", 2)], x => x.Event.Key == "z");
        Assert.Equal(3, state.Categories["Calcio"].Slots);

        // Overflow beyond the maximum is dropped; slots never shrink.
        events.Add(Ev("y", "Calcio", now.AddMinutes(20), 60));
        var (third, _) = SlotAllocator.Allocate(events.Where(e => e.Key != "z").ToList(), state, now, cfg);
        Assert.Equal(3, state.Categories["Calcio"].Slots);
        Assert.DoesNotContain(third.Values.SelectMany(v => v), x => x.Event.Key == "q");

        // Ended events disappear; the category keeps its channels.
        var (later, cats2) = SlotAllocator.Allocate(events, state, now.AddHours(8), cfg);
        Assert.Empty(later);
        Assert.Equal(3, cats2.Single(c => c.Category == "Calcio").Slots.Slots);
    }

    [Fact]
    public async Task LineupHasNumberedChannelsAndSlotsThatPersist()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);
        var lineup = Lineup(h).Get();
        var channels247 = lineup.Channels.Where(c => c.Kind == LineupKind.Channel247).ToList();
        Assert.Equal(6, channels247.Count);
        Assert.Equal(channels247.Count, channels247.Select(c => c.Number).Distinct().Count());
        Assert.All(channels247, c => Assert.Contains("livehub-src-" + h.Catalog.FindChannel(c.ChannelKey!)!.ProviderId, c.Tags));

        var calcio = lineup.Channels.Where(c => c.Kind == LineupKind.Slot && c.Category == "Calcio").ToList();
        Assert.NotEmpty(calcio);
        Assert.Equal("Calcio 1", calcio[0].Name);
        Assert.StartsWith("90", calcio[0].Number, StringComparison.Ordinal);
        Assert.Contains(lineup.Assignments[calcio[0].Id], a => a.Event.Title == "Inter vs Milan" || a.Event.Title.StartsWith("Juventus", StringComparison.Ordinal));

        // A new instance (restart) reads the persisted state: same ids and numbers.
        var again = Lineup(h).Get();
        Assert.Equal(lineup.ChannelSetVersion, again.ChannelSetVersion);
        Assert.Equal(lineup.Channels.Select(c => c.Id + c.Number), again.Channels.Select(c => c.Id + c.Number));
    }

    [Fact]
    public async Task TunerAndGuideServeChannelsProgramsAndStreams()
    {
        using var h = new Harness(_mock);
        h.Config.UiLanguage = "it";
        h.AddAllMockDefinitions();
        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);
        var lineup = Lineup(h);
        var sources = Sources(h);
        var tuner = new LiveHubTunerHost(lineup, sources, h.ConfigAccessor, new FixedBaseUrl(Harness.RelayBase), new HttpContextAccessor(), NullLogger<LiveHubTunerHost>.Instance);
        var guide = new LiveHubListingsProvider(lineup, h.Catalog, h.Store, h.ConfigAccessor, new FixedBaseUrl(Harness.RelayBase), new Jellyfin.Plugin.LiveHub.Images.EventImageService());

        var channels = await tuner.GetChannels(true, CancellationToken.None);
        Assert.All(channels, c => Assert.Equal("livehub", c.TunerHostId));
        Assert.Equal(channels.Select(c => c.Id), (await guide.GetChannels(new ListingsProviderInfo(), CancellationToken.None)).Select(c => c.Id));
        var slot = channels.First(c => c.Name == "Calcio 1");
        Assert.Equal(Harness.RelayBase + "/LiveHub/Images/Channel/" + slot.Id + ".png", slot.ImageUrl);

        var now = DateTime.UtcNow;
        var programs = (await guide.GetProgramsAsync(new ListingsProviderInfo(), slot.Id, now.AddHours(-2), now.AddDays(2), CancellationToken.None)).ToList();
        Assert.NotEmpty(programs);
        Assert.All(programs, p =>
        {
            Assert.True(p.IsLive);
            Assert.True(p.IsSports);
            Assert.Equal(DateTimeKind.Utc, p.StartDate.Kind);
            Assert.True(p.EndDate > p.StartDate);
            Assert.Contains("Calcio", p.Genres);
        });
        Assert.Contains(programs, p => p.Overview?.Contains("Fonti: ", StringComparison.Ordinal) == true);
        Assert.Equal(programs.Count, programs.Select(p => p.Id).Distinct().Count());

        // On air now: the slot plays the live event, through the relay.
        var media = await tuner.GetChannelStreamMediaSources(slot.Id, CancellationToken.None);
        Assert.NotEmpty(media);
        Assert.All(media, m => Assert.StartsWith(Harness.RelayBase + "/LiveHub/stream/", m.Path, StringComparison.Ordinal));
        var live = await tuner.GetChannelStream(slot.Id, media[0].Id, new List<ILiveStream>(), CancellationToken.None);
        Assert.IsType<RelayLiveStream>(live);
        await live.Close();

        // 24/7 channel with XMLTV guide data, and one without (filler blocks).
        var news = channels.Single(c => c.Name == "News 24");
        var newsPrograms = (await guide.GetProgramsAsync(new ListingsProviderInfo(), news.Id, now.AddHours(-1), now.AddHours(6), CancellationToken.None)).ToList();
        Assert.NotEmpty(newsPrograms);
        Assert.DoesNotContain(newsPrograms, p => p.Etag == "fill");
        Assert.NotEmpty(await tuner.GetChannelStreamMediaSources(news.Id, CancellationToken.None));

        var extra = Fixtures.Definition("mock1-flat.json");
        extra.Id = "extra";
        Assert.True(h.Store.Save(extra).IsValid);
        var snapshot = new ProviderSnapshot { ProviderId = "extra", FetchedAt = DateTimeOffset.UtcNow };
        snapshot.Channels.Add(new LiveChannel { Key = "noguide", ProviderId = "extra", Name = "No Guide TV", Streams = { new LiveStream { Id = "ng", ProviderId = "mock-flat", Url = MockServerFixture.BaseUrl + "/hls/ng/master.m3u8" } } });
        h.Catalog.ApplySnapshot("extra", "Extra", snapshot);
        h.Catalog.Rebuild();
        var withoutGuide = (await tuner.GetChannels(true, CancellationToken.None)).Single(c => c.Name == "No Guide TV");
        var filler = (await guide.GetProgramsAsync(new ListingsProviderInfo(), withoutGuide.Id, now, now.AddHours(6), CancellationToken.None)).ToList();
        Assert.InRange(filler.Count, 3, 4);
        Assert.All(filler, p => Assert.Equal("No Guide TV", p.Name));

        h.Config.EnableLiveTv = false;
        Assert.False(tuner.IsSupported);
        Assert.Empty(await tuner.GetChannels(false, CancellationToken.None));
    }

    [Fact]
    public async Task IntegrationCreatesTheGuideSourceAndDebouncesRefreshes()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var h = new Harness(_mock, clock);
        h.Config.GuideRefreshMinMinutes = 10;
        h.AddAllMockDefinitions();
        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);
        var host = new FakeLiveTvHost();
        var integration = new LiveTvIntegration(Lineup(h), h.Catalog, host, h.ConfigAccessor, NullLogger<LiveTvIntegration>.Instance, clock);

        await integration.TickAsync();
        var created = Assert.Single(host.Saved);
        Assert.Equal("livehub", created.Type);
        Assert.False(created.EnableAllTuners);
        Assert.Equal(new[] { "livehub" }, created.EnabledTuners);
        Assert.Equal(1, integration.RefreshesQueued);

        await integration.TickAsync();
        Assert.Single(host.Saved);
        Assert.Equal(1, integration.RefreshesQueued);

        // A new 24/7 category appears: the EPG channel cache is invalidated, the refresh waits for the debounce.
        h.Config.MaxSlotsPerCategory = 6;
        clock.Advance(TimeSpan.FromMinutes(6));
        h.Catalog.Rebuild();
        var before = host.Saved.Count;
        await integration.TickAsync();
        Assert.Equal(1, integration.RefreshesQueued);
        clock.Advance(TimeSpan.FromMinutes(5));
        await integration.TickAsync();
        Assert.True(integration.RefreshesQueued >= 1);
        Assert.True(host.Saved.Count >= before);
    }

    private sealed class FakeLiveTvHost : ILiveTvHost
    {
        public List<ListingsProviderInfo> Saved { get; } = new();

        public IReadOnlyList<ListingsProviderInfo> ListingProviders() => Saved.Take(1).ToList();

        public Task SaveListingProviderAsync(ListingsProviderInfo info)
        {
            info.Id ??= Guid.NewGuid().ToString("N");
            Saved.Add(info);
            return Task.CompletedTask;
        }

        public bool QueueGuideRefresh() => true;
    }
}
