using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using LiveHub.MockServer;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Relay;

public class ResolverTests : IClassFixture<MockServerFixture>
{
    private readonly MockServerFixture _mock;

    public ResolverTests(MockServerFixture mock) => _mock = mock;

    private Harness Create()
    {
        var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        return h;
    }

    [Theory]
    [InlineData("e1")]
    [InlineData("e2")]
    public async Task ExtractsStreamsFromEmbedPages(string id)
    {
        using var h = Create();
        var stream = new LiveStream { Id = "x" + id, ProviderId = "mock-embed", Url = MockServerFixture.BaseUrl + "/embed/" + id, Kind = StreamKind.Embed };
        var steps = new List<string>();
        var resolved = await h.Resolver.ResolveAsync(stream, null, false, steps, CancellationToken.None);
        Assert.Equal(StreamKind.Hls, resolved.Kind);
        Assert.Equal("extract", resolved.Strategy);
        Assert.True(resolved.FromEmbed);
        Assert.EndsWith("/hls/embed-" + id + "/master.m3u8", resolved.Url.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(2, resolved.Variants.Count);
        Assert.Equal(MockServerFixture.BaseUrl + "/player/" + id, resolved.Headers["Referer"]);
        Assert.Contains(steps, s => s.Contains("1 frame(s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectStreamsAreValidatedAndCached()
    {
        using var h = Create();
        var stream = new LiveStream { Id = "d1", ProviderId = "mock-flat", Url = MockServerFixture.BaseUrl + "/hls/test1/master.m3u8", Kind = StreamKind.Hls };
        var first = await h.Resolver.ResolveAsync(stream, false, CancellationToken.None);
        Assert.Equal("direct", first.Strategy);
        Assert.Equal(1080, first.Variants[0].Height);
        Assert.Same(first, await h.Resolver.ResolveAsync(stream, false, CancellationToken.None));
        Assert.Same(first, h.Resolver.Peek(stream));
        h.Resolver.Invalidate(stream);
        Assert.Null(h.Resolver.Peek(stream));
        Assert.NotSame(first, await h.Resolver.ResolveAsync(stream, false, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentResolutionsShareOneRequest()
    {
        using var h = Create();
        var stream = new LiveStream { Id = "c1", ProviderId = "mock-flat", Url = MockServerFixture.BaseUrl + "/hls/conc/master.m3u8", Kind = StreamKind.Hls };
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Resolver.ResolveAsync(stream, false, CancellationToken.None)));
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact]
    public async Task ForbiddenOriginsAreDiagnosed()
    {
        using var h = Create();
        var stream = new LiveStream { Id = "f1", ProviderId = "mock-flat", Url = MockServerFixture.BaseUrl + "/hls/forbidden-1/master.m3u8", Kind = StreamKind.Hls };
        var ex = await Assert.ThrowsAsync<ResolveException>(() => h.Resolver.ResolveAsync(stream, false, CancellationToken.None));
        Assert.Equal(403, ex.StatusCode);
        Assert.Equal("hint.forbidden", ex.Hint);
    }

    [Fact]
    public async Task RequiredHeadersAreSent()
    {
        using var h = Create();
        var url = MockServerFixture.BaseUrl + "/hls/referer-9/master.m3u8";
        var without = new LiveStream { Id = "r0", ProviderId = "mock-multilevel", Url = url, Kind = StreamKind.Hls };
        await Assert.ThrowsAsync<ResolveException>(() => h.Resolver.ResolveAsync(without, false, CancellationToken.None));
        var with = new LiveStream { Id = "r1", ProviderId = "mock-multilevel", Url = url, Kind = StreamKind.Hls };
        with.Headers["Referer"] = MockServerApp.RequiredReferer;
        var resolved = await h.Resolver.ResolveAsync(with, false, CancellationToken.None);
        Assert.Equal(MockServerApp.RequiredReferer, resolved.Headers["Referer"]);
    }

    [Theory]
    [InlineData("http://localhost/hls/x/master.m3u8")]
    [InlineData("http://127.0.0.1:8096/System/Info")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.1/playlist.m3u8")]
    [InlineData("http://[::1]/x.m3u8")]
    public async Task PrivateDestinationsAreBlockedWithoutOptIn(string url)
    {
        using var h = Create();
        var stream = new LiveStream { Id = "s", ProviderId = string.Empty, Url = url, Kind = StreamKind.Hls };
        var ex = await Assert.ThrowsAsync<ResolveException>(() => h.Resolver.ResolveAsync(stream, false, CancellationToken.None));
        Assert.Equal("hint.ssrf", ex.Hint);
    }

    [Fact]
    public async Task OnlyHttpSchemesAreAccepted()
    {
        using var h = Create();
        var stream = new LiveStream { Id = "s", ProviderId = "mock-flat", Url = "file:///etc/passwd", Kind = StreamKind.Hls };
        await Assert.ThrowsAnyAsync<Exception>(() => h.Resolver.ResolveAsync(stream, false, CancellationToken.None));
    }

    [Fact]
    public async Task LazyStreamChainsAreLoadedOnDemandAndCached()
    {
        using var h = Create();
        await h.Scheduler.SyncAllAsync(null, CancellationToken.None);
        var juve = h.Catalog.View.Events.Single(e => e.Title.StartsWith("Juventus", StringComparison.Ordinal));
        Assert.True(juve.HasLazyStreams);
        Assert.Null(h.Catalog.GetLazyStreams(juve.Key));
        var streams = await h.Events.GetStreamsAsync(juve, CancellationToken.None);
        Assert.True(streams.Count >= 2);
        Assert.Contains(streams, s => s.Headers.ContainsKey("Referer"));
        Assert.NotNull(h.Catalog.GetLazyStreams(juve.Key));
    }
}
