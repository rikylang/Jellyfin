using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Http;

/// <summary>
/// Watches the proxy/VPN profiles: a TCP probe every <see cref="EgressProfile.HealthCheckSeconds"/>, the gluetun
/// tunnel status when a control server is configured, and a full public-IP check every few minutes (or immediately
/// when a profile looks down/recovered). The result drives the kill switch.
/// </summary>
public sealed class EgressHealthMonitor : BackgroundService
{
    private static readonly TimeSpan FullCheckEvery = TimeSpan.FromMinutes(5);

    private readonly EgressManager _egress;
    private readonly EgressDiagnostics _diagnostics;
    private readonly ConfigAccessor _config;
    private readonly ILogger<EgressHealthMonitor> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (DateTimeOffset Probe, DateTimeOffset Full)> _last = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="EgressHealthMonitor"/> class.
    /// </summary>
    /// <param name="egress">Egress manager.</param>
    /// <param name="diagnostics">Diagnostics.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">Clock.</param>
    public EgressHealthMonitor(EgressManager egress, EgressDiagnostics diagnostics, ConfigAccessor config, ILogger<EgressHealthMonitor> logger, TimeProvider? clock = null)
    {
        _egress = egress;
        _diagnostics = diagnostics;
        _config = config;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Checks the profiles that are due.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task CheckDueAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        foreach (var profile in _config.Current.EgressProfiles.Where(p => p.Type != EgressType.Direct).ToList())
        {
            (DateTimeOffset Probe, DateTimeOffset Full) last = _last.TryGetValue(profile.Id, out var l) ? l : (DateTimeOffset.MinValue, DateTimeOffset.MinValue);
            if (now - last.Probe < TimeSpan.FromSeconds(Math.Clamp(profile.HealthCheckSeconds, 10, 3600)))
            {
                continue;
            }

            var wasHealthy = _egress.GetHealth(profile.Id).Healthy && _egress.GetHealth(profile.Id).LastCheck is not null;
            var error = await EgressDiagnostics.ProbeTcpAsync(profile, cancellationToken).ConfigureAwait(false);
            if (error is null && await _diagnostics.GluetunRunningAsync(profile, cancellationToken).ConfigureAwait(false) == false)
            {
                error = "The gluetun tunnel is not running.";
            }

            if (error is not null)
            {
                _egress.ReportHealth(profile.Id, false, error);
                _last[profile.Id] = (now, last.Full);
                continue;
            }

            if (!wasHealthy || now - last.Full >= FullCheckEvery)
            {
                var result = await _diagnostics.CheckAsync(profile.Id, cancellationToken).ConfigureAwait(false);
                _last[profile.Id] = (now, now);
                if (result.Ok && !result.CountryMatches)
                {
                    _logger.LogWarning("Egress profile {Profile} exits in {Country}, expected {Expected}", profile.Id, result.Country, result.Expected);
                }
            }
            else
            {
                _egress.ReportHealth(profile.Id, true, null);
                _last[profile.Id] = (now, last.Full);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckDueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Egress health check failed");
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
