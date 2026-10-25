using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Diagnostics;
using Jellyfin.Plugin.LiveHub.Http;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Long-lived per-provider HTTP gateway: rate limit, circuit breaker, single-flight for identical concurrent requests,
/// conditional requests (ETag / If-Modified-Since) with body reuse on 304, OAuth2 token cache and statistics.
/// </summary>
public sealed class ProviderGateway
{
    private const int MaxConditionalEntries = 128;

    private readonly IOutboundHttp _http;
    private readonly SingleFlight<FetchResponse> _flights = new();
    private readonly ConcurrentDictionary<string, FetchResponse> _conditional = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderGateway"/> class.
    /// </summary>
    /// <param name="providerId">The provider id.</param>
    /// <param name="http">Outgoing HTTP.</param>
    /// <param name="clock">Clock.</param>
    public ProviderGateway(string providerId, IOutboundHttp http, TimeProvider? clock = null)
    {
        ProviderId = providerId;
        _http = http;
        Clock = clock ?? TimeProvider.System;
        Bucket = new TokenBucket(60, 10, Clock);
        Breaker = new CircuitBreaker(5, Clock);
        Stats = new ProviderStats { ProviderId = providerId };
    }

    /// <summary>Gets the provider id.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the clock.</summary>
    public TimeProvider Clock { get; }

    /// <summary>Gets the rate limiter.</summary>
    public TokenBucket Bucket { get; }

    /// <summary>Gets the circuit breaker.</summary>
    public CircuitBreaker Breaker { get; }

    /// <summary>Gets the statistics.</summary>
    public ProviderStats Stats { get; }

    /// <summary>Gets or sets the cached OAuth2 token.</summary>
    public string? OAuthToken { get; set; }

    /// <summary>Gets or sets the OAuth2 token expiry.</summary>
    public DateTimeOffset OAuthExpires { get; set; }

    /// <summary>Gets or sets a value indicating whether the circuit breaker is bypassed (wizard previews).</summary>
    public bool BypassBreaker { get; set; }

    /// <summary>Gets the raw outgoing HTTP (OAuth token requests).</summary>
    public IOutboundHttp Http => _http;

    /// <summary>Fetches through the gateway.</summary>
    /// <param name="request">The request.</param>
    /// <param name="endpoint">Endpoint id (statistics).</param>
    /// <param name="conditional">Whether to use conditional requests.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response (304 answers are replaced by the cached body with <see cref="FetchResponse.Headers"/>["X-LiveHub-NotModified"]).</returns>
    public Task<FetchResponse> FetchAsync(FetchRequest request, string endpoint, bool conditional, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!BypassBreaker && !Breaker.TryAcquire())
        {
            throw new CircuitOpenException($"Provider '{ProviderId}' is paused after repeated errors until {Breaker.OpenUntil:HH:mm:ss} UTC.");
        }

        var key = Key(request);
        return _flights.RunAsync(key, () => ExecuteAsync(request, key, endpoint, conditional, cancellationToken));
    }

    /// <summary>Clears the conditional cache (forces full responses on the next sync).</summary>
    public void ClearConditionalCache() => _conditional.Clear();

    private async Task<FetchResponse> ExecuteAsync(FetchRequest request, string key, string endpoint, bool conditional, CancellationToken cancellationToken)
    {
        await Bucket.WaitAsync(cancellationToken).ConfigureAwait(false);
        FetchResponse? cached = null;
        if (conditional && request.Method == HttpMethod.Get && _conditional.TryGetValue(key, out cached))
        {
            request.IfNoneMatch = cached.ETag;
            request.IfModifiedSince = cached.ETag is null ? cached.LastModified : null;
        }

        var started = Clock.GetTimestamp();
        try
        {
            var response = await _http.FetchAsync(request, cancellationToken).ConfigureAwait(false);
            var ms = Clock.GetElapsedTime(started).TotalMilliseconds;
            if (response.StatusCode == 304 && cached is not null)
            {
                Stats.Record(endpoint, ms, true, true);
                var reused = new FetchResponse
                {
                    StatusCode = 200,
                    Body = cached.Body,
                    ContentType = cached.ContentType,
                    ETag = cached.ETag,
                    LastModified = cached.LastModified,
                    FinalUrl = cached.FinalUrl,
                    Elapsed = response.Elapsed,
                };
                foreach (var h in cached.Headers)
                {
                    reused.Headers[h.Key] = h.Value;
                }

                reused.Headers["X-LiveHub-NotModified"] = "1";
                return reused;
            }

            var ok = response.IsSuccess;
            Stats.Record(endpoint, ms, ok, false);
            if (ok && conditional && (response.ETag is not null || response.LastModified is not null))
            {
                if (_conditional.Count >= MaxConditionalEntries)
                {
                    foreach (var k in _conditional.Keys.Take(MaxConditionalEntries / 4).ToList())
                    {
                        _conditional.TryRemove(k, out _);
                    }
                }

                _conditional[key] = response;
            }

            return response;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            Stats.Record(endpoint, Clock.GetElapsedTime(started).TotalMilliseconds, false, false);
            throw;
        }
    }

    private static string Key(FetchRequest r)
    {
        var sb = new StringBuilder();
        sb.Append(r.Method.Method).Append(' ').Append(r.Url.AbsoluteUri).Append('\n');
        foreach (var h in r.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(h.Key).Append(':').Append(h.Value).Append('\n');
        }

        sb.Append(r.Body);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
