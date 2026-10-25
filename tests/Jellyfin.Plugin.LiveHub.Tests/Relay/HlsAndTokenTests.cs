using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Security;
using Jellyfin.Plugin.LiveHub.Storage;
using Jellyfin.Plugin.LiveHub.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Relay;

public class HlsAndTokenTests
{
    private const string Master = "#EXTM3U\n#EXT-X-VERSION:4\n#EXT-X-INDEPENDENT-SEGMENTS\n"
        + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",LANGUAGE=\"it\",NAME=\"Italiano\",DEFAULT=YES,URI=\"audio/it.m3u8\"\n"
        + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",LANGUAGE=\"en\",NAME=\"English\",URI=\"https://cdn.example/audio/en.m3u8?t=1\"\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=5000000,AVERAGE-BANDWIDTH=4500000,RESOLUTION=1920x1080,CODECS=\"avc1.640028,mp4a.40.2\",FRAME-RATE=50,AUDIO=\"aud\"\nhi/index.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS=\"avc1.4d401e,mp4a.40.2\",AUDIO=\"aud\"\nlo/index.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS=\"mp4a.40.2\"\naudio-only.m3u8\n"
        + "#EXT-X-STREAM-INF:BANDWIDTH=1\nfile:///etc/passwd\n";

    private const string Media = "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:120\n"
        + "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x1234\n#EXT-X-MAP:URI=\"init.mp4\"\n"
        + "#EXT-X-PROGRAM-DATE-TIME:2026-10-25T10:00:00Z\n#EXTINF:6.000,first\nseg120.m4s\n"
        + "#EXT-X-DISCONTINUITY\n#EXT-X-BYTERANGE:1000@0\n#EXTINF:5.5,\nhttps://other.example/seg121.m4s\n"
        + "#EXT-X-KEY:METHOD=NONE\n#EXTINF:6,\nseg122.m4s\n";

    [Fact]
    public void ParsesMasterPlaylists()
    {
        var p = HlsPlaylist.Parse(Master, new Uri("https://cdn.example/live/master.m3u8?token=abc"));
        Assert.True(p.IsMaster);
        Assert.Equal(4, p.Version);
        Assert.True(p.IndependentSegments);
        Assert.Equal(3, p.Variants.Count); // the file:// variant is never followed
        var hi = p.Variants[0];
        Assert.Equal("https://cdn.example/live/hi/index.m3u8", hi.Uri.AbsoluteUri);
        Assert.Equal(1080, hi.Height);
        Assert.Equal(1920, hi.Width);
        Assert.Equal(4_500_000, hi.AverageBandwidth);
        Assert.Equal(50, hi.FrameRate);
        Assert.Equal("aud", hi.AudioGroup);
        Assert.Equal(2, p.Renditions.Count);
        Assert.Equal("https://cdn.example/live/audio/it.m3u8", p.Renditions[0].Uri!.AbsoluteUri);
        Assert.True(p.Renditions[0].IsDefault);
        Assert.Equal("en", p.Renditions[1].Language);
    }

    [Fact]
    public void ParsesMediaPlaylists()
    {
        var p = HlsPlaylist.Parse(Media, new Uri("https://cdn.example/live/hi/index.m3u8"));
        Assert.False(p.IsMaster);
        Assert.Equal(120, p.MediaSequence);
        Assert.Equal(3, p.Segments.Count);
        Assert.Equal(new long[] { 120, 121, 122 }, p.Segments.Select(s => s.Sequence));
        Assert.Equal("https://cdn.example/live/hi/key.bin", p.Segments[0].KeyUri!.AbsoluteUri);
        Assert.Equal("https://cdn.example/live/hi/init.mp4", p.Segments[1].MapUri!.AbsoluteUri);
        Assert.Contains("#EXT-X-PROGRAM-DATE-TIME:2026-10-25T10:00:00Z", p.Segments[0].Tags);
        Assert.True(p.Segments[1].Discontinuity);
        Assert.Contains("#EXT-X-BYTERANGE:1000@0", p.Segments[1].Tags);
        Assert.Equal("https://other.example/seg121.m4s", p.Segments[1].Uri.AbsoluteUri);
        Assert.Null(p.Segments[2].KeyLine);
        Assert.Equal("first", p.Segments[0].Title);
        Assert.Equal(5.5, p.Segments[1].Duration);
    }

    [Fact]
    public void AttributesAndUriReplacement()
    {
        var a = HlsPlaylist.ParseAttributes("METHOD=AES-128,URI=\"https://k.example/key?a=1,b=2\",IV=0x01");
        Assert.Equal("https://k.example/key?a=1,b=2", a["URI"]);
        Assert.Equal("0x01", a["IV"]);
        Assert.Equal("#EXT-X-KEY:METHOD=AES-128,URI=\"../s/abc\",IV=0x01", HlsPlaylist.ReplaceUriAttribute("#EXT-X-KEY:METHOD=AES-128,URI=\"https://k.example/key?a=1,b=2\",IV=0x01", "../s/abc"));
    }

    [Theory]
    [InlineData("auto", 1080)]
    [InlineData("low", 360)]
    [InlineData("h480", 360)]
    [InlineData("max1000000", 360)]
    [InlineData("max100", 360)]
    public void ChoosesVariants(string selector, int expectedHeight)
    {
        var p = HlsPlaylist.Parse(Master, new Uri("https://cdn.example/live/master.m3u8"));
        Assert.Equal(expectedHeight, HlsPlaylist.Choose(p.Variants, VariantSelector.Parse(selector))!.Height);
        Assert.Equal(selector, VariantSelector.Parse(selector).ToString());
    }

