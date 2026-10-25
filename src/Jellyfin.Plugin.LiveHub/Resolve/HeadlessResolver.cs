using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Http;

namespace Jellyfin.Plugin.LiveHub.Resolve;

/// <summary>
/// Optional resolver backed by the headless browser sidecar (Playwright, see resolver-sidecar/): the page is loaded in
/// Chromium and the first .m3u8/.mpd request is captured. The returned URL is then validated through the plugin egress.
/// Protocol: POST {url}/resolve {"url","referer","userAgent","timeoutMs"} → {"url","headers":{},"expiresAt"}.
/// </summary>
public sealed class HeadlessResolver : IStreamResolver
{
    private readonly IOutboundHttp _http;
    private readonly DirectResolver _direct;
    private readonly ConfigAccessor _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="HeadlessResolver"/> class.
    /// </summary>
    /// <param name="http">Outgoing HTTP.</param>
    /// <param name="direct">Direct resolver used to validate the result.</param>
    /// <param name="config">Configuration.</param>
    public HeadlessResolver(IOutboundHttp http, DirectResolver direct, ConfigAccessor config)
    {
        _http = http;
        _direct = direct;
        _config = config;
    }

    /// <inheritdoc />
    public string Name => "headless";

    /// <summary>Gets a value indicating whether the sidecar is configured.</summary>
    public bool Enabled => _config.Current.HeadlessResolverEnabled && Uri.TryCreate(_config.Current.HeadlessResolverUrl, UriKind.Absolute, out _);

    /// <inheritdoc />
    public async Task<ResolvedStream?> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enabled)
        {
            request.Steps.Add("headless: sidecar disabled");
            return null;
        }

        var endpoint = new Uri(new Uri(_config.Current.HeadlessResolverUrl.TrimEnd('/') + "/"), "resolve");
        var body = new JsonObject
        {
            ["url"] = request.Url.AbsoluteUri,
            ["referer"] = request.Definition?.Resolver?.Referer ?? (request.Headers.TryGetValue("Referer", out var r) ? r : null),
            ["userAgent"] = request.Headers.TryGetValue("User-Agent", out var ua) ? ua : OutboundHttp.DefaultUserAgent,
            ["timeoutMs"] = 20000,
        };

        // The sidecar runs next to Jellyfin (private address by design); it must share the VPN network itself
        // (docker-compose: network_mode "service:gluetun"), see docs/README.
        var response = await _http.FetchAsync(
            new FetchRequest
            {
                ProviderId = request.Stream.ProviderId,
                Url = endpoint,
                Method = HttpMethod.Post,
                Body = body.ToJsonString(),
                ContentType = "application/json",
                AllowPrivateNetwork = true,
                EgressProfileId = Configuration.EgressProfile.DirectId,
                Timeout = TimeSpan.FromSeconds(30),
                MaxBytes = 256 * 1024,
            },
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            request.Steps.Add($"headless: sidecar HTTP {response.StatusCode}");
            throw new ResolveException("Headless resolver failed: HTTP " + response.StatusCode, response.StatusCode, "hint.headless");
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(response.Text());
        }
        catch (JsonException ex)
        {
            throw new ResolveException("Headless resolver returned invalid JSON", ex);
        }

        var url = json?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var found))
        {
            request.Steps.Add("headless: no stream captured");
            return null;
        }

        var sub = new ResolveRequest
        {
            Stream = request.Stream,
            Url = found,
            Definition = request.Definition,
            EgressProfileId = request.EgressProfileId,
            AllowPrivateNetwork = request.AllowPrivateNetwork,
            Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
            Timeout = request.Timeout,
            Steps = request.Steps,
        };
        if (json?["headers"] is JsonObject headers)
        {
            foreach (var kv in headers)
            {
                if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && IsForwardable(kv.Key))
                {
                    sub.Headers[kv.Key] = s;
                }
            }
        }

        request.Steps.Add("headless: captured " + found.Host + found.AbsolutePath);
        var resolved = await _direct.ResolveAsync(sub, cancellationToken).ConfigureAwait(false);
        if (resolved is not null)
        {
            resolved.Strategy = Name;
            resolved.FromEmbed = true;
            if (json?["expiresAt"] is JsonValue exp && exp.TryGetValue<long>(out var unix) && unix > 0)
            {
                var at = unix > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(unix) : DateTimeOffset.FromUnixTimeSeconds(unix);
                if (at < resolved.ExpiresAt)
                {
                    resolved.ExpiresAt = at;
                }
            }
        }

        return resolved;
    }

    private static bool IsForwardable(string header)
        => header.Equals("Referer", StringComparison.OrdinalIgnoreCase)
           || header.Equals("Origin", StringComparison.OrdinalIgnoreCase)
           || header.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
           || header.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
           || header.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
           || header.Equals("Authorization", StringComparison.OrdinalIgnoreCase);
}
