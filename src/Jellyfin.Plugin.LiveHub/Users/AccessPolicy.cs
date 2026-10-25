using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Definitions;

namespace Jellyfin.Plugin.LiveHub.Users;

/// <summary>
/// Which providers a user may see (decided by the administrator). The anonymous view (no user, e.g. server side
/// refreshes) only contains providers visible to every restricted user.
/// </summary>
public sealed class AccessPolicy
{
    private readonly ConfigAccessor _config;
    private readonly DefinitionStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessPolicy"/> class.
    /// </summary>
    /// <param name="config">Configuration.</param>
    /// <param name="store">Definitions.</param>
    public AccessPolicy(ConfigAccessor config, DefinitionStore store)
    {
        _config = config;
        _store = store;
    }

    /// <summary>Returns whether a user may see a provider.</summary>
    /// <param name="userId">The user (empty = anonymous).</param>
    /// <param name="providerId">The provider.</param>
    /// <returns>True when allowed.</returns>
    public bool IsAllowed(Guid userId, string providerId)
    {
        var def = _store.Get(providerId);
        if (def is null || !def.Enabled)
        {
            return false;
        }

        var rules = _config.Current.UserAccess.Where(r => r.Restricted).ToList();
        if (userId == Guid.Empty)
        {
            return rules.TrueForAll(r => Allowed(r).Contains(providerId));
        }

        var key = userId.ToString("N", CultureInfo.InvariantCulture);
        var rule = rules.FirstOrDefault(r => string.Equals(r.UserId.Replace("-", string.Empty, StringComparison.Ordinal), key, StringComparison.OrdinalIgnoreCase));
        return rule is null || Allowed(rule).Contains(providerId);
    }

    /// <summary>Gets a fingerprint of the access rules of a user (channel cache key).</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The fingerprint.</returns>
    public string Fingerprint(Guid userId)
    {
        var allowed = _store.List().Where(d => IsAllowed(userId, d.Id)).Select(d => d.Id);
        return Engine.Keys.Hash(string.Join(',', allowed))[..8];
    }

    private static HashSet<string> Allowed(UserAccessRule rule)
        => rule.AllowedProviders.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
}
