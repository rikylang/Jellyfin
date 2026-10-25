using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Resolve;

/// <summary>
/// Runs the resolver chain (direct → page extraction → headless sidecar) according to the definition settings,
/// caches results with a TTL shorter than the URL expiry and deduplicates concurrent resolutions.
/// </summary>
public sealed class StreamResolverService
{
    private static readonly TimeSpan ResolveBudget = TimeSpan.FromSeconds(30);

    private readonly DefinitionStore _store;
    private readonly IEgressResolver _egress;
    private readonly DirectResolver _direct;
    private readonly PageExtractResolver _extract;
    private readonly HeadlessResolver _headless;
    private readonly ILogger<StreamResolverService> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, ResolvedStream Stream)> _cache = new(StringComparer.Ordinal);
    private readonly SingleFlight<ResolvedStream> _flight = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamResolverService"/> class.
    /// </summary>
    /// <param name="store">Definitions.</param>
    /// <param name="egress">Egress resolver.</param>
    /// <param name="direct">Direct resolver.</param>
    /// <param name="extract">Page extraction resolver.</param>
    /// <param name="headless">Headless resolver.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">Clock.</param>
    public StreamResolverService(DefinitionStore store, IEgressResolver egress, DirectResolver direct, PageExtractResolver extract, HeadlessResolver headless, ILogger<StreamResolverService> logger, TimeProvider? clock = null)
    {
        _store = store;
        _egress = egress;
        _direct = direct;
        _extract = extract;
        _headless = headless;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Returns a cached resolution without resolving.</summary>
    /// <param name="stream">The stream.</param>
    /// <returns>The cached result or null.</returns>
    public ResolvedStream? Peek(LiveStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return _cache.TryGetValue(CacheKey(stream), out var entry) && entry.Until > _clock.GetUtcNow() ? entry.Stream : null;
    }

    /// <summary>Drops the cached resolution of a stream (after a 403/410 from the origin).</summary>
    /// <param name="stream">The stream.</param>
    public void Invalidate(LiveStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _cache.TryRemove(CacheKey(stream), out _);
    }

    /// <summary>Resolves a catalog stream (definition read from the store).</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="force">Ignore the cache.</param>
    /// <param name="cancellationToken">Cancellation (the shared resolution keeps running for other callers).</param>
    /// <returns>The resolved stream.</returns>
    public Task<ResolvedStream> ResolveAsync(LiveStream stream, bool force, CancellationToken cancellationToken)
        => ResolveAsync(stream, null, force, null, cancellationToken);

    /// <summary>Resolves a stream.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="definition">Definition with plain secrets (null = read from the store).</param>
    /// <param name="force">Ignore the cache.</param>
    /// <param name="steps">Collects diagnostic steps (disables sharing with concurrent callers).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The resolved stream.</returns>
    public async Task<ResolvedStream> ResolveAsync(LiveStream stream, ProviderDefinition? definition, bool force, List<string>? steps, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var key = CacheKey(stream);
        if (!force && steps is null && Peek(stream) is { } cached)
        {
            return cached;
        }

        if (steps is not null)
        {
            return await ResolveCoreAsync(stream, definition, steps, cancellationToken).ConfigureAwait(false);
        }

        // Shared resolution: it must not be cancelled by the first caller going away.
        var task = _flight.RunAsync(key, async () =>
        {
            using var cts = new CancellationTokenSource(ResolveBudget);
            return await ResolveCoreAsync(stream, definition, new List<string>(), cts.Token).ConfigureAwait(false);
        });
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the base request for a stream (egress profile, headers, user agent).</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="definition">The definition (plain secrets), if any.</param>
    /// <returns>The request.</returns>
    public ResolveRequest CreateRequest(LiveStream stream, ProviderDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var request = new ResolveRequest
        {
            Stream = stream,
            Url = Uri.TryCreate(stream.Url, UriKind.Absolute, out var u) ? u : throw new ResolveException("Invalid stream URL", null, "hint.invalidUrl"),
            Definition = definition,
            EgressProfileId = _egress.ResolveProfileId(definition?.EgressProfile, definition?.Defaults?.Country),
            AllowPrivateNetwork = definition?.AllowPrivateNetwork ?? false,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(definition?.Connection?.TimeoutSeconds ?? 10, 3, 30)),
        };

        var ua = definition?.Connection?.UserAgent;
        if (!string.IsNullOrWhiteSpace(ua) && !ua.Contains('{', StringComparison.Ordinal))
        {
            request.Headers["User-Agent"] = ua;
        }

        foreach (var kv in stream.Headers)
        {
            request.Headers[kv.Key] = kv.Value;
        }

        return request;
    }

    private static string CacheKey(LiveStream stream) => stream.ProviderId + "|" + Keys.Hash(stream.Id + "|" + stream.Url);

    private static List<IStreamResolver> Chain(ResolverStrategy strategy, bool enabled, StreamKind kind, IStreamResolver direct, IStreamResolver extract, HeadlessResolver headless)
    {
        if (!enabled)
        {
            return new List<IStreamResolver> { direct };
        }

        var chain = strategy switch
        {
            ResolverStrategy.Direct => new List<IStreamResolver> { direct },
            ResolverStrategy.Extract => new List<IStreamResolver> { extract },
            ResolverStrategy.Headless => new List<IStreamResolver> { headless },
            _ => kind == StreamKind.Embed ? new List<IStreamResolver> { extract } : new List<IStreamResolver> { direct, extract },
        };

        if (strategy == ResolverStrategy.Auto && headless.Enabled)
        {
            chain.Add(headless);
        }

        return chain;
    }

    private async Task<ResolvedStream> ResolveCoreAsync(LiveStream stream, ProviderDefinition? definition, List<string> steps, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        definition ??= string.IsNullOrEmpty(stream.ProviderId) ? null : _store.GetForRun(stream.ProviderId);
        var request = CreateRequest(stream, definition);
        request.Steps.Clear();
        var spec = definition?.Resolver;
        var chain = Chain(spec?.Strategy ?? ResolverStrategy.Auto, spec?.Enabled != false, stream.Kind, _direct, _extract, _headless);
        steps.Add($"resolver: chain {string.Join(" → ", chain.Select(c => c.Name))}, egress '{request.EgressProfileId}'");

        Exception? failure = null;
        foreach (var resolver in chain)
        {
            var attempt = new ResolveRequest
            {
                Stream = request.Stream,
                Url = request.Url,
                Definition = request.Definition,
                EgressProfileId = request.EgressProfileId,
                AllowPrivateNetwork = request.AllowPrivateNetwork,
                Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase),
                Timeout = request.Timeout,
                Steps = steps,
            };

            try
            {
                var resolved = await resolver.ResolveAsync(attempt, cancellationToken).ConfigureAwait(false);
                if (resolved is null)
                {
                    continue;
                }

                resolved.Latency = sw.Elapsed;
                Store(stream, resolved, spec?.CacheSeconds);
                return resolved;
            }
            catch (EgressUnavailableException)
            {
                // Kill switch: never fall back to another route.
                throw;
            }
            catch (ResolveException ex)
            {
                failure = ex;
                if (ex.StatusCode is 401 or 403 && resolver == _direct && stream.Kind != StreamKind.Embed)
                {
                    // A protected URL is not a page: extraction would not help.
                    break;
                }
            }
            catch (SsrfException ex)
            {
                steps.Add(resolver.Name + ": blocked destination: " + ex.Message);
                failure = new ResolveException(ex.Message, null, "hint.ssrf");
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or ResponseTooLargeException or System.IO.IOException)
            {
                steps.Add(resolver.Name + ": " + ex.Message);
                failure = new ResolveException(ex.Message, null, ex is TimeoutException ? "hint.timeout" : "hint.network");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = new ResolveException("Timeout", null, "hint.timeout");
            }
        }

        _logger.LogDebug("Stream {StreamId} of {Provider} could not be resolved: {Error}", stream.Id, stream.ProviderId, failure?.Message);
        throw failure as ResolveException ?? new ResolveException("No playable URL found", null, "hint.noStream");
    }

    private void Store(LiveStream stream, ResolvedStream resolved, int? cacheSeconds)
    {
        var now = _clock.GetUtcNow();
        var ttl = TimeSpan.FromSeconds(Math.Clamp(cacheSeconds ?? 600, 0, 6 * 3600));
        var untilExpiry = resolved.ExpiresAt - now - TimeSpan.FromSeconds(60);
        if (untilExpiry < ttl)
        {
            ttl = untilExpiry;
        }

        if (ttl > TimeSpan.Zero)
        {
            _cache[CacheKey(stream)] = (now + ttl, resolved);
        }

        if (_cache.Count > 2000)
        {
            foreach (var kv in _cache.Where(kv => kv.Value.Until <= now).ToList())
            {
                _cache.TryRemove(kv.Key, out _);
            }
        }
    }
}
