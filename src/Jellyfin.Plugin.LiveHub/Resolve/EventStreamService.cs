using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Resolve;

/// <summary>
/// Returns every stream of an event, running the lazy stream chains (separate multi-level endpoints) of each source
/// provider on demand. Results are cached in the catalog for a few minutes and concurrent requests are merged.
/// </summary>
public sealed class EventStreamService
{
    private static readonly TimeSpan LazyTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EmptyTtl = TimeSpan.FromSeconds(45);

    private readonly CatalogService _catalog;
    private readonly DefinitionStore _store;
    private readonly ProviderEngine _engine;
    private readonly ILogger<EventStreamService> _logger;
    private readonly SingleFlight<List<LiveStream>> _flight = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="EventStreamService"/> class.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="store">Definitions.</param>
    /// <param name="engine">Engine.</param>
    /// <param name="logger">Logger.</param>
    public EventStreamService(CatalogService catalog, DefinitionStore store, ProviderEngine engine, ILogger<EventStreamService> logger)
    {
        _catalog = catalog;
        _store = store;
        _engine = engine;
        _logger = logger;
    }

    /// <summary>Gets the streams of an event, loading lazy chains when needed.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The streams.</returns>
    public async Task<List<LiveStream>> GetStreamsAsync(LiveEvent ev, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (!ev.HasLazyStreams || _catalog.GetLazyStreams(ev.Key) is not null)
        {
            return _catalog.StreamsOf(ev);
        }

        var task = _flight.RunAsync(ev.Key, async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var collected = await LoadLazyAsync(ev, cts.Token).ConfigureAwait(false);
            _catalog.SetLazyStreams(ev.Key, collected, collected.Count > 0 ? LazyTtl : EmptyTtl);
            return collected;
        });

        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Lazy streams of {Event} could not be loaded: {Error}", ev.Key, ex.Message);
        }

        return _catalog.StreamsOf(ev);
    }

    /// <summary>Gets the streams of an event or channel by key.</summary>
    /// <param name="kind">Event or 24/7 channel.</param>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The title and the streams, or null when the item no longer exists.</returns>
    public async Task<(string Title, List<LiveStream> Streams)?> GetItemStreamsAsync(Channels.ItemKind kind, string key, CancellationToken cancellationToken)
    {
        if (kind == Channels.ItemKind.Channel)
        {
            var channel = _catalog.FindChannel(key);
            return channel is null ? null : (channel.Name, channel.Streams.ToList());
        }

        var ev = _catalog.FindEvent(key);
        if (ev is null)
        {
            return null;
        }

        return (ev.Title, await GetStreamsAsync(ev, cancellationToken).ConfigureAwait(false));
    }

    private async Task<List<LiveStream>> LoadLazyAsync(LiveEvent ev, CancellationToken cancellationToken)
    {
        var result = new List<LiveStream>();
        var providers = ev.SourceProviders.Count > 0 ? ev.SourceProviders : new List<string> { ev.ProviderId };
        var tasks = providers.Distinct(StringComparer.Ordinal).Select(async providerId =>
        {
            var definition = _store.GetForRun(providerId);
            if (definition?.Streams is null || definition.Streams.Levels.Count == 0)
            {
                return new List<LiveStream>();
            }

            var providerEvent = _catalog.GetProviderSnapshot(providerId)?.Events
                .FirstOrDefault(e => e.Key == ev.Key || e.MergedRefs.Exists(r => ev.MergedRefs.Contains(r)));
            if (providerEvent is null)
            {
                return new List<LiveStream>();
            }

            var (streams, diagnostics) = await _engine.FetchLazyStreamsAsync(definition, providerEvent, cancellationToken).ConfigureAwait(false);
            foreach (var error in diagnostics.Errors.Take(3))
            {
                _logger.LogWarning("Stream chain of {Provider} for {Event}: {Error}", providerId, ev.Key, error);
            }

            return streams;
        });

        foreach (var list in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            result.AddRange(list.Where(s => !result.Exists(x => x.Id == s.Id || x.Url == s.Url)));
        }

        return result;
    }
}
