using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Playback;
using Jellyfin.Plugin.LiveHub.Relay;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.LiveTv;

/// <summary>
/// Live TV tuner: real 24/7 channels and per-category slot channels. Streams go through the relay exactly like the
/// channel items (one media source per stream, shared live streams, failover).
/// </summary>
public class LiveHubTunerHost : ITunerHost
{
    private readonly LiveTvLineup _lineup;
    private readonly LiveHubMediaSourceProvider _sources;
    private readonly ConfigAccessor _config;
    private readonly IRelayBaseUrl _baseUrl;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<LiveHubTunerHost> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveHubTunerHost"/> class.
    /// </summary>
    /// <param name="lineup">Lineup.</param>
    /// <param name="sources">Media sources.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="baseUrl">Local base URL (channel images).</param>
    /// <param name="http">HTTP context (requesting user).</param>
    /// <param name="logger">Logger.</param>
    public LiveHubTunerHost(LiveTvLineup lineup, LiveHubMediaSourceProvider sources, ConfigAccessor config, IRelayBaseUrl baseUrl, IHttpContextAccessor http, ILogger<LiveHubTunerHost> logger)
    {
        _lineup = lineup;
        _sources = sources;
        _config = config;
        _baseUrl = baseUrl;
        _http = http;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Live Hub";

    /// <inheritdoc />
    public string Type => LiveTvLineup.TunerType;

    /// <inheritdoc />
    public bool IsSupported => _config.Current.EnableLiveTv;

    /// <summary>Converts a lineup channel to a Jellyfin channel.</summary>
    /// <param name="channel">The channel.</param>
    /// <param name="baseUrl">Local base URL.</param>
    /// <returns>The channel info.</returns>
    public static ChannelInfo ToChannelInfo(LineupChannel channel, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return new ChannelInfo
        {
            Id = channel.Id,
            TunerChannelId = channel.Id,
            Name = channel.Name,
            Number = channel.Number,
            CallSign = channel.Name,
            TunerHostId = LiveTvLineup.TunerType,
            ChannelType = ChannelType.TV,
            ChannelGroup = channel.Category,
            ImageUrl = channel.Kind == LineupKind.Slot || string.IsNullOrEmpty(channel.LogoUrl)
                ? baseUrl + "/LiveHub/Images/Channel/" + Uri.EscapeDataString(channel.Id) + ".png"
                : channel.LogoUrl,
            IsHD = channel.IsHd,
            Tags = channel.Tags.ToArray(),
        };
    }

    /// <inheritdoc />
    public Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return Task.FromResult(new List<ChannelInfo>());
        }

        var baseUrl = _baseUrl.BaseUrl;
        return Task.FromResult(_lineup.Get().Channels.Select(c => ToChannelInfo(c, baseUrl)).ToList());
    }

    /// <inheritdoc />
    public async Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var target = Target(channelId);
        if (target is null)
        {
            _logger.LogInformation("Live Hub channel {Channel}: nothing on air", channelId);
            return new List<MediaSourceInfo>();
        }

        return await _sources.GetSourcesAsync(target.Value.Kind, target.Value.Key, CurrentUser(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        var sources = await GetChannelStreamMediaSources(channelId, cancellationToken).ConfigureAwait(false);
        var source = sources.Find(s => string.Equals(s.Id, streamId, StringComparison.OrdinalIgnoreCase)) ?? sources.FirstOrDefault()
            ?? throw new InvalidOperationException("Live Hub: nothing is on air on this channel right now.");
        return await _sources.OpenMediaSource(source.OpenToken, currentLiveStreams?.ToList() ?? new List<ILiveStream>(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
        => Task.FromResult(new List<TunerHostInfo>());

    private (ItemKind Kind, string Key)? Target(string channelId)
    {
        if (channelId.StartsWith("c-", StringComparison.Ordinal))
        {
            return (ItemKind.Channel, channelId[2..]);
        }

        var ev = _lineup.EventOnSlot(channelId);
        return ev is null ? null : (ItemKind.Event, ev.Key);
    }

    private Guid CurrentUser()
    {
        var value = _http.HttpContext?.User.FindFirst(LiveHubMediaSourceProvider.UserIdClaim)?.Value;
        return Guid.TryParse(value, out var id) ? id : Guid.Empty;
    }
}
