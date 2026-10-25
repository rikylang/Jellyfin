using System.IO;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.LiveHub.Storage;

/// <summary>
/// File locations used by the plugin. Durable data lives under the plugin configuration folder (included in Jellyfin
/// backups); disposable data (snapshots, images) under the cache folder.
/// </summary>
public class LiveHubPaths
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LiveHubPaths"/> class.
    /// </summary>
    /// <param name="appPaths">Jellyfin paths.</param>
    public LiveHubPaths(IApplicationPaths appPaths)
        : this(Path.Combine(appPaths.PluginConfigurationsPath, "LiveHub"), Path.Combine(appPaths.CachePath, "livehub"))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveHubPaths"/> class with explicit folders (tests).
    /// </summary>
    /// <param name="dataDir">Durable data folder.</param>
    /// <param name="cacheDir">Cache folder.</param>
    public LiveHubPaths(string dataDir, string cacheDir)
    {
        DataDir = dataDir;
        CacheDir = cacheDir;
    }

    /// <summary>Gets the durable data folder.</summary>
    public string DataDir { get; }

    /// <summary>Gets the cache folder.</summary>
    public string CacheDir { get; }

    /// <summary>Gets the provider definitions folder.</summary>
    public string ProvidersDir => Ensure(Path.Combine(DataDir, "providers"));

    /// <summary>Gets the per-user preferences folder.</summary>
    public string UsersDir => Ensure(Path.Combine(DataDir, "users"));

    /// <summary>Gets the state folder (slot assignments, channel numbers).</summary>
    public string StateDir => Ensure(Path.Combine(DataDir, "state"));

    /// <summary>Gets the master key file.</summary>
    public string KeyFile => Path.Combine(Ensure(DataDir), "livehub.key");

    /// <summary>Gets the snapshot folder.</summary>
    public string SnapshotsDir => Ensure(Path.Combine(CacheDir, "snapshots"));

    /// <summary>Gets the generated images folder.</summary>
    public string ImagesDir => Ensure(Path.Combine(CacheDir, "images"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
