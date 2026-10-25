using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Formats;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Interprets Provider Definitions and produces the internal model. One gateway (rate limit, breaker, caches) per provider.
/// </summary>
public sealed class ProviderEngine
{
    private readonly IOutboundHttp _http;
    private readonly IEgressResolver _egress;
    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, ProviderGateway> _gateways = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderEngine"/> class.
    /// </summary>
    /// <param name="http">Outgoing HTTP.</param>
    /// <param name="egress">Egress resolver.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">Clock (null = system).</param>
    public ProviderEngine(IOutboundHttp http, IEgressResolver egress, ILogger<ProviderEngine> logger, TimeProvider? clock = null)
    {
        _http = http;
        _egress = egress;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Gets the clock.</summary>
    public TimeProvider Clock => _clock;

    /// <summary>Gets (or creates) the gateway of a provider.</summary>
    /// <param name="providerId">The provider id.</param>
    /// <returns>The gateway.</returns>
    public ProviderGateway GetGateway(string providerId) => _gateways.GetOrAdd(providerId, id => new ProviderGateway(id, _http, _clock));

    /// <summary>Gets all gateways (diagnostics).</summary>
    /// <returns>The gateways.</returns>
    public IReadOnlyCollection<ProviderGateway> Gateways() => _gateways.Values.ToList();

    /// <summary>Drops the gateway of a deleted provider.</summary>
    /// <param name="providerId">The provider id.</param>
    public void Forget(string providerId) => _gateways.TryRemove(providerId, out _);

    /// <summary>Creates a run context (exposed for the wizard and the stream chain).</summary>
    /// <param name="definition">Definition with decrypted secrets.</param>
    /// <param name="options">Options.</param>
    /// <param name="gateway">Gateway override (wizard previews use an isolated one).</param>
    /// <returns>The context.</returns>
    public ProviderRunContext CreateContext(ProviderDefinition definition, RunOptions? options = null, ProviderGateway? gateway = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var gw = gateway ?? GetGateway(definition.Id);
        var rate = definition.Connection?.RateLimit;
        gw.Bucket.Configure(rate?.RequestsPerMinute ?? 120, rate?.Burst ?? 10);
        var profile = _egress.ResolveProfileId(definition.EgressProfile, definition.Defaults?.Country);
        gw.Stats.EgressProfile = profile;
        return new ProviderRunContext(definition, gw, profile, _logger, options);
    }

    /// <summary>Runs a provider.</summary>
    /// <param name="definition">Definition with decrypted secrets.</param>
    /// <param name="options">Options.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="gateway">Gateway override.</param>
    /// <returns>The result.</returns>
    public async Task<ProviderRunResult> RunAsync(ProviderDefinition definition, RunOptions? options, CancellationToken cancellationToken, ProviderGateway? gateway = null)
    {
        var ctx = CreateContext(definition, options, gateway);
        var result = new ProviderRunResult();
        List<EndpointResult> endpoints;
        try
        {
            endpoints = definition.Kind switch
            {
                ProviderKind.Rest => await RestRunner.RunAsync(ctx, cancellationToken).ConfigureAwait(false),
                ProviderKind.M3u => await PlaylistRunners.RunM3uAsync(ctx, cancellationToken).ConfigureAwait(false),
                ProviderKind.Xtream => await PlaylistRunners.RunXtreamAsync(ctx, cancellationToken).ConfigureAwait(false),
                ProviderKind.Direct => PlaylistRunners.RunDirect(ctx),
                _ => new List<EndpointResult>(),
            };
        }
        catch (ProviderRequestException ex)
        {
            ctx.Diagnostics.Errors.Add(ctx.Redact(ex.Message));
            endpoints = new List<EndpointResult>();
        }

        result.Endpoints.AddRange(endpoints);
        result.Snapshot = BuildSnapshot(ctx, endpoints);

        if (definition.Streams is not null && definition.Streams.Levels.Count > 0)
        {
            var lazy = definition.Streams.Lazy != false;
            if (lazy)
            {
                foreach (var ev in result.Snapshot.Events)
                {
                    ev.HasLazyStreams = true;
                }
            }
            else if (ctx.Options.RunEagerStreamChains)
            {
                await RunEagerChainsAsync(ctx, result.Snapshot.Events, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var d in ctx.Diagnostics.Requests)
        {
            result.Diagnostics.Requests.Add(d);
        }

        result.Diagnostics.Warnings.AddRange(ctx.Diagnostics.Warnings);
        result.Diagnostics.Errors.AddRange(ctx.Diagnostics.Errors);
        foreach (var s in ctx.Diagnostics.Samples)
        {
            result.Diagnostics.Samples[s.Key] = s.Value;
        }

        result.Snapshot.Warnings.AddRange(ctx.Diagnostics.Warnings.Take(50));
        return result;
    }

    /// <summary>Fetches the lazy streams of an event (stream chain / event detail).</summary>
    /// <param name="definition">Definition with decrypted secrets.</param>
    /// <param name="ev">The event (as produced by this provider).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The streams and diagnostics.</returns>
    public async Task<(List<LiveStream> Streams, RunDiagnostics Diagnostics)> FetchLazyStreamsAsync(ProviderDefinition definition, LiveEvent ev, CancellationToken cancellationToken)
    {
        var ctx = CreateContext(definition);
        var streams = await StreamChainRunner.FetchAsync(ctx, ev, cancellationToken).ConfigureAwait(false);
        return (streams, ctx.Diagnostics);
    }

    /// <summary>Merges endpoint results into a snapshot (events of several endpoints are merged by key).</summary>
    /// <param name="ctx">The context.</param>
    /// <param name="endpoints">The endpoint results.</param>
    /// <returns>The snapshot.</returns>
    public static ProviderSnapshot BuildSnapshot(ProviderRunContext ctx, IEnumerable<EndpointResult> endpoints)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(endpoints);
        var snapshot = new ProviderSnapshot { ProviderId = ctx.ProviderId, FetchedAt = ctx.Clock.GetUtcNow() };
        var events = new Dictionary<string, LiveEvent>(StringComparer.Ordinal);
        var channels = new Dictionary<string, LiveChannel>(StringComparer.Ordinal);
        foreach (var r in endpoints)
        {
            foreach (var c in r.Categories.Where(c => !snapshot.Categories.Exists(x => x.Id == c.Id)))
            {
                snapshot.Categories.Add(c);
            }

            foreach (var ev in r.Events)
            {
                if (events.TryGetValue(ev.Key, out var existing))
                {
                    MergeSameProvider(existing, ev);
                }
                else
                {
                    events[ev.Key] = ev;
                }
            }

            foreach (var ch in r.Channels)
            {
                if (channels.TryGetValue(ch.Key, out var existing))
                {
                    foreach (var s in ch.Streams.Where(s => !existing.Streams.Exists(x => x.Id == s.Id)))
                    {
                        existing.Streams.Add(s);
                    }
                }
                else
                {
                    channels[ch.Key] = ch;
                }
            }

            snapshot.Programs.AddRange(r.Programs);
        }

        snapshot.Events.AddRange(events.Values.OrderBy(e => e.Start));
        snapshot.Channels.AddRange(channels.Values);
        return snapshot;
    }

    private static void MergeSameProvider(LiveEvent target, LiveEvent other)
    {
        target.IsLiveFlag = other.IsLiveFlag ?? target.IsLiveFlag;
        target.StatusFlag = other.StatusFlag ?? target.StatusFlag;
        target.End ??= other.End;
        target.Popularity = other.Popularity ?? target.Popularity;
        target.PosterUrl ??= other.PosterUrl;
        target.ThumbUrl ??= other.ThumbUrl;
        target.LogoUrl ??= other.LogoUrl;
        target.Description ??= other.Description;
        target.Competition ??= other.Competition;
        foreach (var s in other.Streams.Where(s => !target.Streams.Exists(x => x.Id == s.Id)))
        {
            target.Streams.Add(s);
        }

        foreach (var kv in other.Context)
        {
            target.Context.TryAdd(kv.Key, kv.Value);
        }
    }

    private static async Task RunEagerChainsAsync(ProviderRunContext ctx, List<LiveEvent> events, CancellationToken cancellationToken)
    {
        var horizon = ctx.Clock.GetUtcNow().AddHours(24);
        var targets = events.Where(e => e.Start <= horizon).Take(ctx.Options.Preview ? 3 : 60).ToList();
        using var gate = new SemaphoreSlim(3);
        var tasks = targets.Select(async ev =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var streams = await StreamChainRunner.FetchAsync(ctx, ev, cancellationToken).ConfigureAwait(false);
                lock (ev)
                {
                    foreach (var s in streams.Where(s => !ev.Streams.Exists(x => x.Id == s.Id)))
                    {
                        ev.Streams.Add(s);
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var ev in events.Except(targets))
        {
            ev.HasLazyStreams = true;
        }
    }
}
