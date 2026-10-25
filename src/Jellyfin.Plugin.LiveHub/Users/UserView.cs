using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Localization;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Users;

/// <summary>
/// Everything needed to filter, sort and format the catalog for one user: preferences, allowed providers, time zone,
/// languages and UI language.
/// </summary>
public sealed class UserView
{
    private readonly Func<string, bool> _providerAllowed;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserView"/> class.
    /// </summary>
    /// <param name="userId">The user (empty = anonymous).</param>
    /// <param name="prefs">Preferences.</param>
    /// <param name="providerAllowed">Admin access check.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="now">Now.</param>
    public UserView(Guid userId, UserPreferences prefs, Func<string, bool> providerAllowed, PluginConfiguration config, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        ArgumentNullException.ThrowIfNull(config);
        UserId = userId;
        Prefs = prefs;
        Config = config;
        Now = now;
        _providerAllowed = providerAllowed;
        Zone = DateParser.FindZone(prefs.TimeZone) ?? DateParser.FindZone(config.DefaultTimeZone) ?? TimeZoneInfo.Local;
        var preferred = prefs.Language ?? Locale.LocaleNormalizer.NormalizeLanguage(config.DefaultLanguage) ?? "en";
        var fallbacks = prefs.FallbackLanguages.Count > 0
            ? prefs.FallbackLanguages
            : config.FallbackLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Locale.LocaleNormalizer.NormalizeLanguage).Where(l => l is not null).Select(l => l!).ToList();
        Languages = new[] { preferred }.Concat(fallbacks).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        UiLanguage = LocalizationService.Normalize(config.UiLanguage is "it" or "en" ? config.UiLanguage : (prefs.Language ?? config.DefaultLanguage));
    }

    /// <summary>Gets the user id.</summary>
    public Guid UserId { get; }

    /// <summary>Gets the preferences.</summary>
    public UserPreferences Prefs { get; }

    /// <summary>Gets the configuration.</summary>
    public PluginConfiguration Config { get; }

    /// <summary>Gets now.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>Gets the display time zone.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>Gets the preferred language followed by the fallbacks.</summary>
    public IReadOnlyList<string> Languages { get; }

    /// <summary>Gets the UI language (it/en) used for texts.</summary>
    public string UiLanguage { get; }

    /// <summary>Returns whether a provider is visible to this user (admin access + personal source selection).</summary>
    /// <param name="providerId">The provider.</param>
    /// <returns>True when visible.</returns>
    public bool ProviderVisible(string providerId)
        => _providerAllowed(providerId)
           && (Prefs.Sources.Count == 0 || Prefs.Sources.Contains(providerId, StringComparer.Ordinal))
           && !Prefs.Hidden.Contains("src:" + providerId, StringComparer.Ordinal);

    /// <summary>Gets the streams the user may use.</summary>
    /// <param name="streams">All streams.</param>
    /// <returns>The visible streams.</returns>
    public List<LiveStream> VisibleStreams(IEnumerable<LiveStream> streams)
        => streams.Where(s => ProviderVisible(s.ProviderId)).ToList();

    /// <summary>Returns whether an event is visible.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>True when visible.</returns>
    public bool IsVisible(LiveEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (!ev.SourceProviders.Any(ProviderVisible))
        {
            return false;
        }

        if (Prefs.Hidden.Contains(ev.Key, StringComparer.Ordinal)
            || (ev.Category is not null && Prefs.Hidden.Contains("cat:" + ev.Category, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Prefs.Countries.Count > 0 && ev.Country is not null && !Prefs.Countries.Contains(ev.Country, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Prefs.OnlyMyLanguage)
        {
            var streams = VisibleStreams(ev.Streams);
            var languages = streams.Count > 0 ? streams.Select(s => s.Language) : new[] { ev.Language };
            if (!languages.Any(l => l is not null && Languages.Contains(l, StringComparer.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns whether a 24/7 channel is visible.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>True when visible.</returns>
    public bool IsVisible(LiveChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!ProviderVisible(channel.ProviderId) || Prefs.Hidden.Contains(channel.Key, StringComparer.Ordinal))
        {
            return false;
        }

        if (channel.Category is not null && Prefs.Hidden.Contains("cat:" + channel.Category, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Prefs.Countries.Count > 0 && channel.Country is not null && !Prefs.Countries.Contains(channel.Country, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Prefs.OnlyMyLanguage && channel.Language is not null && !Languages.Contains(channel.Language, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>Returns whether an event matches the user's favourites (categories, teams).</summary>
    /// <param name="ev">The event.</param>
    /// <returns>True when favourite.</returns>
    public bool IsFavorite(LiveEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (ev.Category is not null && Prefs.FavoriteCategories.Contains(ev.Category, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ev.Competition is not null && Prefs.FavoriteCategories.Contains(ev.Competition, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Prefs.FavoriteTeams.Count == 0)
        {
            return false;
        }

        var names = ev.Participants.Select(p => EventDeduplicator.Normalize(p.Name)).ToList();
        var title = EventDeduplicator.Normalize(ev.Title);
        return Prefs.FavoriteTeams.Select(EventDeduplicator.Normalize).Any(team => team.Length > 0 && (names.Contains(team) || (" " + title + " ").Contains(" " + team + " ", StringComparison.Ordinal)));
    }

    /// <summary>Sorts events according to the preference.</summary>
    /// <param name="events">The events.</param>
    /// <returns>The sorted events.</returns>
    public IEnumerable<LiveEvent> Sort(IEnumerable<LiveEvent> events) => Prefs.Sort switch
    {
        SortPreference.Popularity => events.OrderByDescending(e => e.Popularity ?? 0).ThenBy(e => e.Start),
        SortPreference.Name => events.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase),
        _ => events.OrderByDescending(e => EventStatusCalculator.Status(e, Now, Config) == EventStatus.Live).ThenBy(e => e.Start),
    };

    /// <summary>Converts an instant to the user's time zone.</summary>
    /// <param name="instant">The instant.</param>
    /// <returns>The local time.</returns>
    public DateTime Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone).DateTime;
}
