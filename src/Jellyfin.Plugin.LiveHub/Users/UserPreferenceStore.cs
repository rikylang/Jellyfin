using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Users;

/// <summary>
/// Stores the preferences of each Jellyfin user (one JSON file per UserId).
/// </summary>
public sealed class UserPreferenceStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly LiveHubPaths _paths;
    private readonly ILogger<UserPreferenceStore> _logger;
    private readonly ConcurrentDictionary<Guid, UserPreferences> _cache = new();
    private readonly object _ioLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="UserPreferenceStore"/> class.
    /// </summary>
    /// <param name="paths">Paths.</param>
    /// <param name="logger">Logger.</param>
    public UserPreferenceStore(LiveHubPaths paths, ILogger<UserPreferenceStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Raised when a user saves preferences.</summary>
    public event EventHandler<Guid>? Changed;

    /// <summary>Gets the preferences of a user (defaults when none).</summary>
    /// <param name="userId">The user id (empty = anonymous defaults).</param>
    /// <returns>The preferences (do not mutate).</returns>
    public UserPreferences Get(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return new UserPreferences();
        }

        return _cache.GetOrAdd(userId, Load);
    }

    /// <summary>Saves the preferences of a user (normalizes languages and countries).</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="prefs">The preferences.</param>
    /// <returns>The saved preferences.</returns>
    public UserPreferences Save(Guid userId, UserPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user is required.", nameof(userId));
        }

        prefs.Language = LocaleNormalizer.NormalizeLanguage(prefs.Language);
        prefs.FallbackLanguages = prefs.FallbackLanguages.Select(LocaleNormalizer.NormalizeLanguage).Where(l => l is not null).Select(l => l!).Distinct().ToList();
        prefs.Countries = prefs.Countries.Select(LocaleNormalizer.NormalizeCountry).Where(c => c is not null).Select(c => c!).Distinct().ToList();
        if (prefs.TimeZone is { Length: > 0 } tz && Engine.DateParser.FindZone(tz) is null)
        {
            prefs.TimeZone = null;
        }

        prefs.MaxBitrateKbps = prefs.MaxBitrateKbps is > 0 ? Math.Clamp(prefs.MaxBitrateKbps.Value, 200, 200_000) : null;
        prefs.Notifications.LeadMinutes = Math.Clamp(prefs.Notifications.LeadMinutes, 0, 120);
        prefs.Version = Get(userId).Version + 1;

        lock (_ioLock)
        {
            var path = PathFor(userId);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(prefs, Json));
            File.Move(tmp, path, overwrite: true);
        }

        _cache[userId] = prefs;
        Changed?.Invoke(this, userId);
        return prefs;
    }

    /// <summary>Gets every user that saved preferences.</summary>
    /// <returns>The user ids.</returns>
    public System.Collections.Generic.IReadOnlyList<Guid> Users()
    {
        try
        {
            return Directory.EnumerateFiles(_paths.UsersDir, "*.json")
                .Select(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<Guid>();
        }
    }

    private UserPreferences Load(Guid userId)
    {
        var path = PathFor(userId);
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<UserPreferences>(File.ReadAllBytes(path), Json) ?? new UserPreferences();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to read the Live Hub preferences of user {User}", userId);
        }

        return new UserPreferences();
    }

    private string PathFor(Guid userId) => Path.Combine(_paths.UsersDir, userId.ToString("N", CultureInfo.InvariantCulture) + ".json");
}
