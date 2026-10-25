using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Locale;

namespace Jellyfin.Plugin.LiveHub.Users;

/// <summary>Personal preferences page data and the per-user source access edited by administrators.</summary>
public sealed class UserSettingsService
{
    private static readonly string[] CommonLanguages = { "it", "en", "es", "fr", "de", "pt", "nl", "ar", "tr", "pl" };

    private readonly UserPreferenceStore _prefs;
    private readonly UserViewFactory _views;
    private readonly CatalogService _catalog;
    private readonly DefinitionStore _store;
    private readonly ConfigAccessor _config;
    private readonly IUserDirectory _users;
    private readonly AccessPolicy _access;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserSettingsService"/> class.
    /// </summary>
    /// <param name="prefs">Preferences.</param>
    /// <param name="views">User views.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="store">Definitions.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="users">Jellyfin users.</param>
    /// <param name="access">Admin access policy.</param>
    public UserSettingsService(UserPreferenceStore prefs, UserViewFactory views, CatalogService catalog, DefinitionStore store, ConfigAccessor config, IUserDirectory users, AccessPolicy access)
    {
        _prefs = prefs;
        _views = views;
        _catalog = catalog;
        _store = store;
        _config = config;
        _users = users;
        _access = access;
    }

    /// <summary>Builds the preferences page of a user.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The page data.</returns>
    public UserPreferencesPage GetPage(Guid userId)
    {
        var view = _views.Create(userId);
        var lang = view.UiLanguage;
        var page = new UserPreferencesPage
        {
            Preferences = _prefs.Get(userId),
            TimeZone = view.Zone.Id,
            NotificationsAvailable = _config.Current.EnableNotifications,
        };

        // Only providers the administrator allows (the user's own source selection is applied on top).
        var catalog = _catalog.View;
        foreach (var def in _store.List().Where(d => d.Enabled && _access.IsAllowed(userId, d.Id)).OrderBy(d => d.Priority).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            page.Providers.Add(new PreferenceOption(def.Id, def.Name));
        }

        page.Categories.AddRange(catalog.Categories);
        var languages = catalog.Events.SelectMany(e => e.Streams.Select(s => s.Language).Append(e.Language))
            .Concat(catalog.Channels.Select(c => c.Language))
            .Concat(CommonLanguages)
            .Where(l => !string.IsNullOrEmpty(l)).Select(l => l!).Distinct(StringComparer.OrdinalIgnoreCase);
        page.Languages.AddRange(languages.Select(l => new PreferenceOption(l, LocaleNormalizer.LanguageName(l, lang))).OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase));
        var countries = catalog.Events.Select(e => e.Country).Concat(catalog.Channels.Select(c => c.Country)).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!).Distinct(StringComparer.OrdinalIgnoreCase);
        page.Countries.AddRange(countries.Select(c => new PreferenceOption(c, CountryName(c))).OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase));
        page.Channels.AddRange(catalog.Channels.Where(c => IsAllowedByAdmin(userId, c.ProviderId)).Select(c => new PreferenceOption(c.Key, c.Name)));
        page.TimeZones.AddRange(TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Order(StringComparer.Ordinal));
        return page;
    }

    /// <summary>Saves the preferences of a user (unknown providers/channels are dropped).</summary>
    /// <param name="userId">The user.</param>
    /// <param name="prefs">The preferences.</param>
    /// <returns>The saved preferences.</returns>
    public UserPreferences Save(Guid userId, UserPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        prefs.Sources = prefs.Sources.Where(s => IsAllowedByAdmin(userId, s)).Distinct().ToList();
        prefs.FavoriteTeams = prefs.FavoriteTeams.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        prefs.FavoriteCategories = prefs.FavoriteCategories.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
        prefs.Hidden = prefs.Hidden.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().Take(1000).ToList();
        return _prefs.Save(userId, prefs);
    }

    /// <summary>Gets the access settings of every Jellyfin user.</summary>
    /// <returns>The users.</returns>
    public List<UserAccessDto> GetAccess()
    {
        var rules = _config.Current.UserAccess;
        return _users.Users().Select(u =>
        {
            var rule = rules.Find(r => Guid.TryParse(r.UserId, out var g) && g == u.Id);
            return new UserAccessDto
            {
                Id = u.Id,
                Name = u.Name,
                Restricted = rule?.Restricted ?? false,
                AllowedProviders = (rule?.AllowedProviders ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            };
        }).ToList();
    }

    /// <summary>Saves the access settings (only existing users and providers are kept).</summary>
    /// <param name="access">The settings.</param>
    public void SaveAccess(IEnumerable<UserAccessDto> access)
    {
        ArgumentNullException.ThrowIfNull(access);
        var users = _users.Users().Select(u => u.Id).ToHashSet();
        var providers = _store.List().Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        _config.Current.UserAccess = access
            .Where(a => users.Contains(a.Id) && (a.Restricted || a.AllowedProviders.Count > 0))
            .Select(a => new UserAccessRule
            {
                UserId = a.Id.ToString("N"),
                Restricted = a.Restricted,
                AllowedProviders = string.Join(',', a.AllowedProviders.Where(providers.Contains).Distinct()),
            })
            .ToList();
        _config.Save();
    }

    private static string CountryName(string iso)
    {
        try
        {
            return new System.Globalization.RegionInfo(iso).NativeName;
        }
        catch (ArgumentException)
        {
            return iso;
        }
    }

    private bool IsAllowedByAdmin(Guid userId, string providerId) => _access.IsAllowed(userId, providerId);
}