    [Fact]
    public void SniffsFormatsAndExpiry()
    {
        Assert.Equal(StreamKind.Hls, DirectResolver.Sniff(Encoding.UTF8.GetBytes("﻿#EXTM3U\n"), null));
        Assert.Equal(StreamKind.Dash, DirectResolver.Sniff(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\">"), null));
        Assert.Equal(StreamKind.Embed, DirectResolver.Sniff(Encoding.UTF8.GetBytes("<!doctype html><html>"), "text/html"));
        var ts = new byte[400];
        ts[0] = 0x47;
        ts[188] = 0x47;
        Assert.Equal(StreamKind.Ts, DirectResolver.Sniff(ts, null));
        Assert.Equal(StreamKind.Mp4, DirectResolver.Sniff(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }, null));

        var exp = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        Assert.Equal(exp, DirectResolver.ExpiryFromUrl(new Uri("https://cdn.example/a.m3u8?token=x&expires=1900000000")));
        Assert.Equal(exp, DirectResolver.ExpiryFromUrl(new Uri("https://cdn.example/a.m3u8?hdnts=st=1~exp=1900000000~acl=/*")));
        Assert.Equal(exp, DirectResolver.ExpiryFromUrl(new Uri("https://cdn.example/a.m3u8?e=1900000000000")));
        Assert.Null(DirectResolver.ExpiryFromUrl(new Uri("https://cdn.example/a.m3u8?id=1900000000")));
    }

    [Fact]
    public void ExtractsManifestsFromPages()
    {
        var page = new Uri("https://site.example/watch/1");
        var html = "<html><script>var cfg = {\"file\":\"https:\\/\\/cdn.example\\/live\\/master.m3u8?x=1\\u0026y=2\"};"
            + "var s = atob(\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("https://b64.example/hls/index.m3u8")) + "\");</script>"
            + "<video><source src=\"/media/clip.mp4\" type=\"video/mp4\"></video>"
            + "<iframe data-src=\"//player.example/embed/9\"></iframe><a href=\"/x.mpd\">x</a></html>";
        var (manifests, frames) = PageExtractResolver.Extract(html, page);
        Assert.Equal("https://cdn.example/live/master.m3u8?x=1&y=2", manifests[0].AbsoluteUri);
        Assert.Contains(manifests, u => u.AbsoluteUri == "https://b64.example/hls/index.m3u8");
        Assert.Contains(manifests, u => u.AbsoluteUri == "https://site.example/media/clip.mp4");
        Assert.Contains(manifests, u => u.AbsoluteUri == "https://site.example/x.mpd");
        Assert.Equal("https://player.example/embed/9", Assert.Single(frames).AbsoluteUri);
    }

    [Fact]
    public void TokensAreSignedAndExpire()
    {
        var dir = Path.Combine(Path.GetTempPath(), "livehub-tok-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            var protector = new SecretProtector(new LiveHubPaths(Path.Combine(dir, "a"), Path.Combine(dir, "c")));
            var service = new RelayTokenService(protector, clock);
            var token = service.Create(new RelayClaims { Key = "evt", StreamId = "s1", Variant = "h720", User = Guid.NewGuid().ToString("N") }, TimeSpan.FromHours(1));
            Assert.True(service.TryValidate(token, out var claims));
            Assert.Equal("e|evt|s1|h720", claims.SessionKey);

            // Tampering with the payload (another stream) breaks the signature.
            var forged = service.Create(new RelayClaims { Key = "evt", StreamId = "s2" }, TimeSpan.FromHours(1));
            var mixed = forged.Split('.')[0] + "." + token.Split('.')[1];
            Assert.False(service.TryValidate(mixed, out _));
            Assert.False(service.TryValidate(token[..^2] + (token[^1] == 'A' ? "BB" : "AA"), out _));
            Assert.False(service.TryValidate("garbage", out _));

            // Another installation (other master key) cannot validate it.
            var other = new RelayTokenService(new SecretProtector(new LiveHubPaths(Path.Combine(dir, "b"), Path.Combine(dir, "d"))), clock);
            Assert.False(other.TryValidate(token, out _));

            clock.Advance(TimeSpan.FromHours(2));
            Assert.False(service.TryValidate(token, out _));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task TsFanoutBroadcastsOneOriginToEverySubscriber()
    {
        var payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        var opens = 0;
        await using var fanout = new TsFanout(
            async (attempt, ct) =>
            {
                Interlocked.Increment(ref opens);
                await Task.Delay(50, ct);
                return attempt == 0 ? new MemoryStream(payload) : throw new IOException("ended");
            },
            NullLogger.Instance);
        var a = fanout.Subscribe();
        var b = fanout.Subscribe();
        async Task<byte[]> Drain(System.Threading.Channels.ChannelReader<ReadOnlyMemory<byte>> reader)
        {
            using var ms = new MemoryStream();
            await foreach (var chunk in reader.ReadAllAsync())
            {
                ms.Write(chunk.Span);
            }

            return ms.ToArray();
        }

        var results = await Task.WhenAll(Drain(a), Drain(b)).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(payload, results[0]);
        Assert.Equal(payload, results[1]);
        Assert.Equal(2, opens); // the first connection + one failed reconnection attempt
    }
}
