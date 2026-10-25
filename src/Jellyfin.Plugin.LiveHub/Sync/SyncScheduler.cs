using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Sync;

/// <summary>
/// Real-time synchronisation: adaptive polling per endpoint (live 30-60 s, today 5 min, calendar 30 min by default),
/// exponential backoff with jitter (Retry-After honoured), at most 4 providers in parallel, disk snapshots, status ticks.
/// A broken provider never delays the others (independent tasks, circuit breaker in its gateway).
/// </summary>
public sealed class SyncScheduler : BackgroundService
{
    private const string WholeProvider = "*";
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    private readonly DefinitionStore _store;
    private readonly ProviderEngine _engine;
    private readonly CatalogService _catalog;
    private readonly SnapshotStore _snapshots;
    private readonly ConfigAccessor _config;
    private readonly ILogger<SyncScheduler> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, ProviderSchedule> _schedules = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _parallel = new(4, 4);
    private readonly Lock _scheduleLock = new();
    private readonly Channel<bool> _wakeUp = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private DateTimeOffset _lastStatusTick;
    private CancellationToken _stopping;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncScheduler"/> class.
    /// </summary>
    /// <param name="store">Definitions.</param>
    /// <param name="engine">Engine.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="snapshots">Snapshots.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="logger">Logger.</param>
    public SyncScheduler(DefinitionStore store, ProviderEngine engine, CatalogService catalog, SnapshotStore snapshots, ConfigAccessor config, ILogger<SyncScheduler> logger)
    {
        _store = store;
        _engine = engine;
        _catalog = catalog;
        _snapshots = snapshots;
        _config = config;
        _logger = logger;
        _clock = engine.Clock;
    }

    /// <summary>Raised after every provider run (diagnostics, notifications, pre-resolve).</summary>
    public event EventHandler<string>? ProviderSynced;

    /// <summary>Gets the schedule state (diagnostics).</summary>
    /// <returns>Provider id → next due time.</returns>
    public IReadOnlyDictionary<string, DateTimeOffset> NextRuns()
        => _schedules.ToDictionary(kv => kv.Key, kv => kv.Value.NextDue.Values.DefaultIfEmpty(DateTimeOffset.MaxValue).Min(), StringComparer.Ordinal);

    /// <summary>Requests an immediate run of one provider (or all).</summary>
    /// <param name="providerId">The provider, or null for all.</param>
    public void Trigger(string? providerId)
    {
        foreach (var kv in _schedules.Where(kv => providerId is null || kv.Key == providerId))
        {
            lock (_scheduleLock)
            {
                foreach (var key in kv.Value.NextDue.Keys.ToList())
                {
                    kv.Value.NextDue[key] = DateTimeOffset.MinValue;
                }

                kv.Value.RetryAt = DateTimeOffset.MinValue;
            }
        }

        if (providerId is not null && !_schedules.ContainsKey(providerId))
        {
            _schedules[providerId] = new ProviderSchedule();
        }

        _wakeUp.Writer.TryWrite(true);
    }

