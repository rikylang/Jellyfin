using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Jellyfin.Plugin.LiveHub.Users;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Channels;

public class ChannelTests : IClassFixture<MockServerFixture>
{
    private readonly MockServerFixture _mock;

    public ChannelTests(MockServerFixture mock) => _mock = mock;

    private static async Task<Harness> SyncedAsync(MockServerFixture mock)
    {
        var h = new Harness(mock);
        h.Config.UiLanguage = "it";
        h.AddAllMockDefinitions();
        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);
        return h;
    }

    private static async Task<List<ChannelItemInfo>> Items(Harness h, string? folder, Guid user)
        => (await h.Channel.GetChannelItems(new InternalChannelItemQuery { FolderId = folder!, UserId = user }, CancellationToken.None)).Items.ToList();

    [Fact]
    public async Task RootHasTheEightFoldersInItalian()
    {
        using var h = await SyncedAsync(_mock);
        var root = await Items(h, null, Guid.Empty);
        Assert.Equal(new[] { "In diretta ora", "Oggi", "Prossimi", "Popolari", "Per categoria", "Per fonte", "Preferiti", "Canali 24/7" }, root.Select(i => i.Name));
        Assert.All(root, i => Assert.Equal(ChannelItemType.Folder, i.Type));
        Assert.Equal("f|live", root[0].Id);
    }

    [Fact]
    public async Task LiveFolderShowsLiveEventsWithStatusSourcesAndLanguages()
    {
        using var h = await SyncedAsync(_mock);
        var live = await Items(h, "f|live", Guid.Empty);
        Assert.Contains(live, i => i.Name == "LIVE · Inter vs Milan");
        Assert.Contains(live, i => i.Name.StartsWith("LIVE · Lakers vs Celtics", StringComparison.Ordinal));
        Assert.Contains(live, i => i.Name.StartsWith("LIVE · Juventus vs Napoli", StringComparison.Ordinal));
        Assert.DoesNotContain(live, i => i.Name.Contains("Roma", StringComparison.Ordinal));

        var derby = live.Single(i => i.Name == "LIVE · Inter vs Milan");
        Assert.True(derby.IsLiveStream);
        Assert.Equal(ChannelMediaType.Video, derby.MediaType);
        Assert.Contains("IN DIRETTA ora", derby.Overview, StringComparison.Ordinal);
        Assert.Contains("Serie A · Calcio", derby.Overview, StringComparison.Ordinal);
        Assert.Contains("Mock 1", derby.Overview, StringComparison.Ordinal);
        Assert.Contains("Mock 2", derby.Overview, StringComparison.Ordinal);
        Assert.Contains("Italiano", derby.Overview, StringComparison.Ordinal);
        Assert.Contains("Stream: 3", derby.Overview, StringComparison.Ordinal);
        Assert.Contains("livehub-src-mock-flat", derby.Tags);
        Assert.True(ChannelIds.TryParseMedia(derby.Id, out var kind, out var key));
        Assert.Equal(ItemKind.Event, kind);
        Assert.NotNull(h.Catalog.FindEvent(key));
    }

    [Fact]
    public async Task TodayUpcomingCategoriesSourcesAndChannels()
    {
        using var h = await SyncedAsync(_mock);
        var today = await Items(h, "f|today", Guid.Empty);
        Assert.Contains(today, i => System.Text.RegularExpressions.Regex.IsMatch(i.Name, @"^\d\d:\d\d · Roma vs Lazio$"));

        var upcoming = await Items(h, "f|upcoming", Guid.Empty);
        Assert.NotEmpty(upcoming);
        Assert.All(upcoming, f => Assert.Matches(@"^\d · ", f.Name));
        var day = await Items(h, upcoming[0].Id, Guid.Empty);
        Assert.NotEmpty(day);

        var cats = await Items(h, "f|categories", Guid.Empty);
        Assert.Contains(cats, c => c.Name == "Calcio");
        var calcio = await Items(h, cats.Single(c => c.Name == "Calcio").Id, Guid.Empty);
        Assert.True(calcio.Count >= 4);

        var sources = await Items(h, "f|sources", Guid.Empty);
        Assert.Equal(6, sources.Count);
        var xtream = await Items(h, "f|sources|mock-xtream", Guid.Empty);
        Assert.NotEmpty(xtream);

        var channels = await Items(h, "f|channels", Guid.Empty);
        Assert.Equal(6, channels.Count);
        Assert.All(channels, c => Assert.True(c.IsLiveStream));
        Assert.Contains(channels, c => c.Name == "News 24" && c.Overview.Contains("Canale 24/7", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItemIdsAreUniquePerFolderAndStable()
    {
        using var h = await SyncedAsync(_mock);
        var a = await Items(h, "f|live", Guid.Empty);
        var b = await Items(h, "f|live", Guid.Empty);
        Assert.Equal(a.Select(i => i.Id), b.Select(i => i.Id));
        var popular = await Items(h, "f|popular", Guid.Empty);
        Assert.Empty(a.Select(i => i.Id).Intersect(popular.Select(i => i.Id)));
    }

    [Fact]
    public async Task PerUserFiltersAndAdminRestrictions()
    {
        using var h = await SyncedAsync(_mock);
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        h.Prefs.Save(alice, new UserPreferences { Language = "it", OnlyMyLanguage = true, FallbackLanguages = new List<string> { "it" } });
        h.Prefs.Save(bob, new UserPreferences { Hidden = new List<string> { "cat:Basket" }, FavoriteTeams = new List<string> { "Juventus" } });
        h.Config.UserAccess.Add(new UserAccessRule { UserId = bob.ToString("N"), Restricted = true, AllowedProviders = "mock-nested,mock-multilevel" });

        var aliceLive = await Items(h, "f|live", alice);
        Assert.DoesNotContain(aliceLive, i => i.Name.Contains("Lakers", StringComparison.Ordinal));
        Assert.Contains(aliceLive, i => i.Name.Contains("Inter", StringComparison.Ordinal));

        var bobLive = await Items(h, "f|live", bob);
        Assert.DoesNotContain(bobLive, i => i.Name.Contains("Lakers", StringComparison.Ordinal));
        Assert.Contains(bobLive, i => i.Name.Contains("Juventus", StringComparison.Ordinal));
        Assert.DoesNotContain(bobLive, i => i.Name.Contains("Ferrari", StringComparison.Ordinal));
        var bobDerby = bobLive.Single(i => i.Name.Contains("Inter", StringComparison.Ordinal));
        Assert.Contains("Stream: 1", bobDerby.Overview, StringComparison.Ordinal);
        Assert.DoesNotContain("Mock 1", bobDerby.Overview, StringComparison.Ordinal);

        var bobFav = await Items(h, "f|favorites", bob);
        Assert.Contains(bobFav, i => i.Name.Contains("Juventus", StringComparison.Ordinal));
        Assert.Empty(await Items(h, "f|sources|mock-flat", bob));

        // Anonymous (server side) view: only providers visible to every restricted user.
        var anon = await Items(h, "f|sources", Guid.Empty);
        Assert.Equal(new[] { "mock-multilevel", "mock-nested" }, anon.Select(s => s.Id.Split('|')[2]).OrderBy(x => x));
    }

    [Fact]
    public async Task CacheKeyChangesWhenADifferentUserIsServed()
    {
        using var h = new Harness(_mock);
        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");
        var k1 = h.Channel.GetCacheKey(a);
        await h.Channel.GetChannelItems(new InternalChannelItemQuery { UserId = Guid.Parse(a) }, CancellationToken.None);
        var k2 = h.Channel.GetCacheKey(a);
        Assert.Equal(k1, k2);
        await h.Channel.GetChannelItems(new InternalChannelItemQuery { UserId = Guid.Parse(b) }, CancellationToken.None);
        Assert.NotEqual(k2, h.Channel.GetCacheKey(a));
        h.Prefs.Save(Guid.Parse(a), new UserPreferences { Language = "en" });
        Assert.NotEqual(k2, h.Channel.GetCacheKey(a));
    }

    [Fact]
    public async Task ChannelImagesAndLatestMedia()
    {
        using var h = await SyncedAsync(_mock);
        var img = await h.Channel.GetChannelImage(ImageType.Primary, CancellationToken.None);
        Assert.True(img.HasImage);
        Assert.True(img.Stream.Length > 1000);
        var latest = (await h.Channel.GetLatestMedia(new ChannelLatestMediaSearch(), CancellationToken.None)).ToList();
        Assert.Contains(latest, i => i.Name.StartsWith("LIVE · ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Real Madrid vs Barcelona — Clásico de la Liga 2026 edición especial", "Real Madrid vs Barcelona — Clásico de la…")]
    [InlineData("Inter vs Milan", "Inter vs Milan")]
    public void TitlesAreShortenedAtWordBoundaries(string input, string expected)
    {
        Assert.Equal(expected, ChannelText.Shorten(input));
    }
}
