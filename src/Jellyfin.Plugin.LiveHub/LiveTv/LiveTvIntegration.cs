using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Configuration;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.LiveTv;

/// <summary>
/// Keeps Jellyfin's Live TV in sync without manual setup: creates the "Live Hub" guide source bound to the Live Hub
/// tuner, invalidates Jellyfin's EPG channel cache when the channel set changes and queues "Refresh Guide" (debounced,
/// at most every <see cref="PluginConfiguration.GuideRefreshMinMinutes"/>) when programs change.
/// </summary>
public sealed class LiveTvIntegration : BackgroundService
{
    private readonly LiveTvLineup _lineup;
    private readonly CatalogService _catalog;
    private readonly ILiveTvHost _host;
    private readonly ConfigAccessor _config;
    private readonly ILogger<LiveTvIntegration> _logger;
    private readonly TimeProvider _clock;
    private string? _channelSet;
    private string? _programs;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private bool _refreshPending;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvIntegration"/> class.
    /// </summary>
    /// <param name="lineup">Lineup.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="host">Live TV host.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">Clock.</param>
    public LiveTvIntegration(LiveTvLineup lineup, CatalogService catalog, ILiveTvHost host, ConfigAccessor config, ILogger<LiveTvIntegration> logger, TimeProvider? clock = null)
    {
        _lineup = lineup;
        _catalog = catalog;
        _host = host;
        _config = config;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Gets the number of guide refreshes queued (diagnostics, tests).</summary>
    public int RefreshesQueued { get; private set; }

    /// <summary>Runs one synchronisation step.</summary>
    /// <returns>A task.</returns>
    public async Task TickAsync()
    {
        if (!_config.Current.EnableLiveTv || _catalog.View.BuiltAt == default)
        {
            return;
        }

        var lineup = _lineup.Get();
        var provider = _host.ListingProviders().FirstOrDefault(p => string.Equals(p.Type, LiveTvLineup.TunerType, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            _logger.LogInformation("Creating the Live Hub guide source for the Live TV guide");
            await _host.SaveListingProviderAsync(new ListingsProviderInfo
            {
                Type = LiveTvLineup.TunerType,
                ListingsId = LiveTvLineup.TunerType,
                EnableAllTuners = false,
                EnabledTuners = new[] { LiveTvLineup.TunerType },
                ChannelMappings = Array.Empty<MediaBrowser.Model.Dto.NameValuePair>(),
            }).ConfigureAwait(false);
            _channelSet = lineup.ChannelSetVersion;
            _refreshPending = true;
        }
        else if (_channelSet != lineup.ChannelSetVersion)
        {
            if (_channelSet is not null)
            {
                // Jellyfin caches the EPG channel list per guide source: saving it again clears that cache.
                await _host.SaveListingProviderAsync(provider).ConfigureAwait(false);
            }

            _channelSet = lineup.ChannelSetVersion;
            _refreshPending = true;
        }

        if (_programs != lineup.ProgramsVersion)
        {
            _programs = lineup.ProgramsVersion;
            _refreshPending = true;
        }

        var now = _clock.GetUtcNow();
        if (_refreshPending && now - _lastRefresh >= TimeSpan.FromMinutes(Math.Max(1, _config.Current.GuideRefreshMinMinutes)))
        {
            if (_host.QueueGuideRefresh())
            {
                _logger.LogInformation("Live Hub queued a Live TV guide refresh ({Channels} channels)", lineup.Channels.Count);
                _lastRefresh = now;
                _refreshPending = false;
                RefreshesQueued++;
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let Jellyfin finish starting its Live TV services.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Live Hub Live TV synchronisation failed");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
