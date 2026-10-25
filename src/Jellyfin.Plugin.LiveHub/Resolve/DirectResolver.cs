using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Relay;

namespace Jellyfin.Plugin.LiveHub.Resolve;

/// <summary>
/// Validates URLs that are already playable (HLS, DASH, MP4, TS): one request, format sniffed from the first bytes,
/// HLS playlists parsed (variants). Web pages are left to the next resolver.
/// </summary>
public sealed class DirectResolver : IStreamResolver
{
    private const int HeadBytes = 4096;
    private const int MaxPlaylistBytes = 4 * 1024 * 1024;

    private static readonly Regex ExpiryRegex = new(
        @"(?:^|[?&~;,/])(?:exp|expires|expiry|expire|e|validto|valid_to|token_expires)=(?<v>\d{10,13})(?:$|[&~;,/])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private readonly IOutboundHttp _http;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectResolver"/> class.
    /// </summary>
    /// <param name="http">Outgoing HTTP.</param>
    /// <param name="clock">Clock.</param>
    public DirectResolver(IOutboundHttp http, TimeProvider? clock = null)
    {
        _http = http;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "direct";

    /// <summary>Reads an expiry timestamp from a signed URL (exp=, expires=, hdnts=exp=...).</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The expiry, if present.</returns>
    public static DateTimeOffset? ExpiryFromUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var text = Uri.UnescapeDataString(url.Query + url.AbsolutePath);
        try
        {
            var m = ExpiryRegex.Match(text);
            if (!m.Success || !long.TryParse(m.Groups["v"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                return null;
            }

            var instant = v > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(v) : DateTimeOffset.FromUnixTimeSeconds(v);
            return instant.Year is > 2000 and < 2200 ? instant : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Detects the format of a body from its first bytes and the content type.</summary>
    /// <param name="head">The first bytes.</param>
    /// <param name="contentType">The content type.</param>
    /// <returns>The kind; <see cref="StreamKind.Embed"/> for HTML; <see cref="StreamKind.Auto"/> when unknown.</returns>
    public static StreamKind Sniff(ReadOnlySpan<byte> head, string? contentType)
    {
        var text = Encoding.UTF8.GetString(head[..Math.Min(head.Length, 1024)]).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (text.StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            return StreamKind.Hls;
        }

        if (text.Contains("<MPD", StringComparison.Ordinal))
        {
            return StreamKind.Dash;
        }

        if (head.Length >= 189 && head[0] == 0x47 && head[188] == 0x47)
        {
            return StreamKind.Ts;
        }

        if (head.Length >= 8 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p')
        {
            return StreamKind.Mp4;
        }

        if (text.StartsWith('<') && (text.Contains("<html", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) || text.Contains("<body", StringComparison.OrdinalIgnoreCase)))
        {
            return StreamKind.Embed;
        }

        var ct = (contentType ?? string.Empty).ToLowerInvariant();
        if (ct.Contains("mpegurl", StringComparison.Ordinal))
        {
            return StreamKind.Hls;
        }

        if (ct.Contains("dash+xml", StringComparison.Ordinal))
        {
            return StreamKind.Dash;
        }

        if (ct.Contains("mp2t", StringComparison.Ordinal))
        {
            return StreamKind.Ts;
        }

        if (ct.Contains("video/mp4", StringComparison.Ordinal))
        {
            return StreamKind.Mp4;
        }

        if (head.Length >= 1 && head[0] == 0x47)
        {
            return StreamKind.Ts;
        }

        return ct.Contains("text/html", StringComparison.Ordinal) ? StreamKind.Embed : StreamKind.Auto;
    }

    /// <summary>Maps an HTTP failure to a diagnosis key.</summary>
    /// <param name="status">The status.</param>
    /// <returns>The hint key.</returns>
    public static string HintFor(int status) => status switch
    {
        401 or 403 => "hint.forbidden",
        404 or 410 => "hint.notFound",
        429 => "hint.rateLimited",
        >= 500 => "hint.serverError",
        _ => "hint.httpError",
    };

    /// <inheritdoc />
    public async Task<ResolvedStream?> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();
        var fetch = new FetchRequest
        {
            ProviderId = request.Stream.ProviderId,
            Url = request.Url,
            Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
            EgressProfileId = request.EgressProfileId,
            AllowPrivateNetwork = request.AllowPrivateNetwork,
            Timeout = request.Timeout,
        };

        var response = await _http.OpenStreamAsync(fetch, cancellationToken).ConfigureAwait(false);
        await using (response.ConfigureAwait(false))
        {
            if (response.StatusCode is < 200 or >= 300)
            {
                request.Steps.Add($"direct: HTTP {response.StatusCode} from {response.FinalUrl.Host}");
                throw new ResolveException($"HTTP {response.StatusCode}", response.StatusCode, HintFor(response.StatusCode));
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(request.Timeout);
            var head = await ReadAsync(response.Stream, HeadBytes, cts.Token).ConfigureAwait(false);
            var kind = Sniff(head, response.ContentType);
            request.Steps.Add($"direct: HTTP {response.StatusCode}, {response.ContentType ?? "?"}, detected {kind}");
            if (kind == StreamKind.Embed)
            {
                return null;
            }

            if (kind == StreamKind.Auto)
            {
                kind = request.Stream.Kind is StreamKind.Hls or StreamKind.Dash or StreamKind.Mp4 or StreamKind.Ts ? request.Stream.Kind : StreamKind.Auto;
                if (kind == StreamKind.Auto)
                {
                    throw new ResolveException("Unknown stream format", null, "hint.unknownFormat");
                }
            }

            var resolved = new ResolvedStream
            {
                Url = response.FinalUrl,
                Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
                Kind = kind,
                Strategy = Name,
                EgressProfileId = request.EgressProfileId,
                AllowPrivateNetwork = request.AllowPrivateNetwork,
                ProviderId = request.Stream.ProviderId,
                ContentType = response.ContentType,
                ExpiresAt = ExpiryFromUrl(response.FinalUrl) ?? request.Stream.ExpiresAt ?? _clock.GetUtcNow().AddHours(6),
            };

            if (kind == StreamKind.Hls)
            {
                var rest = await ReadAsync(response.Stream, MaxPlaylistBytes - head.Length, cts.Token).ConfigureAwait(false);
                var all = new byte[head.Length + rest.Length];
                head.CopyTo(all, 0);
                rest.CopyTo(all, head.Length);
                var text = Encoding.UTF8.GetString(all);
                resolved.Playlist = HlsPlaylist.Parse(text, response.FinalUrl);
                request.Steps.Add(resolved.Playlist.IsMaster
                    ? $"direct: HLS master with {resolved.Playlist.Variants.Count} variant(s)"
                    : $"direct: HLS media playlist with {resolved.Playlist.Segments.Count} segment(s){(resolved.Playlist.Ended ? " (ended)" : " (live)")}");
            }

            resolved.Latency = sw.Elapsed;
            return resolved;
        }
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int max, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(max, 64 * 1024)];
        using var ms = new MemoryStream();
        while (ms.Length < max)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, max - ms.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await ms.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return ms.ToArray();
    }
}
