using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LiveHub.LiveTv;

/// <summary>The parts of the Jellyfin Live TV subsystem the plugin drives (abstracted for tests).</summary>
public interface ILiveTvHost
{
    /// <summary>Gets the configured listings providers.</summary>
    /// <returns>The providers.</returns>
    IReadOnlyList<ListingsProviderInfo> ListingProviders();

    /// <summary>Saves a listings provider (creates it, or invalidates Jellyfin's EPG channel cache when it exists).</summary>
    /// <param name="info">The provider.</param>
    /// <returns>A task.</returns>
    Task SaveListingProviderAsync(ListingsProviderInfo info);

    /// <summary>Queues the "Refresh Guide" scheduled task.</summary>
    /// <returns>False when the task is missing or already running.</returns>
    bool QueueGuideRefresh();
}

/// <summary>Jellyfin implementation of <see cref="ILiveTvHost"/>.</summary>
public sealed class JellyfinLiveTvHost : ILiveTvHost
{
    /// <summary>Key of Jellyfin's guide refresh task.</summary>
    public const string RefreshGuideTaskKey = "RefreshGuide";

    private readonly IConfigurationManager _configuration;
    private readonly IListingsManager _listings;
    private readonly ITaskManager _tasks;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinLiveTvHost"/> class.
    /// </summary>
    /// <param name="configuration">Configuration manager.</param>
    /// <param name="listings">Listings manager.</param>
    /// <param name="tasks">Task manager.</param>
    public JellyfinLiveTvHost(IConfigurationManager configuration, IListingsManager listings, ITaskManager tasks)
    {
        _configuration = configuration;
        _listings = listings;
        _tasks = tasks;
    }

    /// <inheritdoc />
    public IReadOnlyList<ListingsProviderInfo> ListingProviders()
        => _configuration.GetConfiguration("livetv") is LiveTvOptions options ? options.ListingProviders : Array.Empty<ListingsProviderInfo>();

    /// <inheritdoc />
    public Task SaveListingProviderAsync(ListingsProviderInfo info) => _listings.SaveListingProvider(info, false, false);

    /// <inheritdoc />
    public bool QueueGuideRefresh()
    {
        var worker = _tasks.ScheduledTasks.FirstOrDefault(w => string.Equals(w.ScheduledTask.Key, RefreshGuideTaskKey, StringComparison.Ordinal));
        if (worker is null || worker.State == TaskState.Running)
        {
            return false;
        }

        _tasks.QueueScheduledTask(worker.ScheduledTask, new TaskOptions());
        return true;
    }
}
