using System;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Configuration;

namespace Jellyfin.Plugin.LiveHub.Users;

/// <summary>Builds <see cref="UserView"/> instances.</summary>
public sealed class UserViewFactory
{
    private readonly UserPreferenceStore _prefs;
    private readonly AccessPolicy _access;
    private readonly ConfigAccessor _config;
    private readonly CatalogService _catalog;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserViewFactory"/> class.
    /// </summary>
    /// <param name="prefs">Preferences.</param>
    /// <param name="access">Access policy.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="catalog">Catalog (clock).</param>
    public UserViewFactory(UserPreferenceStore prefs, AccessPolicy access, ConfigAccessor config, CatalogService catalog)
    {
        _prefs = prefs;
        _access = access;
        _config = config;
        _catalog = catalog;
    }

    /// <summary>Gets the preference store.</summary>
    public UserPreferenceStore Preferences => _prefs;

    /// <summary>Gets the access policy.</summary>
    public AccessPolicy Access => _access;

    /// <summary>Creates the view of a user.</summary>
    /// <param name="userId">The user (empty = anonymous).</param>
    /// <returns>The view.</returns>
    public UserView Create(Guid userId)
        => new(userId, _prefs.Get(userId), p => _access.IsAllowed(userId, p), _config.Current, _catalog.Clock.GetUtcNow());
}
