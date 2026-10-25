using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Diagnostics;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Sync;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveHub.Api;

/// <summary>
/// Synchronisation and diagnostics endpoints (administrators only).
/// </summary>
[ApiController]
[Route("LiveHub/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class DiagnosticsController : ControllerBase
{
    private readonly SyncScheduler _scheduler;
    private readonly CatalogService _catalog;
    private readonly ProviderEngine _engine;
    private readonly DefinitionStore _store;
    private readonly EgressManager _egress;
    private readonly ConfigAccessor _config;
    private readonly IEnumerable<IActiveStreamsSource> _streams;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticsController"/> class.
    /// </summary>
    /// <param name="scheduler">Scheduler.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="engine">Engine.</param>
    /// <param name="store">Definitions.</param>
    /// <param name="egress">Egress.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="streams">Active stream sources.</param>
    public DiagnosticsController(SyncScheduler scheduler, CatalogService catalog, ProviderEngine engine, DefinitionStore store, EgressManager egress, ConfigAccessor config, IEnumerable<IActiveStreamsSource> streams)
    {
        _scheduler = scheduler;
        _catalog = catalog;
        _engine = engine;
        _store = store;
        _egress = egress;
        _config = config;
        _streams = streams;
    }

    /// <summary>Starts a synchronisation of one provider (or all).</summary>
    /// <param name="providerId">The provider id (optional).</param>
    /// <returns>Accepted.</returns>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult Sync([FromQuery] string? providerId)
    {
        _scheduler.Trigger(string.IsNullOrWhiteSpace(providerId) ? null : providerId);
        return Accepted();
    }

    /// <summary>Gets the diagnostics: providers, catalog, active streams, egress.</summary>
    /// <returns>The diagnostics.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> Get()
    {
        var view = _catalog.View;
        var now = _catalog.Clock.GetUtcNow();
        var cfg = _config.Current;
        var ids = _store.List().Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var next = _scheduler.NextRuns();
        var providers = _engine.Gateways()
            .Where(g => ids.Contains(g.ProviderId))
            .Select(g =>
            {
                g.Stats.Circuit = g.Breaker.State.ToString();
                return new
                {
                    g.Stats.ProviderId,
                    g.Stats.LastSyncAt,
                    g.Stats.LastSuccessAt,
                    g.Stats.LastError,
                    g.Stats.LastErrorAt,
                    g.Stats.ConsecutiveFailures,
                    g.Stats.Events,
                    g.Stats.Channels,
                    g.Stats.Programs,
                    g.Stats.Streams,
                    g.Stats.Requests,
                    g.Stats.NotModified,
                    g.Stats.Failures,
                    g.Stats.Circuit,
                    g.Stats.EgressProfile,
                    FromSnapshot = _catalog.IsFromSnapshot(g.ProviderId),
                    NextRun = next.TryGetValue(g.ProviderId, out var n) && n != DateTimeOffset.MaxValue && n != DateTimeOffset.MinValue ? n : (DateTimeOffset?)null,
                    g.Stats.Endpoints,
                };
            })
            .ToList();

        var egress = cfg.EgressProfiles.ToDictionary(p => p.Id, p => _egress.GetHealth(p.Id), StringComparer.Ordinal);
        return Ok(new
        {
            Catalog = new
            {
                Events = view.Events.Count,
                Live = view.Events.Count(e => EventStatusCalculator.Status(e, now, cfg) == EventStatus.Live),
                Channels = view.Channels.Count,
                Programs = view.Programs.Sum(p => p.Value.Count),
                Revision = view.DataRevision + "/" + view.DisplayRevision,
                LastChange = view.BuiltAt,
            },
            Providers = providers,
            Streams = _streams.SelectMany(s => s.ActiveStreams()).ToList(),
            Egress = egress,
        });
    }
}
