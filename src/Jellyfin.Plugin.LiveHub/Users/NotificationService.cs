using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Localization;
using Jellyfin.Plugin.LiveHub.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Users;

/// <summary>
/// Optional notifications: when a favourite event (team or category) is about to start or goes live, a native
/// DisplayMessage is shown on the user's active sessions — never on sessions that are playing something.
/// </summary>
public sealed class NotificationService : BackgroundService
{
    private readonly CatalogService _catalog;
    private readonly UserPreferenceStore _prefs;
    private readonly UserViewFactory _views;
    private readonly ISessionSink _sessions;
    private readonly LocalizationService _loc;
    private readonly ConfigAccessor _config;
    private readonly ILogger<NotificationService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sent = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationService"/> class.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="prefs">Preferences.</param>
    /// <param name="views">User views.</param>
    /// <param name="sessions">Sessions.</param>
    /// <param name="loc">Localization.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="logger">Logger.</param>
    public NotificationService(CatalogService catalog, UserPreferenceStore prefs, UserViewFactory views, ISessionSink sessions, LocalizationService loc, ConfigAccessor config, ILogger<NotificationService> logger)
    {
        _catalog = catalog;
        _prefs = prefs;
        _views = views;
        _sessions = sessions;
        _loc = loc;
        _config = config;
        _logger = logger;
    }

    /// <summary>Runs one pass.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The number of messages sent.</returns>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var cfg = _config.Current;
        if (!cfg.EnableNotifications)
        {
            return 0;
        }

        var now = _catalog.Clock.GetUtcNow();
        var sent = 0;
        foreach (var group in _sessions.Sessions().GroupBy(s => s.UserId))
        {
            var prefs = _prefs.Get(group.Key);
            var n = prefs.Notifications;
            if (!n.Enabled || (prefs.FavoriteTeams.Count == 0 && prefs.FavoriteCategories.Count == 0))
            {
                continue;
            }

            var targets = group.Where(s => !s.Playing && s.CanDisplayMessage).ToList();
            if (targets.Count == 0)
            {
                continue;
            }

            var view = _views.Create(group.Key);
            var lead = TimeSpan.FromMinutes(Math.Clamp(n.LeadMinutes, 0, 120));
            foreach (var ev in _catalog.View.Events)
            {
                if (ev.Start - lead > now || ev.Start + TimeSpan.FromMinutes(30) < now || !view.IsVisible(ev) || !Matches(ev, view, n))
                {
                    continue;
                }

                var status = EventStatusCalculator.Status(ev, now, cfg);
                if (status == EventStatus.Ended)
                {
                    continue;
                }

                var key = group.Key.ToString("N") + "|" + ev.Key;
                if (_sent.ContainsKey(key))
                {
                    continue;
                }

                var minutes = (int)Math.Ceiling((ev.Start - now).TotalMinutes);
                var text = status == EventStatus.Live || minutes <= 0
                    ? _loc.Format(view.UiLanguage, "notify.live", ChannelText.Shorten(ev.Title, 60))
                    : _loc.Format(view.UiLanguage, "notify.soon", ChannelText.Shorten(ev.Title, 60), minutes);
                if (!string.IsNullOrEmpty(ev.Competition))
                {
                    text += " · " + ev.Competition;
                }

                foreach (var session in targets)
                {
                    try
                    {
                        await _sessions.SendAsync(session.Id, "Live Hub", text, 8000, cancellationToken).ConfigureAwait(false);
                        sent++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug("Notification to session {Session} failed: {Error}", session.Id, ex.Message);
                    }
                }

                _sent[key] = now;
            }
        }

        foreach (var old in _sent.Where(kv => now - kv.Value > TimeSpan.FromDays(1)).Select(kv => kv.Key).ToList())
        {
            _sent.TryRemove(old, out _);
        }

        return sent;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Live Hub notifications failed");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private static bool Matches(LiveEvent ev, UserView view, NotificationPreferences n)
    {
        var prefs = view.Prefs;
        if (n.Categories && ((ev.Category is not null && prefs.FavoriteCategories.Contains(ev.Category, StringComparer.OrdinalIgnoreCase))
            || (ev.Competition is not null && prefs.FavoriteCategories.Contains(ev.Competition, StringComparer.OrdinalIgnoreCase))))
        {
            return true;
        }

        if (!n.Teams || prefs.FavoriteTeams.Count == 0)
        {
            return false;
        }

        var teamsOnly = new UserPreferences { FavoriteTeams = prefs.FavoriteTeams };
        return new UserView(view.UserId, teamsOnly, _ => true, view.Config, view.Now).IsFavorite(ev);
    }
}
