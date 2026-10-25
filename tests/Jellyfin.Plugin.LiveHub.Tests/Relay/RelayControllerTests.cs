using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Api;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Relay;

/// <summary>Plays a stream through the relay controller over HTTP, following relative URLs like ffmpeg does.</summary>
public class RelayControllerTests : IClassFixture<MockServerFixture>
{
    private readonly MockServerFixture _mock;

    public RelayControllerTests(MockServerFixture mock) => _mock = mock;

    [Fact]
    public async Task PlaysThroughTheRelayLikeFfmpeg()
    {
        using var h = new Harness(_mock);
        h.AddAllMockDefinitions();
        var now = DateTimeOffset.UtcNow;
        var ev = new LiveEvent { Key = "http-test", ProviderId = "mock-flat", ExternalId = "1", Title = "HTTP test", Start = now.AddMinutes(-5), IsLiveFlag = true, SourceProviders = new List<string> { "mock-flat" } };
        ev.Streams.Add(new LiveStream { Id = "s1", ProviderId = "mock-flat", Url = MockServerFixture.BaseUrl + "/hls/http-a/master.m3u8", Kind = StreamKind.Hls });
        var snapshot = new ProviderSnapshot { ProviderId = "mock-flat", FetchedAt = now };
        snapshot.Events.Add(ev);
        h.Catalog.ApplySnapshot("mock-flat", "Mock 1", snapshot);
        h.Catalog.Rebuild();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(h.Relay);
        builder.Services.AddControllers().AddApplicationPart(typeof(RelayController).Assembly);
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        using var client = app.GetTestClient();

        var token = h.Relay.CreateToken(new RelayClaims { Key = "http-test", StreamId = "s1" });
        var entry = new Uri(client.BaseAddress!, "/LiveHub/stream/" + token + "/stream");

        // Generic entry → redirect to the HLS master.
        using (var redirect = await client.GetAsync(entry))
        {
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
            Assert.Equal("master.m3u8", redirect.Headers.Location!.OriginalString);
        }

        var masterUrl = new Uri(entry, "master.m3u8");
        using var master = await client.GetAsync(masterUrl);
        Assert.Equal("application/vnd.apple.mpegurl", master.Content.Headers.ContentType!.MediaType);
        var masterText = await master.Content.ReadAsStringAsync();
        var mediaUrl = new Uri(masterUrl, masterText.Split('\n').First(l => l.Length > 0 && !l.StartsWith('#')));
        var mediaText = await client.GetStringAsync(mediaUrl);
        var segments = mediaText.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => new Uri(mediaUrl, l)).ToList();
        Assert.Equal(6, segments.Count);
        Assert.All(segments, s => Assert.StartsWith("/LiveHub/stream/" + token + "/s/", s.AbsolutePath, StringComparison.Ordinal));
        var bytes = await client.GetByteArrayAsync(segments[^1]);
        Assert.Equal(0x47, bytes[0]);

        // Security: forged tokens and unknown ids.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri(client.BaseAddress!, "/LiveHub/stream/abc.def/master.m3u8"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri(mediaUrl, "../s/notregistered"))).StatusCode);

        // An item that no longer exists gives a clear error.
        var gone = h.Relay.CreateToken(new RelayClaims { Key = "nope", StreamId = "x" });
        var goneResponse = await client.GetAsync(new Uri(client.BaseAddress!, "/LiveHub/stream/" + gone + "/master.m3u8"));
        Assert.Equal(HttpStatusCode.NotFound, goneResponse.StatusCode);
    }
}
