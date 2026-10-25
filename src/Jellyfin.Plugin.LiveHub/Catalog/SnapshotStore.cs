using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Catalog;

/// <summary>
/// Disk snapshots of the provider data: used at cold start and while an API is unreachable.
/// Writes are throttled (at most once per minute per provider).
/// </summary>
public sealed class SnapshotStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly LiveHubPaths _paths;
    private readonly ILogger<SnapshotStore> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWrite = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotStore"/> class.
    /// </summary>
    /// <param name="paths">Paths.</param>
    /// <param name="logger">Logger.</param>
    public SnapshotStore(LiveHubPaths paths, ILogger<SnapshotStore> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Saves a snapshot (throttled unless forced).</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="force">Ignore the throttle.</param>
    public void Save(ProviderSnapshot snapshot, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var now = DateTimeOffset.UtcNow;
        if (!force && _lastWrite.TryGetValue(snapshot.ProviderId, out var last) && now - last < TimeSpan.FromMinutes(1))
        {
            return;
        }

        try
        {
            var path = PathFor(snapshot.ProviderId);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(snapshot, Json));
            File.Move(tmp, path, overwrite: true);
            _lastWrite[snapshot.ProviderId] = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to write the snapshot of {Provider}", snapshot.ProviderId);
        }
    }

    /// <summary>Loads a snapshot.</summary>
    /// <param name="providerId">The provider.</param>
    /// <returns>The snapshot or null.</returns>
    public ProviderSnapshot? Load(string providerId)
    {
        var path = PathFor(providerId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProviderSnapshot>(File.ReadAllBytes(path), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Unable to read the snapshot of {Provider}", providerId);
            return null;
        }
    }

    /// <summary>Deletes a snapshot.</summary>
    /// <param name="providerId">The provider.</param>
    public void Delete(string providerId)
    {
        try
        {
            File.Delete(PathFor(providerId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Unable to delete the snapshot of {Provider}", providerId);
        }
    }

    private string PathFor(string providerId)
    {
        if (providerId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || providerId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid provider id.", nameof(providerId));
        }

        return Path.Combine(_paths.SnapshotsDir, providerId + ".json");
    }
}
