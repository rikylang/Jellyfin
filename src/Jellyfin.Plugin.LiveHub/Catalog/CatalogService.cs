using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Catalog;

/// <summary>
/// The merged, deduplicated live catalog shared by the Channel, Live TV, relay and notifications.
/// Endpoint results replace the previous data of the same endpoint only when successful, so a broken API keeps its last
/// good data (or the disk snapshot) instead of emptying the catalog.
/// </summary>
public sealed class CatalogService
{
    private readonly ConfigAccessor _config;
    private readonly TimeProvider _clock;
    private readonly ILogger<CatalogService> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, ProviderData> _providers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, List<LiveStream> Streams)> _lazyStreams = new(StringComparer.Ordinal);
    private CatalogView _view = CatalogView.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogService"/> class.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">Clock (null = system).</param>
    public CatalogService(ConfigAccessor config, ILogger<CatalogService> logger, TimeProvider? clock = null)
    {
        _config = config;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Raised when the catalog changes (content or status buckets).</summary>
    public event EventHandler<CatalogChangedEventArgs>? Changed;

    /// <summary>Gets the current view.</summary>
    public CatalogView View => Volatile.Read(ref _view);

    /// <summary>Gets the clock.</summary>
    public TimeProvider Clock => _clock;

    /// <summary>Gets the configuration.</summary>
    public PluginConfiguration Config => _config.Current;

    /// <summary>Stores the successful endpoint results of a provider run.</summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="results">The endpoint results.</param>
    /// <returns>Whether at least one endpoint succeeded.</returns>
    public bool ApplyEndpointResults(string providerId, string name, IEnumerable<EndpointResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var any = false;
        lock (_lock)
        {
            var data = GetOrCreate(providerId, name);
            foreach (var r in results.Where(r => r.Success))
            {
                data.Endpoints[r.EndpointId] = r;
                any = true;
            }

            if (any)
            {
                data.Snapshot = null;
                data.UpdatedAt = _clock.GetUtcNow();
            }
        }

        return any;
    }

    /// <summary>Loads a disk snapshot for a provider (used until the first successful sync).</summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="snapshot">The snapshot.</param>
    public void ApplySnapshot(string providerId, string name, ProviderSnapshot snapshot)
    {
        lock (_lock)
        {
            var data = GetOrCreate(providerId, name);
            if (data.Endpoints.Count == 0)
            {
                data.Snapshot = snapshot;
                data.UpdatedAt = snapshot.FetchedAt;
            }
        }
    }

    /// <summary>Removes a provider (deleted or disabled).</summary>
    /// <param name="providerId">The provider.</param>
    public void RemoveProvider(string providerId)
    {
        lock (_lock)
        {
            _providers.Remove(providerId);
        }
    }

    /// <summary>Returns whether a provider is served from a disk snapshot.</summary>
    /// <param name="providerId">The provider.</param>
    /// <returns>True when only the snapshot is available.</returns>
    public bool IsFromSnapshot(string providerId)
    {
        lock (_lock)
        {
            return _providers.TryGetValue(providerId, out var d) && d.Endpoints.Count == 0 && d.Snapshot is not null;
        }
    }

    /// <summary>Builds the current snapshot of a provider (for disk persistence).</summary>
    /// <param name="providerId">The provider.</param>
    /// <returns>The snapshot or null.</returns>
    public ProviderSnapshot? GetProviderSnapshot(string providerId)
    {
        lock (_lock)
        {
            return _providers.TryGetValue(providerId, out var d) ? Flatten(d) : null;
        }
    }

    /// <summary>Returns whether a provider has events on air or starting soon (adaptive polling).</summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="within">How soon.</param>
    /// <returns>True when something is live or imminent.</returns>
    public bool HasLiveOrImminent(string providerId, TimeSpan within)
    {
        var now = _clock.GetUtcNow();
        var cfg = _config.Current;
        return View.Events.Any(e => e.SourceProviders.Contains(providerId)
            && (EventStatusCalculator.Status(e, now, cfg) == EventStatus.Live || (e.Start > now && e.Start - now <= within)));
    }

    /// <summary>Rebuilds the merged view (dedup, expiry, region rules) and raises <see cref="Changed"/> when needed.</summary>
    public void Rebuild()
    {
        var cfg = _config.Current;
        var now = _clock.GetUtcNow();
        List<ProviderSnapshot> snapshots;
        lock (_lock)
        {
            snapshots = _providers.Values.Select(Flatten).ToList();
        }

        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var allEvents = snapshots.SelectMany(s => s.Events).Where(e => !EventStatusCalculator.IsExpired(e, now, cfg)).ToList();
        foreach (var ev in allEvents)
        {
            ApplyRegionRules(ev, cfg);
        }

        var events = cfg.EnableDeduplication
            ? EventDeduplicator.Merge(allEvents, TimeSpan.FromMinutes(Math.Clamp(cfg.DedupWindowMinutes, 0, 180)), aliases)
            : allEvents;
        events = events.OrderBy(e => e.Start).ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

        var channels = snapshots.SelectMany(s => s.Channels).GroupBy(c => c.Key).Select(g => g.First()).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var programs = snapshots.SelectMany(s => s.Programs)
            .GroupBy(p => p.ChannelKey)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LiveProgram>)g.OrderBy(p => p.Start).ToList(), StringComparer.Ordinal);
        var categories = events.Select(e => e.Category).Concat(channels.Select(c => c.Category))
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();

        var view = new CatalogView
        {
            Events = events,
            Channels = channels,
            Programs = programs,
            Categories = categories,
            Aliases = aliases,
            EventIndex = events.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            ChannelIndex = channels.ToDictionary(c => c.Key, StringComparer.Ordinal),
            DataRevision = DataHash(events, channels, programs),
            DisplayRevision = DisplayHash(events, channels, now, cfg),
            BuiltAt = now,
        };

        var previous = View;
        Volatile.Write(ref _view, view);
        var dataChanged = previous.DataRevision != view.DataRevision;
        if (dataChanged || previous.DisplayRevision != view.DisplayRevision)
        {
            _logger.LogDebug("Catalog changed: {Events} events, {Channels} channels (data changed: {DataChanged})", events.Count, channels.Count, dataChanged);
            RaiseChanged(view, dataChanged);
        }

        PruneLazyStreams(now);
    }

    /// <summary>Recomputes the display revision (statuses change with time even without new data).</summary>
    public void Tick()
    {
        var cfg = _config.Current;
        var now = _clock.GetUtcNow();
        var current = View;
        if (current.Events.Any(e => EventStatusCalculator.IsExpired(e, now, cfg)))
        {
            Rebuild();
            return;
        }

        var display = DisplayHash(current.Events, current.Channels, now, cfg);
        if (display != current.DisplayRevision)
        {
            var view = new CatalogView
            {
                Events = current.Events,
                Channels = current.Channels,
                Programs = current.Programs,
                Categories = current.Categories,
                Aliases = current.Aliases,
                EventIndex = current.EventIndex,
                ChannelIndex = current.ChannelIndex,
                DataRevision = current.DataRevision,
                DisplayRevision = display,
                BuiltAt = current.BuiltAt,
            };
            Volatile.Write(ref _view, view);
            RaiseChanged(view, false);
        }
    }

    /// <summary>Finds an event by key (also merged keys).</summary>
    /// <param name="key">The key.</param>
    /// <returns>The event or null.</returns>
    public LiveEvent? FindEvent(string key)
    {
        var view = View;
        if (view.EventIndex.TryGetValue(key, out var ev))
        {
            return ev;
        }

        return view.Aliases.TryGetValue(key, out var primary) && view.EventIndex.TryGetValue(primary, out ev) ? ev : null;
    }

    /// <summary>Finds a channel by key.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The channel or null.</returns>
    public LiveChannel? FindChannel(string key) => View.ChannelIndex.TryGetValue(key, out var ch) ? ch : null;

    /// <summary>Caches streams fetched lazily for an event.</summary>
    /// <param name="eventKey">The event key.</param>
    /// <param name="streams">The streams.</param>
    /// <param name="ttl">Time to live.</param>
    public void SetLazyStreams(string eventKey, List<LiveStream> streams, TimeSpan ttl)
        => _lazyStreams[eventKey] = (_clock.GetUtcNow() + ttl, streams);

    /// <summary>Gets cached lazy streams.</summary>
    /// <param name="eventKey">The event key.</param>
    /// <returns>The streams or null when missing/expired.</returns>
    public List<LiveStream>? GetLazyStreams(string eventKey)
        => _lazyStreams.TryGetValue(eventKey, out var entry) && entry.Expires > _clock.GetUtcNow() ? entry.Streams : null;

    /// <summary>Gets every stream of an event: catalog streams first, then cached lazy streams.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>The streams.</returns>
    public List<LiveStream> StreamsOf(LiveEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var list = new List<LiveStream>(ev.Streams);
        if (GetLazyStreams(ev.Key) is { } lazy)
        {
            list.AddRange(lazy.Where(s => !list.Exists(x => x.Id == s.Id || x.Url == s.Url)));
        }

        return list;
    }

    private static string DataHash(IEnumerable<LiveEvent> events, IEnumerable<LiveChannel> channels, IReadOnlyDictionary<string, IReadOnlyList<LiveProgram>> programs)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            sb.Append(e.Key).Append('|').Append(e.Title).Append('|').Append(e.Start.ToUnixTimeSeconds()).Append('|').Append(e.End?.ToUnixTimeSeconds())
              .Append('|').Append(e.Category).Append('|').Append(e.Streams.Count).Append('|').Append(e.ThumbUrl ?? e.PosterUrl).Append('|').Append(e.IsLiveFlag)
              .Append('|').Append(e.StatusFlag).Append('|').Append(string.Join(',', e.SourceProviders)).Append('\n');
        }

        foreach (var c in channels)
        {
            sb.Append(c.Key).Append('|').Append(c.Name).Append('|').Append(c.Streams.Count).Append('|').Append(c.LogoUrl).Append('\n');
        }

        sb.Append(programs.Sum(p => p.Value.Count).ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static string DisplayHash(IEnumerable<LiveEvent> events, IEnumerable<LiveChannel> channels, DateTimeOffset now, PluginConfiguration cfg)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            sb.Append(e.Key).Append(':').Append((int)EventStatusCalculator.Bucket(e, now, cfg)).Append('|').Append(e.Title).Append('|').Append(e.Streams.Count).Append('\n');
        }

        foreach (var c in channels)
        {
            sb.Append(c.Key).Append('\n');
        }

        // The local day matters too ("Today" folder content changes at midnight).
        sb.Append(now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static void ApplyRegionRules(LiveEvent ev, PluginConfiguration cfg)
    {
        if (cfg.RegionRules.Count == 0)
        {
            return;
        }

        var haystack = string.Join(" | ", new[] { ev.Category, ev.Competition, ev.Title }.Where(s => !string.IsNullOrEmpty(s)));
        foreach (var rule in cfg.RegionRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Match))
            {
                continue;
            }

            try
            {
                if (!TransformEngine.GetRegex(rule.Match).IsMatch(haystack))
                {
                    continue;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                continue;
            }

            ev.Country ??= LocaleNormalizer.NormalizeCountry(rule.Country);
            ev.Language ??= LocaleNormalizer.NormalizeLanguage(rule.Language);
            break;
        }
    }

    private static ProviderSnapshot Flatten(ProviderData data)
    {
        if (data.Endpoints.Count == 0)
        {
            return data.Snapshot ?? new ProviderSnapshot { ProviderId = data.ProviderId };
        }

        var snapshot = new ProviderSnapshot { ProviderId = data.ProviderId, FetchedAt = data.UpdatedAt };
        var events = new Dictionary<string, LiveEvent>(StringComparer.Ordinal);
        var channels = new Dictionary<string, LiveChannel>(StringComparer.Ordinal);

        // "live" endpoints are applied last so their status flags win.
        foreach (var r in data.Endpoints.Values.OrderBy(r => r.EndpointId.Contains("live", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            snapshot.Categories.AddRange(r.Categories.Where(c => !snapshot.Categories.Exists(x => x.Id == c.Id)));
            foreach (var ev in r.Events)
            {
                if (events.TryGetValue(ev.Key, out var existing))
                {
                    existing.IsLiveFlag = ev.IsLiveFlag ?? existing.IsLiveFlag;
                    existing.StatusFlag = ev.StatusFlag ?? existing.StatusFlag;
                    foreach (var s in ev.Streams.Where(s => !existing.Streams.Exists(x => x.Id == s.Id)))
                    {
                        existing.Streams.Add(s);
                    }
                }
                else
                {
                    events[ev.Key] = ev.ShallowClone();
                }
            }

            foreach (var ch in r.Channels)
            {
                channels.TryAdd(ch.Key, ch);
            }

            snapshot.Programs.AddRange(r.Programs);
        }

        snapshot.Events.AddRange(events.Values);
        snapshot.Channels.AddRange(channels.Values);
        return snapshot;
    }

    private ProviderData GetOrCreate(string providerId, string name)
    {
        if (!_providers.TryGetValue(providerId, out var data))
        {
            data = new ProviderData { ProviderId = providerId, Name = name };
            _providers[providerId] = data;
        }

        data.Name = name;
        return data;
    }

    private void PruneLazyStreams(DateTimeOffset now)
    {
        foreach (var kv in _lazyStreams.Where(kv => kv.Value.Expires <= now).ToList())
        {
            _lazyStreams.TryRemove(kv.Key, out _);
        }
    }

    private void RaiseChanged(CatalogView view, bool dataChanged)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<CatalogChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, new CatalogChangedEventArgs(view, dataChanged));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Catalog change handler failed");
            }
        }
    }
}
