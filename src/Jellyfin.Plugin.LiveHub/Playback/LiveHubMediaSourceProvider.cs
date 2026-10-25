using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Users;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Playback;

/// <summary>
/// Media sources of the Live Hub channel items. Jellyfin discovers this provider by type scanning; it asks for the
/// sources of every item, so items that are not ours are rejected immediately. The requesting user is read from the
/// HTTP context because the interface carries no user.
/// </summary>
public class LiveHubMediaSourceProvider : IMediaSourceProvider
{
    /// <summary>Claim holding the Jellyfin user id.</summary>
    public const string UserIdClaim = "Jellyfin-UserId";

    private readonly EventStreamService _events;
    private readonly UserViewFactory _views;
    private readonly MediaSourceBuilder _builder;
    private readonly RelayService _relay;
    private readonly ConfigAccessor _config;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<LiveHubMediaSourceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveHubMediaSourceProvider"/> class.
    /// </summary>
    /// <param name="events">Event streams.</param>
    /// <param name="views">User views.</param>
    /// <param name="builder">Media source builder.</param>
    /// <param name="relay">Relay.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="http">HTTP context accessor.</param>
    /// <param name="logger">Logger.</param>
    public LiveHubMediaSourceProvider(EventStreamService events, UserViewFactory views, MediaSourceBuilder builder, RelayService relay, ConfigAccessor config, IHttpContextAccessor http, ILogger<LiveHubMediaSourceProvider> logger)
    {
        _events = events;
        _views = views;
        _builder = builder;
        _relay = relay;
        _config = config;
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!ChannelIds.TryParseMedia(item.ExternalId, out var kind, out var key))
        {
            return Enumerable.Empty<MediaSourceInfo>();
        }

        return await GetSourcesAsync(kind, key, CurrentUser(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the media sources of an item for a user.</summary>
    /// <param name="kind">Event or channel.</param>
    /// <param name="key">The key.</param>
    /// <param name="userId">The user.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The sources.</returns>
    public async Task<List<MediaSourceInfo>> GetSourcesAsync(ItemKind kind, string key, Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _events.GetItemStreamsAsync(kind, key, cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                return new List<MediaSourceInfo>();
            }

            var view = _views.Create(userId);
            return _builder.Build(kind, key, view.VisibleStreams(data.Value.Streams), view);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Live Hub media sources of {Key} failed", key);
            return new List<MediaSourceInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        if (!_relay.TryAuthorize(openToken, out var claims))
        {
            throw new ArgumentException("Invalid or expired Live Hub stream token.", nameof(openToken));
        }

        var shared = currentLiveStreams?.OfType<RelayLiveStream>().FirstOrDefault(s => s.EnableStreamSharing && string.Equals(s.OriginalStreamId, claims.SessionKey, StringComparison.Ordinal));
        if (shared is not null)
        {
            shared.ConsumerCount++;
            _logger.LogDebug("Live Hub: sharing live stream {Key} ({Count} consumers)", claims.SessionKey, shared.ConsumerCount);
            return shared;
        }

        var limit = Math.Max(1, _config.Current.MaxStreamsPerUser);
        if (claims.UserId != Guid.Empty && _relay.HoldsOf(claims.UserId) >= limit)
        {
            throw new InvalidOperationException($"Live Hub: limit of {limit} simultaneous streams reached.");
        }

        var data = await _events.GetItemStreamsAsync(claims.Kind, claims.Key, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Live Hub: the event or channel no longer exists.");
        var stream = data.Streams.Find(s => s.Id == claims.StreamId) ?? data.Streams.FirstOrDefault()
            ?? throw new InvalidOperationException("Live Hub: no stream available.");
        var view = _views.Create(claims.UserId);
        var ordinal = data.Streams.Where(s => s.ProviderId == stream.ProviderId).ToList().IndexOf(stream) + 1;
        var source = _builder.Create(claims, stream, Math.Max(1, ordinal), view.UiLanguage);
        var live = new RelayLiveStream(_relay, claims, source.OpenToken, source);
        await live.Open(cancellationToken).ConfigureAwait(false);
        return live;
    }

    private Guid CurrentUser()
    {
        var value = _http.HttpContext?.User.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }
}
