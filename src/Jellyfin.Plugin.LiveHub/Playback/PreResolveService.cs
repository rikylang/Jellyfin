using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Users;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Playback;

/// <summary>
/// Pre-resolves (lazy chains + resolver) the streams of favourite and popular events that are live or start within a
/// few minutes, so that playback starts quickly. Runs every minute, a few items at a time.
/// </summary>
public sealed class PreResolveService : BackgroundService
{
    private readonly CatalogService _catalog;
    private readonly EventStreamService _events;
    private readonly StreamResolverService _resolver;
    private readonly UserPreferenceStore _prefs;
    private readonly UserViewFactory _views;
    private readonly ConfigAccessor _config;
    private readonly ILogger<PreResolveService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreResolveService"/> class.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="events">Event streams.</param>
    /// <param name="resolver">Resolver.</param>
    /// <param name="prefs">Preferences.</param>
    /// <param name="views">User views.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="logger">Logger.</param>
    public PreResolveService(CatalogService catalog, EventStreamService events, StreamResolverService resolver, UserPreferenceStore prefs, UserViewFactory views, ConfigAccessor config, ILogger<PreResolveService> logger)
    {
        _catalog = catalog;
        _events = events;
        _resolver = resolver;
        _prefs = prefs;
        _views = views;
        _config = config;
        _logger = logger;
    }

    /// <summary>Selects the events to pre-resolve.</summary>
    /// <param name="events">Catalog events.</param>
    /// <param name="favourites">Favourite matcher.</param>
    /// <param name="now">Now.</param>
    /// <param name="config">Configuration.</param>
    /// <returns>The events.</returns>
    public static List<LiveEvent> Targets(IEnumerable<LiveEvent> events, Func<LiveEvent, bool> favourites, DateTimeOffset now, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(favourites);
        ArgumentNullException.ThrowIfNull(config);
        var horizon = now.AddMinutes(Math.Clamp(config.PreResolveMinutes, 0, 120));
        var soon = events.Where(e => e.Start <= horizon && EventStatusCalculator.Status(e, now, config) != EventStatus.Ended).ToList();
        var popular = soon.OrderByDescending(e => e.Popularity ?? 0).Take(Math.Max(0, config.PreResolveTopPopular));
        return soon.Where(favourites).Concat(popular).DistinctBy(e => e.Key).Take(20).ToList();
    }

    /// <summary>Runs one pass.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The number of streams resolved.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var cfg = _config.Current;
        if (!cfg.PreResolveEnabled)
        {
            return 0;
        }

        var views = _prefs.Users().Select(_views.Create).ToList();
        var now = _catalog.Clock.GetUtcNow();
        var targets = Targets(_catalog.View.Events, e => views.Exists(v => v.IsFavorite(e)), now, cfg);
        var resolved = 0;
        foreach (var ev in targets)
        {
            var streams = await _events.GetStreamsAsync(ev, cancellationToken).ConfigureAwait(false);
            foreach (var stream in streams.OrderByDescending(s => s.Priority).Take(2))
            {
                if (_resolver.Peek(stream) is not null)
                {
                    continue;
                }

                try
                {
                    await _resolver.ResolveAsync(stream, false, cancellationToken).ConfigureAwait(false);
                    resolved++;
                }
                catch (Exception ex) when (ex is ResolveException or Http.EgressUnavailableException)
                {
                    _logger.LogDebug("Pre-resolve of {Event} failed: {Error}", ev.Title, ex.Message);
                }
            }
        }

        return resolved;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Live Hub pre-resolve pass failed");
                }

                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