    /// <summary>Runs every enabled provider now and waits for completion (scheduled task, tests).</summary>
    /// <param name="progress">Progress (0-100).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task SyncAllAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var defs = _store.List().Where(d => d.Enabled).ToList();
        var done = 0;
        await Task.WhenAll(defs.Select(async d =>
        {
            await RunProviderAsync(d, null, cancellationToken).ConfigureAwait(false);
            progress?.Report(100.0 * Interlocked.Increment(ref done) / Math.Max(1, defs.Count));
        })).ConfigureAwait(false);
        _catalog.Rebuild();
        progress?.Report(100);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (var def in _store.List())
        {
            if (_catalog.GetProviderSnapshot(def.Id) is { } snap && snap.FetchedAt != default)
            {
                _snapshots.Save(snap, force: true);
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _parallel.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        try
        {
            LoadSnapshots();
            _store.Changed += OnDefinitionChanged;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    ScheduleDueRuns();
                    var now = _clock.GetUtcNow();
                    if (now - _lastStatusTick >= TimeSpan.FromSeconds(30))
                    {
                        _lastStatusTick = now;
                        _catalog.Tick();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Live Hub scheduler loop error");
                }

                using var delay = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                delay.CancelAfter(TickInterval);
                try
                {
                    await _wakeUp.Reader.ReadAsync(delay.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Tick elapsed.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _store.Changed -= OnDefinitionChanged;
        }
    }

    /// <summary>Computes the polling interval of an endpoint.</summary>
    /// <param name="def">The definition.</param>
    /// <param name="endpoint">The endpoint (null for whole-provider kinds).</param>
    /// <param name="liveOrImminent">Whether the provider has live/imminent events.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>The interval.</returns>
    public static TimeSpan Interval(ProviderDefinition def, EndpointSpec? endpoint, bool liveOrImminent, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(config);
        var live = TimeSpan.FromSeconds(Math.Clamp(def.Refresh?.LiveSeconds ?? config.LiveRefreshSeconds, 15, 3600));
        var today = TimeSpan.FromSeconds(Math.Clamp(def.Refresh?.TodaySeconds ?? config.TodayRefreshSeconds, 30, 21600));
        var all = TimeSpan.FromSeconds(Math.Clamp(def.Refresh?.AllSeconds ?? config.FullRefreshSeconds, 60, 86400));
        var adaptive = def.Refresh?.Adaptive != false;

        switch (def.Kind)
        {
            case ProviderKind.M3u:
                return TimeSpan.FromMinutes(Math.Clamp(def.M3u?.RefreshMinutes ?? 360, 5, 10080));
            case ProviderKind.Xtream:
                return TimeSpan.FromMinutes(Math.Clamp(def.Xtream?.RefreshMinutes ?? 360, 5, 10080));
            case ProviderKind.Direct:
                return TimeSpan.FromHours(12);
        }

        if (endpoint?.RefreshSeconds is int explicitSeconds)
        {
            return TimeSpan.FromSeconds(Math.Clamp(explicitSeconds, 15, 86400));
        }

        return endpoint?.Scope switch
        {
            EndpointScope.Live => adaptive && !liveOrImminent ? Min(live * 3, TimeSpan.FromMinutes(5)) : live,
            EndpointScope.Today => adaptive && liveOrImminent ? Min(today, live * 2) : today,
            EndpointScope.Upcoming => all,

            // A single "all" list is the only source of truth: keep it fresh (cheap with ETag), slower when nothing is on.
            _ => adaptive && !liveOrImminent ? live * 2 : live,
        };
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void LoadSnapshots()
    {
        foreach (var def in _store.List().Where(d => d.Enabled))
        {
            if (_snapshots.Load(def.Id) is { } snapshot)
            {
                _catalog.ApplySnapshot(def.Id, def.Name, snapshot);
                _engine.GetGateway(def.Id).Stats.FromSnapshot = true;
                _logger.LogInformation("Loaded snapshot of {Provider} ({Events} events, {Channels} channels)", def.Id, snapshot.Events.Count, snapshot.Channels.Count);
            }
        }

        _catalog.Rebuild();
    }

    private void OnDefinitionChanged(object? sender, string providerId)
    {
        var def = _store.Get(providerId);
        if (def is null || !def.Enabled)
        {
            _catalog.RemoveProvider(providerId);
            _schedules.TryRemove(providerId, out _);
            if (def is null)
            {
                _snapshots.Delete(providerId);
                _engine.Forget(providerId);
            }

            _catalog.Rebuild();
            return;
        }

        // New or edited: forget previous data shape and run immediately.
        _schedules[providerId] = new ProviderSchedule();
        _engine.GetGateway(providerId).ClearConditionalCache();
        Trigger(providerId);
    }

    private void ScheduleDueRuns()
    {
        var now = _clock.GetUtcNow();
        var cfg = _config.Current;
        foreach (var def in _store.List().Where(d => d.Enabled))
        {
            var schedule = _schedules.GetOrAdd(def.Id, _ => new ProviderSchedule());
            List<string> due;
            lock (_scheduleLock)
            {
                if (schedule.Running || now < schedule.RetryAt)
                {
                    continue;
                }

                var keys = EndpointKeys(def);
                foreach (var k in keys.Where(k => !schedule.NextDue.ContainsKey(k)))
                {
                    schedule.NextDue[k] = DateTimeOffset.MinValue;
                }

                foreach (var stale in schedule.NextDue.Keys.Except(keys).ToList())
                {
                    schedule.NextDue.Remove(stale);
                }

                due = schedule.NextDue.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
                if (due.Count == 0)
                {
                    continue;
                }

                schedule.Running = true;
            }

            var ids = due.Contains(WholeProvider) ? null : due.ToHashSet(StringComparer.Ordinal);
            _ = Task.Run(() => RunProviderAsync(def, ids, _stopping), _stopping);
        }

        _ = cfg;
    }

    private static List<string> EndpointKeys(ProviderDefinition def)
    {
        if (def.Kind != ProviderKind.Rest)
        {
            return new List<string> { WholeProvider };
        }

        var keys = new List<string>();
        if (def.Categories is not null)
        {
            keys.Add("categories");
        }

        keys.AddRange((def.Events ?? new List<EndpointSpec>()).Select((e, i) => RestRunner.EndpointId(e, "events", i)));
        keys.AddRange((def.Channels ?? new List<EndpointSpec>()).Select((e, i) => RestRunner.EndpointId(e, "channels", i)));
        return keys;
    }

    private async Task RunProviderAsync(ProviderDefinition def, IReadOnlySet<string>? endpointIds, CancellationToken cancellationToken)
    {
        var schedule = _schedules.GetOrAdd(def.Id, _ => new ProviderSchedule());
        lock (_scheduleLock)
        {
            schedule.Running = true;
        }

        var gateway = _engine.GetGateway(def.Id);
        var stats = gateway.Stats;
        try
        {
            await _parallel.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var plain = _store.GetForRun(def.Id);
                if (plain is null)
                {
                    return;
                }

                stats.LastSyncAt = _clock.GetUtcNow();
                var result = await _engine.RunAsync(plain, new RunOptions { EndpointIds = endpointIds }, cancellationToken).ConfigureAwait(false);
                var anySuccess = _catalog.ApplyEndpointResults(def.Id, def.Name, result.Endpoints);
                var failed = result.Endpoints.Where(e => !e.Success).ToList();
                var now = _clock.GetUtcNow();
                var liveOrImminent = _catalog.HasLiveOrImminent(def.Id, TimeSpan.FromHours(2));

                lock (_scheduleLock)
                {
                    foreach (var key in endpointIds ?? (IEnumerable<string>)schedule.NextDue.Keys.ToList())
                    {
                        var spec = FindEndpoint(plain, key);
                        var r = result.Endpoints.FirstOrDefault(e => e.EndpointId == key || key == WholeProvider);
                        var ok = r?.Success ?? anySuccess;
                        schedule.NextDue[key] = ok
                            ? now + Interval(plain, spec, liveOrImminent, _config.Current)
                            : now + RetryDelay(schedule.Failures + 1, r?.RetryAfter);
                    }

                    if (failed.Count == 0 && result.Diagnostics.Errors.Count == 0)
                    {
                        schedule.Failures = 0;
                    }
                    else
                    {
                        schedule.Failures++;
                        if (!anySuccess)
                        {
                            schedule.RetryAt = now + RetryDelay(schedule.Failures, failed.Select(f => f.RetryAfter).Max());
                        }
                    }
                }

                stats.ConsecutiveFailures = schedule.Failures;
                if (anySuccess)
                {
                    stats.LastSuccessAt = now;
                    stats.FromSnapshot = false;
                    gateway.Breaker.Success();
                }

                if (result.Diagnostics.Errors.Count > 0)
                {
                    stats.LastError = string.Join(" | ", result.Diagnostics.Errors.Take(3));
                    stats.LastErrorAt = now;
                    _logger.LogWarning("Live Hub provider {Provider}: {Errors}", def.Id, stats.LastError);
                    if (!anySuccess)
                    {
                        gateway.Breaker.Failure();
                    }
                }
                else if (anySuccess)
                {
                    stats.LastError = null;
                }

                stats.Circuit = gateway.Breaker.State.ToString();
                if (anySuccess && _catalog.GetProviderSnapshot(def.Id) is { } snapshot)
                {
                    stats.Events = snapshot.Events.Count;
                    stats.Channels = snapshot.Channels.Count;
                    stats.Programs = snapshot.Programs.Count;
                    stats.Streams = snapshot.Events.Sum(e => e.Streams.Count) + snapshot.Channels.Sum(c => c.Streams.Count);
                    _snapshots.Save(snapshot);
                }

                _catalog.Rebuild();
            }
            finally
            {
                _parallel.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Never let a provider crash the scheduler.
            _logger.LogError(ex, "Live Hub provider {Provider} failed", def.Id);
            lock (_scheduleLock)
            {
                schedule.Failures++;
                schedule.RetryAt = _clock.GetUtcNow() + RetryDelay(schedule.Failures, null);
            }

            stats.LastError = ex.Message;
            stats.LastErrorAt = _clock.GetUtcNow();
            stats.ConsecutiveFailures = schedule.Failures;
        }
        finally
        {
            lock (_scheduleLock)
            {
                schedule.Running = false;
            }

            try
            {
                ProviderSynced?.Invoke(this, def.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProviderSynced handler failed");
            }
        }
    }

    private static EndpointSpec? FindEndpoint(ProviderDefinition def, string key)
    {
        if (key == "categories")
        {
            return def.Categories;
        }

        var events = def.Events ?? new List<EndpointSpec>();
        for (var i = 0; i < events.Count; i++)
        {
            if (RestRunner.EndpointId(events[i], "events", i) == key)
            {
                return events[i];
            }
        }

        var channels = def.Channels ?? new List<EndpointSpec>();
        for (var i = 0; i < channels.Count; i++)
        {
            if (RestRunner.EndpointId(channels[i], "channels", i) == key)
            {
                return channels[i];
            }
        }

        return null;
    }

    private static TimeSpan RetryDelay(int attempt, TimeSpan? retryAfter)
    {
        var backoff = Backoff.Delay(Math.Max(1, attempt), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(15));
        return retryAfter is { } ra && ra > backoff ? ra : backoff;
    }

    private sealed class ProviderSchedule
    {
        public Dictionary<string, DateTimeOffset> NextDue { get; } = new(StringComparer.Ordinal);

        public bool Running { get; set; }

        public int Failures { get; set; }

        public DateTimeOffset RetryAt { get; set; }
    }
}
