using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Images;
using Jellyfin.Plugin.LiveHub.Localization;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Jellyfin.Plugin.LiveHub.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Users;

public class UserFeatureTests : IClassFixture<MockServerFixture>
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly MockServerFixture _mock;

    public UserFeatureTests(MockServerFixture mock) => _mock = mock;

    private sealed class FakeDirectory : IUserDirectory
    {
        public IReadOnlyList<DirectoryUser> Users() => new[] { new DirectoryUser(Alice, "alice"), new DirectoryUser(Bob, "bob") };
    }

    private sealed class FakeSink : ISessionSink
    {
        public List<ActiveSession> Active { get; } = new();

        public List<(string Session, string Text)> Sent { get; } = new();

        public IReadOnlyList<ActiveSession> Sessions() => Active;

        public Task SendAsync(string sessionId, string header, string text, long timeoutMs, CancellationToken cancellationToken)
        {
            Sent.Add((sessionId, text));
            return Task.CompletedTask;
        }
    }

    private async Task<Harness> SyncedAsync()
    {
        var h = new Harness(_mock);
        h.Config.UiLanguage = "it";
        h.AddAllMockDefinitions();
        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);
        return h;
    }

    private static UserSettingsService Settings(Harness h) => new(h.Prefs, h.Views, h.Catalog, h.Store, h.ConfigAccessor, new FakeDirectory(), h.Access);

    [Fact]
    public async Task PreferencesPageOffersOnlyAllowedSourcesAndKeepsChoices()
    {
        using var h = await SyncedAsync();
        var settings = Settings(h);
        settings.SaveAccess(new[]
        {
            new UserAccessDto { Id = Bob, Restricted = true, AllowedProviders = new List<string> { "mock-nested", "unknown" } },
            new UserAccessDto { Id = Guid.NewGuid(), Restricted = true },
        });
        var rule = Assert.Single(h.Config.UserAccess);
        Assert.Equal("mock-nested", rule.AllowedProviders);

        var access = settings.GetAccess();
        Assert.Equal(new[] { "alice", "bob" }, access.Select(a => a.Name));
        Assert.True(access[1].Restricted);
        Assert.Equal(new[] { "mock-nested" }, access[1].AllowedProviders);

        var bobPage = settings.GetPage(Bob);
        Assert.Equal(new[] { "mock-nested" }, bobPage.Providers.Select(p => p.Id));
        Assert.Equal(6, settings.GetPage(Alice).Providers.Count);
        Assert.Contains(bobPage.Languages, l => l.Id == "it" && l.Name == "Italiano");
        Assert.Contains("Calcio", bobPage.Categories);
        Assert.NotEmpty(bobPage.TimeZones);

        var saved = settings.Save(Bob, new UserPreferences { Sources = { "mock-nested", "mock-flat" }, FavoriteTeams = { " Inter ", "inter", "" }, Language = "Italiano" });
        Assert.Equal(new[] { "mock-nested" }, saved.Sources);
        Assert.Equal(new[] { "Inter" }, saved.FavoriteTeams);
        Assert.Equal("it", saved.Language);
        Assert.Equal(1, saved.Version);
    }

    [Fact]
    public async Task NotificationsReachIdleSessionsOnlyOnce()
    {
        using var h = await SyncedAsync();
        var sink = new FakeSink();
        sink.Active.Add(new ActiveSession("tv", Alice, Playing: false, CanDisplayMessage: true));
        sink.Active.Add(new ActiveSession("phone", Alice, Playing: true, CanDisplayMessage: true));
        sink.Active.Add(new ActiveSession("old", Alice, Playing: false, CanDisplayMessage: false));
        sink.Active.Add(new ActiveSession("bob-tv", Bob, Playing: false, CanDisplayMessage: true));
        h.Prefs.Save(Alice, new UserPreferences { FavoriteTeams = { "Inter" }, FavoriteCategories = { "Tennis" }, Notifications = new NotificationPreferences { LeadMinutes = 0 } });
        var service = new NotificationService(h.Catalog, h.Prefs, h.Views, sink, new LocalizationService(), h.ConfigAccessor, NullLogger<NotificationService>.Instance);

        var sent = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, sent);
        var (session, text) = Assert.Single(sink.Sent);
        Assert.Equal("tv", session);
        Assert.StartsWith("IN DIRETTA: Inter vs Milan", text, StringComparison.Ordinal);

        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None));

        // Cycling starts in about an hour: with a long lead time it is announced in advance.
        h.Prefs.Save(Alice, new UserPreferences { FavoriteTeams = { "Inter" }, FavoriteCategories = { "Ciclismo" }, Notifications = new NotificationPreferences { LeadMinutes = 120 } });
        await service.RunOnceAsync(CancellationToken.None);
        Assert.Contains(sink.Sent, s => s.Text.StartsWith("Tra ", StringComparison.Ordinal) && s.Text.Contains("Tour Stage 5", StringComparison.Ordinal));

        h.Config.EnableNotifications = false;
        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None));
    }

    [Fact]
    public void CompositeImagesAreRenderedAsPng()
    {
        var renderer = new SkiaImageRenderer(NullLogger<SkiaImageRenderer>.Instance);
        var ev = new LiveEvent { Key = "k", Title = "Inter vs Milan", Category = "Calcio", Competition = "Serie A", Participants = { new Participant { Name = "Inter" }, new Participant { Name = "Milan" } } };
        foreach (var shape in Enum.GetValues<ImageShape>())
        {
            var png = renderer.Render(EventImageService.TextFor(ev), shape);
            Assert.NotNull(png);
            Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png!.Take(4).ToArray());
            var (w, hgt) = SkiaImageRenderer.Size(shape);
            Assert.Equal(w, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
            Assert.Equal(hgt, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        }

        Assert.NotNull(renderer.Render(new ImageText { Title = "A very long title without participants that needs wrapping" }, ImageShape.Poster));
    }

    [Fact]
    public async Task RemoteImagesGoThroughTheSignedProxyAndAreCached()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        var proxy = new ImageProxy(h.Protector, h.Http, h.Store, new FixedEgress(), h.Paths, NullLogger<ImageProxy>.Instance);
        var images = new EventImageService(new FixedBaseUrl(Harness.RelayBase), proxy, new SkiaImageRenderer(NullLogger<SkiaImageRenderer>.Instance), h.ConfigAccessor, h.Paths);
        var ev = new LiveEvent { Key = "e1", ProviderId = "mock-flat", Title = "Roma vs Lazio", ThumbUrl = MockServerFixture.BaseUrl + "/img/roma.png", Category = "Calcio" };

        var url = images.ForEvent(ev, ImageShape.Thumb)!;
        Assert.StartsWith(Harness.RelayBase + "/LiveHub/Images/Remote?u=", url, StringComparison.Ordinal);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.True(proxy.Verify(query["u"]!, query["p"]!, query["s"]!));
        Assert.False(proxy.Verify("https://evil.example/x.png", query["p"]!, query["s"]!));
        Assert.False(proxy.Verify(query["u"]!, "mock-nested", query["s"]!));

        var image = await proxy.GetAsync(query["u"]!, query["p"]!, CancellationToken.None);
        Assert.NotNull(image);
        Assert.Equal("image/png", image!.Value.ContentType);
        Assert.Single(System.IO.Directory.GetFiles(System.IO.Path.Combine(h.Paths.ImagesDir, "remote"), "*.bin"));

        // Non-image answers are refused (the proxy never relays HTML/JSON).
        Assert.Null(await proxy.GetAsync(MockServerFixture.BaseUrl + "/api1/events.json", "mock-flat", CancellationToken.None));

        // Without API images a composite is generated and cached on disk.
        var plain = new LiveEvent { Key = "e2", ProviderId = "mock-flat", Title = "Tour Stage 5", Category = "Ciclismo" };
        var generated = images.ForEvent(plain, ImageShape.Poster)!;
        Assert.StartsWith(Harness.RelayBase + "/LiveHub/Images/Event/e2/poster.png?v=", generated, StringComparison.Ordinal);
        var first = images.Render("event|e2", EventImageService.TextFor(plain), ImageShape.Poster);
        Assert.NotNull(first);
        Assert.Equal(first, images.Render("event|e2", EventImageService.TextFor(plain), ImageShape.Poster));

        h.Config.GenerateImages = false;
        Assert.Null(images.ForEvent(plain, ImageShape.Poster));
    }
}
