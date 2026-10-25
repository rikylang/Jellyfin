using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LiveHub.Sync;

/// <summary>
/// Scheduled task "Live Hub: synchronise sources" (Dashboard → Scheduled tasks). Discovered by Jellyfin through type
/// scanning; the background scheduler does the real-time polling, this task forces a full refresh.
/// </summary>
public class SyncAllTask : IScheduledTask
{
    private readonly SyncScheduler _scheduler;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncAllTask"/> class.
    /// </summary>
    /// <param name="scheduler">The scheduler.</param>
    public SyncAllTask(SyncScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    /// <inheritdoc />
    public string Name => "Live Hub: synchronise sources";

    /// <inheritdoc />
    public string Key => "LiveHubSyncAll";

    /// <inheritdoc />
    public string Description => "Downloads events, channels and guides from every enabled Live Hub provider.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _scheduler.SyncAllAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(30).Ticks },
        };
    }
}
