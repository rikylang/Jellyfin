using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Http;

namespace Jellyfin.Plugin.LiveHub.Resolve;

/// <summary>
/// Generic extraction of .m3u8/.mpd URLs from web pages: HTML, inline scripts (escaped slashes, JSON, base64/atob),
/// &lt;source&gt; tags and nested iframes (bounded depth). No JavaScript is executed.
/// </summary>
public sealed class PageExtractResolver : IStreamResolver
{
    private const long MaxPageBytes = 3 * 1024 * 1024;
    private const int MaxCandidates = 6;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex AbsoluteManifest = new(@"https?://[^\s""'<>\\()]+?\.(?:m3u8|mpd)(?:\?[^\s""'<>\\()]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex QuotedManifest = new(@"[""'](?<u>[^""'\s<>]+?\.(?:m3u8|mpd)(?:\?[^""'\s<>]*)?)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex Base64Literal = new(@"[""'](?<b>[A-Za-z0-9+/]{24,}={0,2})[""']", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex FrameSource = new(@"<(?:iframe|frame|embed)\b[^>]*?\b(?:data-)?src\s*=\s*[""'](?<u>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SourceTag = new(@"<(?:source|video)\b[^>]*?\bsrc\s*=\s*[""'](?<u>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex UnicodeEscape = new(@"\\u(?<h>[0-9a-fA-F]{4})", RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    private readonly IOutboundHttp _http;
    private readonly DirectResolver _direct;

    /// <summary>
    /// Initializes a new instance of the <see cref="PageExtractResolver"/> class.
    /// </summary>
    /// <param name="http">Outgoing HTTP.</param>
    /// <param name="direct">Direct resolver used to validate candidates.</param>
    public PageExtractResolver(IOutboundHttp http, DirectResolver direct)
    {
        _http = http;
        _direct = direct;
    }

    /// <inheritdoc />
    public string Name => "extract";

    /// <summary>Extracts manifest candidates and frame URLs from a page.</summary>
    /// <param name="html">The page.</param>
    /// <param name="pageUrl">The page URL.</param>
    /// <returns>The manifest candidates (best first) and the frames.</returns>
    public static (List<Uri> Manifests, List<Uri> Frames) Extract(string html, Uri pageUrl)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(pageUrl);
        var manifests = new List<Uri>();
        var frames = new List<Uri>();
        try
        {
            var text = Normalize(html);
            foreach (Match m in AbsoluteManifest.Matches(text))
            {
                Add(manifests, pageUrl, m.Value);
            }

            foreach (Match m in QuotedManifest.Matches(text))
            {
                Add(manifests, pageUrl, m.Groups["u"].Value);
            }

            foreach (Match m in SourceTag.Matches(text))
            {
                var u = m.Groups["u"].Value;
                if (u.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) || u.Contains(".mpd", StringComparison.OrdinalIgnoreCase) || u.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
                {
                    Add(manifests, pageUrl, u);
                }
            }

            foreach (Match m in Base64Literal.Matches(text))
            {
                var decoded = TryBase64(m.Groups["b"].Value);
                if (decoded is null)
                {
                    continue;
                }

                foreach (Match inner in AbsoluteManifest.Matches(Normalize(decoded)))
                {
                    Add(manifests, pageUrl, inner.Value);
                }
            }

            foreach (Match m in FrameSource.Matches(text))
            {
                Add(frames, pageUrl, m.Groups["u"].Value);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological page: use what was found so far.
        }

        var ordered = manifests
            .Distinct()
            .OrderBy(u => u.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(u => Score(u))
            .ToList();
        return (ordered, frames.Distinct().ToList());
    }

    /// <inheritdoc />
    public async Task<ResolvedStream?> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var maxDepth = Math.Clamp(request.Definition?.Resolver?.MaxDepth ?? 2, 0, 4);
        var referer = request.Definition?.Resolver?.Referer;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        ResolveException? last = null;
        var queue = new Queue<(Uri Url, string? Referer, int Depth)>();
        queue.Enqueue((request.Url, referer ?? (request.Headers.TryGetValue("Referer", out var r) ? r : null), 0));
        var pages = 0;

        while (queue.Count > 0 && pages < 8)
        {
            var (url, pageReferer, depth) = queue.Dequeue();
            if (!visited.Add(url.AbsoluteUri))
            {
                continue;
            }

            pages++;
            var fetch = new FetchRequest
            {
                ProviderId = request.Stream.ProviderId,
                Url = url,
                Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
                EgressProfileId = request.EgressProfileId,
                AllowPrivateNetwork = request.AllowPrivateNetwork,
                Timeout = request.Timeout,
                MaxBytes = MaxPageBytes,
            };
            fetch.Headers["Accept"] = "text/html,application/xhtml+xml,*/*;q=0.8";
            if (!string.IsNullOrEmpty(pageReferer))
            {
                fetch.Headers["Referer"] = pageReferer;
            }

            var response = await _http.FetchAsync(fetch, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccess)
            {
                request.Steps.Add($"extract: page {url.Host}{url.AbsolutePath} → HTTP {response.StatusCode}");
                last = new ResolveException($"HTTP {response.StatusCode}", response.StatusCode, DirectResolver.HintFor(response.StatusCode));
                continue;
            }

            var pageUrl = response.FinalUrl;
            if (DirectResolver.Sniff(response.Body, response.ContentType) is not Model.StreamKind.Embed and not Model.StreamKind.Auto)
            {
                // The "page" is actually a stream (e.g. a redirect to a playlist).
                return await ValidateAsync(request, pageUrl, pageReferer, cancellationToken).ConfigureAwait(false);
            }

            var (manifests, frames) = Extract(response.Text(), pageUrl);
            request.Steps.Add($"extract: page {pageUrl.Host}{pageUrl.AbsolutePath} → {manifests.Count} manifest(s), {frames.Count} frame(s)");
            foreach (var candidate in manifests.Take(MaxCandidates))
            {
                try
                {
                    var resolved = await ValidateAsync(request, candidate, pageUrl.AbsoluteUri, cancellationToken).ConfigureAwait(false);
                    if (resolved is not null)
                    {
                        return resolved;
                    }
                }
                catch (ResolveException ex)
                {
                    last = ex;
                }
                catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TimeoutException or SsrfException)
                {
                    request.Steps.Add("extract: candidate failed: " + ex.Message);
                }
            }

            if (depth < maxDepth)
            {
                foreach (var frame in frames.Take(4))
                {
                    queue.Enqueue((frame, pageUrl.AbsoluteUri, depth + 1));
                }
            }
        }

        if (last is not null)
        {
            throw last;
        }

        request.Steps.Add("extract: no playable URL found in the page");
        return null;
    }

    private static string Normalize(string text)
    {
        var s = text.Replace("\\/", "/", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);
        if (s.Contains("\\u", StringComparison.Ordinal))
        {
            try
            {
                s = UnicodeEscape.Replace(s, m => ((char)Convert.ToInt32(m.Groups["h"].Value, 16)).ToString());
            }
            catch (RegexMatchTimeoutException)
            {
                // keep the text as is
            }
        }

        return WebUtility.HtmlDecode(s);
    }

    private static string? TryBase64(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            var text = Encoding.UTF8.GetString(bytes);
            return text.Contains("://", StringComparison.Ordinal) ? text : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void Add(List<Uri> list, Uri pageUrl, string value)
    {
        var v = value.Trim();
        if (v.StartsWith("//", StringComparison.Ordinal))
        {
            v = pageUrl.Scheme + ":" + v;
        }

        if (Uri.TryCreate(pageUrl, v, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !list.Contains(uri))
        {
            list.Add(uri);
        }
    }

    private static int Score(Uri uri)
    {
        var p = uri.AbsolutePath.ToLowerInvariant();
        if (p.Contains("master", StringComparison.Ordinal) || p.Contains("playlist", StringComparison.Ordinal))
        {
            return 0;
        }

        return p.Contains("index", StringComparison.Ordinal) ? 1 : 2;
    }

    private async Task<ResolvedStream?> ValidateAsync(ResolveRequest request, Uri candidate, string? referer, CancellationToken cancellationToken)
    {
        var sub = new ResolveRequest
        {
            Stream = request.Stream,
            Url = candidate,
            Definition = request.Definition,
            EgressProfileId = request.EgressProfileId,
            AllowPrivateNetwork = request.AllowPrivateNetwork,
            Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
            Timeout = request.Timeout,
            Steps = request.Steps,
        };
        if (!string.IsNullOrEmpty(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
        {
            sub.Headers["Referer"] = referer;
            sub.Headers["Origin"] = refUri.GetLeftPart(UriPartial.Authority);
        }

        var resolved = await _direct.ResolveAsync(sub, cancellationToken).ConfigureAwait(false);
        if (resolved is not null)
        {
            resolved.Strategy = Name;
            resolved.FromEmbed = true;
        }

        return resolved;
    }
}
