using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Diagnostics;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Playback;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Relay;

/// <summary>
/// The internal relay: every byte ffmpeg reads comes from here, fetched through the provider egress with the
/// required headers. HLS playlists are rewritten (only URLs found in origin playlists can be fetched, so it is never an
/// open proxy), sources are re-resolved on 403/410 and switched (discontinuity) on stalls or repeated errors.
/// </summary>
public sealed class RelayService : IActiveStreamsSource, IDisposable
{
    /// <summary>Route prefix of the relay controller.</summary>
    public const string RoutePrefix = "LiveHub/stream";

    private const long MaxPlaylistBytes = 4 * 1024 * 1024;
    private const long MaxSegmentBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan PlaylistTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SegmentTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex AbsoluteUrl = new(@"https?://[^\s""'<>]+", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));
    private static readonly Regex RelativeBaseUrl = new(@"<BaseURL(?<a>[^>]*)>(?!https?:|d/)(?<v>[^<]*)</BaseURL>", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(500));

    private readonly IOutboundHttp _http;
    private readonly StreamResolverService _resolver;
    private readonly EventStreamService _events;
    private readonly UserViewFactory _views;
    private readonly ConfigAccessor _config;
    private readonly RelayTokenService _tokens;
    private readonly IRelayBaseUrl _baseUrl;
    private readonly ILogger<RelayService> _logger;
    private readonly TimeProvider _clock;
    private readonly SegmentCache _segments;
    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, TokenBucket> _rates = new();
    private readonly ITimer _cleanup;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayService"/> class.
    /// </summary>
    /// <param name="http">Outgoing HTTP.</param>
    /// <param name="resolver">Resolver.</param>
    /// <param name="events">Event streams.</param>
    /// <param name="views">User views (stream visibility).</param>
    /// <param name="config">Configuration.</param>
    /// <param name="tokens">Token service.</param>
    /// <param name="baseUrl">Local base URL.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="clock">Clock.</param>
    public RelayService(IOutboundHttp http, StreamResolverService resolver, EventStreamService events, UserViewFactory views, ConfigAccessor config, RelayTokenService tokens, IRelayBaseUrl baseUrl, ILogger<RelayService> logger, TimeProvider? clock = null)
    {
        _http = http;
        _resolver = resolver;
        _events = events;
        _views = views;
        _config = config;
        _tokens = tokens;
        _baseUrl = baseUrl;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _segments = new SegmentCache(_clock);
        _cleanup = _clock.CreateTimer(_ => Cleanup(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    /// <summary>Gets the sessions (diagnostics, tests).</summary>
    public IReadOnlyCollection<RelaySession> Sessions => _sessions.Values.ToList();

    /// <summary>Gets the relay mode used for a stream kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The mode.</returns>
    public static RelayMode ModeOf(StreamKind kind) => kind switch
    {
        StreamKind.Ts => RelayMode.Ts,
        StreamKind.Mp4 => RelayMode.File,
        StreamKind.Dash => RelayMode.Dash,
        _ => RelayMode.Hls,
    };

    /// <summary>Gets the file name of the entry point of a mode.</summary>
    /// <param name="mode">The mode (null = generic entry that redirects once the stream is resolved).</param>
    /// <returns>The file name.</returns>
    public static string EntryFile(RelayMode? mode) => mode switch
    {
        RelayMode.Hls => "master.m3u8",
        RelayMode.Ts => "live.ts",
        RelayMode.File => "file.mp4",
        RelayMode.Dash => "manifest.mpd",
        _ => "stream",
    };

    /// <summary>Creates a signed token.</summary>
    /// <param name="claims">The claims.</param>
    /// <returns>The token.</returns>
    public string CreateToken(RelayClaims claims)
        => _tokens.Create(claims, TimeSpan.FromHours(Math.Clamp(_config.Current.RelayTokenHours, 1, 72)));

    /// <summary>Builds the local relay URL of a token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="mode">The mode.</param>
    /// <returns>The URL.</returns>
    public string BuildUrl(string token, RelayMode? mode) => _baseUrl.BaseUrl + "/" + RoutePrefix + "/" + token + "/" + EntryFile(mode);

    /// <summary>Validates a token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="claims">The claims.</param>
    /// <returns>True when valid.</returns>
    public bool TryAuthorize(string token, out RelayClaims claims) => _tokens.TryValidate(token, out claims);

    /// <summary>Applies the per-user request rate limit.</summary>
    /// <param name="user">The user.</param>
    /// <returns>True when the request may proceed.</returns>
    public bool CheckRate(Guid user)
    {
        var perMinute = Math.Max(60, _config.Current.RelayRequestsPerMinute);
        var bucket = _rates.GetOrAdd(user, _ => new TokenBucket(perMinute, Math.Max(50, perMinute / 6), _clock));
        return bucket.TryTake();
    }

    /// <summary>Gets (or creates) the session of a token.</summary>
    /// <param name="claims">The claims.</param>
    /// <returns>The session.</returns>
    public RelaySession GetSession(RelayClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var session = _sessions.GetOrAdd(claims.SessionKey, _ => new RelaySession(claims, _clock.GetUtcNow()));
        session.Touch(_clock.GetUtcNow());
        return session;
    }

    /// <summary>Counts the live streams held by a user.</summary>
    /// <param name="user">The user.</param>
    /// <returns>The count.</returns>
    public int HoldsOf(Guid user) => _sessions.Values.Sum(s => s.HoldsOf(user));

    /// <summary>Holds a session for a Jellyfin live stream.</summary>
    /// <param name="claims">The claims.</param>
    /// <returns>The session.</returns>
    public RelaySession Acquire(RelayClaims claims)
    {
        var session = GetSession(claims);
        session.Acquire(claims.UserId);
        return session;
    }

    /// <summary>Releases a session held for a Jellyfin live stream.</summary>
    /// <param name="sessionKey">The session key.</param>
    /// <param name="user">The user.</param>
    public void Release(string sessionKey, Guid user)
    {
        if (_sessions.TryGetValue(sessionKey, out var session))
        {
            session.Release(user);
            session.Touch(_clock.GetUtcNow());
        }
    }

    /// <summary>Resolves the session source (warm-up before playback) and returns the serving mode.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The mode.</returns>
    public async Task<RelayMode> PrepareAsync(RelaySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentAsync(session, cancellationToken).ConfigureAwait(false);
            return session.Mode ?? RelayMode.Hls;
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>Gets the output master playlist.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The playlist.</returns>
    public async Task<string> GetMasterAsync(RelaySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.Master is not null)
            {
                return session.Master;
            }

            await EnsureCurrentAsync(session, cancellationToken).ConfigureAwait(false);
            if (session.Mode != RelayMode.Hls)
            {
                throw new RelayException(409, "The stream is not HLS (" + session.Mode + ")");
            }

            MapSlots(session);
            return session.Master!;
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>Gets an output media playlist.</summary>
    /// <param name="session">The session.</param>
    /// <param name="slotName">The slot (v0, a0, t0...).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The playlist.</returns>
    public async Task<string> GetMediaPlaylistAsync(RelaySession session, string slotName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetMediaPlaylistLockedAsync(session, slotName, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>Gets a segment (or key / init section) through the shared cache.</summary>
    /// <param name="session">The session.</param>
    /// <param name="id">The relay id.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The segment.</returns>
    public Task<CachedSegment> GetSegmentAsync(RelaySession session, string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.TryGetOrigin(id, out var origin) || origin.IsPrefix)
        {
            throw new RelayException(404, "Unknown segment");
        }

        return FetchSegmentAsync(session, origin.Url, origin.Context, cancellationToken);
    }

    /// <summary>Opens a segment with a Range request (byte-range playlists), bypassing the cache.</summary>
    /// <param name="session">The session.</param>
    /// <param name="id">The relay id.</param>
    /// <param name="range">The Range header.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The origin response.</returns>
    public async Task<StreamingResponse> OpenSegmentRangeAsync(RelaySession session, string id, string range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.TryGetOrigin(id, out var origin) || origin.IsPrefix)
        {
            throw new RelayException(404, "Unknown segment");
        }

        return await OpenOriginAsync(origin.Context, origin.Url, range, SegmentTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Subscribes to the shared MPEG-TS stream of a session.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The reader.</returns>
    public async Task<ChannelReader<ReadOnlyMemory<byte>>> SubscribeTsAsync(RelaySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureCurrentAsync(session, cancellationToken).ConfigureAwait(false);
            if (session.Mode != RelayMode.Ts)
            {
                throw new RelayException(409, "The stream is not MPEG-TS (" + session.Mode + ")");
            }

            session.Fanout ??= new TsFanout((attempt, ct) => OpenTsOriginAsync(session, attempt, ct), _logger);
            return session.Fanout.Subscribe();
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>Opens the progressive file of a session (Range forwarded).</summary>
    /// <param name="session">The session.</param>
    /// <param name="range">The Range header, if any.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The origin response.</returns>
    public async Task<StreamingResponse> OpenFileAsync(RelaySession session, string? range, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < MaxAttempts(session); attempt++)
            {
                await EnsureCurrentAsync(session, cancellationToken).ConfigureAwait(false);
                if (session.Mode != RelayMode.File)
                {
                    throw new RelayException(409, "The stream is not a file (" + session.Mode + ")");
                }

                var current = session.Current!;
                var response = await OpenOriginAsync(current, current.Url, range, SegmentTimeout, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is 401 or 403 or 404 or 410)
                {
                    await response.DisposeAsync().ConfigureAwait(false);
                    ReResolveOrFailover(session, "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                return response;
            }

            throw new RelayException(502, "No source available: " + session.LastError, "hint.allFailed");
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>Gets the rewritten DASH manifest.</summary>
    /// <param name="session">The session.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The manifest.</returns>
    public async Task<string> GetDashManifestAsync(RelaySession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < MaxAttempts(session); attempt++)
            {
                await EnsureCurrentAsync(session, cancellationToken).ConfigureAwait(false);
                if (session.Mode != RelayMode.Dash)
                {
                    throw new RelayException(409, "The stream is not DASH (" + session.Mode + ")");
                }

                var current = session.Current!;
                FetchResponse response;
                try
                {
                    response = await _http.FetchAsync(Request(current, current.Url, MaxPlaylistBytes, PlaylistTimeout), cancellationToken).ConfigureAwait(false);
                }
                catch (EgressUnavailableException ex)
                {
                    throw new RelayException(503, ex.Message, "hint.egress");
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    Failover(session, ex.Message);
                    continue;
                }

                if (!response.IsSuccess)
                {
                    ReResolveOrFailover(session, "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                session.Touch(_clock.GetUtcNow());
                return RewriteDash(session, response.Text(), response.FinalUrl, current);
            }

            throw new RelayException(502, "No source available: " + session.LastError, "hint.allFailed");
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>Gets a DASH resource below a registered directory prefix.</summary>
    /// <param name="session">The session.</param>
    /// <param name="prefixId">The prefix id.</param>
    /// <param name="rest">The relative path.</param>
    /// <param name="query">The query string (with "?").</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The segment.</returns>
    public Task<CachedSegment> GetPrefixedAsync(RelaySession session, string prefixId, string rest, string? query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.TryGetOrigin(prefixId, out var origin) || !origin.IsPrefix)
        {
            throw new RelayException(404, "Unknown prefix");
        }

        if (!Uri.TryCreate(origin.Url, rest + (query ?? string.Empty), out var target)
            || !target.AbsoluteUri.StartsWith(origin.Url.GetLeftPart(UriPartial.Path), StringComparison.Ordinal))
        {
            throw new RelayException(403, "Path outside of the registered prefix");
        }

        return FetchSegmentAsync(session, target, origin.Context, cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyList<ActiveStreamInfo> ActiveStreams()
    {
        var now = _clock.GetUtcNow();
        return _sessions.Values
            .Where(s => s.Holders > 0 || now - s.LastActivity < TimeSpan.FromMinutes(2))
            .Select(s => new ActiveStreamInfo
            {
                Title = s.Title,
                User = string.Join(", ", s.Users.Select(u => u == Guid.Empty ? "system" : u.ToString("N")[..8])),
                Origin = s.Current?.Url.Host ?? string.Empty,
                ProviderId = s.CurrentCandidate?.ProviderId ?? string.Empty,
                StartedAt = s.StartedAt,
                LastActivity = s.LastActivity,
                Bytes = s.Bytes,
                Failovers = s.Failovers,
                Consumers = s.Holders,
            })
            .ToList();
    }

    /// <summary>Removes idle sessions (also called by the timer).</summary>
    public void Cleanup()
    {
        try
        {
            var now = _clock.GetUtcNow();
            var idle = TimeSpan.FromSeconds(Math.Max(15, _config.Current.RelayIdleSeconds));
            foreach (var session in _sessions.Values)
            {
                var silent = now - session.LastActivity;
                var fanoutActive = session.Fanout is { Subscribers: > 0 };
                if (fanoutActive)
                {
                    session.Touch(now);
                    continue;
                }

                if ((session.Holders == 0 && silent > idle) || silent > TimeSpan.FromMinutes(15))
                {
                    if (_sessions.TryRemove(session.Key, out var removed))
                    {
                        _logger.LogDebug("Relay session {Title} closed after {Idle}s of inactivity", removed.Title, (int)silent.TotalSeconds);
                        removed.Dispose();
                    }
                }
            }

            _segments.Prune();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Relay cleanup failed");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cleanup.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    internal static string Render(SlotState slot)
    {
        var sb = new StringBuilder(1024);
        var maxDuration = slot.Window.Count > 0 ? slot.Window.Max(s => s.Duration) : 0;
        sb.Append("#EXTM3U\n#EXT-X-VERSION:").Append(Math.Max(3, slot.Version).ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("#EXT-X-TARGETDURATION:").Append(((int)Math.Ceiling(Math.Max(1, Math.Max(slot.TargetDuration, maxDuration)))).ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("#EXT-X-MEDIA-SEQUENCE:").Append((slot.Window.Count > 0 ? slot.Window[0].Sequence : slot.NextSequence).ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (slot.DiscontinuitySequence > 0)
        {
            sb.Append("#EXT-X-DISCONTINUITY-SEQUENCE:").Append(slot.DiscontinuitySequence.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        string? lastKey = null;
        string? lastMap = null;
        for (var i = 0; i < slot.Window.Count; i++)
        {
            var seg = slot.Window[i];
            if (seg.Discontinuity)
            {
                sb.Append("#EXT-X-DISCONTINUITY\n");
            }

            if (!string.Equals(seg.KeyLine, lastKey, StringComparison.Ordinal))
            {
                if (seg.KeyLine is not null)
                {
                    sb.Append(seg.KeyLine).Append('\n');
                }
                else if (i > 0)
                {
                    sb.Append("#EXT-X-KEY:METHOD=NONE\n");
                }

                lastKey = seg.KeyLine;
            }

            if (seg.MapLine is not null && !string.Equals(seg.MapLine, lastMap, StringComparison.Ordinal))
            {
                sb.Append(seg.MapLine).Append('\n');
                lastMap = seg.MapLine;
            }

            foreach (var tag in seg.Tags)
            {
                sb.Append(tag).Append('\n');
            }

            sb.Append("#EXTINF:").Append(HlsPlaylist.FormatDuration(seg.Duration)).Append(',').Append(seg.Title).Append('\n');
            sb.Append("../s/").Append(seg.Id).Append('\n');
        }

        if (slot.Ended)
        {
            sb.Append("#EXT-X-ENDLIST\n");
        }

        return sb.ToString();
    }

    internal static int Merge(RelaySession session, SlotState slot, HlsPlaylist parsed, ResolvedStream context, DateTimeOffset now)
    {
        if (slot.Generation != session.Generation)
        {
            if (slot.Generation >= 0 && slot.Window.Count > 0)
            {
                slot.PendingDiscontinuity = true;
            }

            slot.Generation = session.Generation;
            slot.LastOriginSequence = null;
            slot.LastAdvance = now;
        }

        var segments = parsed.Segments;
        IEnumerable<HlsSegment> take;
        if (slot.LastOriginSequence is null)
        {
            take = slot.Window.Count == 0 ? segments : segments.Skip(Math.Max(0, segments.Count - 3));
        }
        else if (segments.Count > 0 && segments[^1].Sequence < slot.LastOriginSequence.Value - segments.Count - 2)
        {
            // The origin restarted its numbering: treat as a new stream.
            slot.PendingDiscontinuity = true;
            take = segments.Skip(Math.Max(0, segments.Count - 3));
        }
        else
        {
            var last = slot.LastOriginSequence.Value;
            take = segments.Where(s => s.Sequence > last);
        }

        var appended = 0;
        foreach (var s in take.ToList())
        {
            // ffmpeg only accepts HLS segments with a known extension (allowed_segment_extensions): keep it.
            var id = session.Register(s.Uri, context) + SegmentExtension(s.Uri, ".ts");
            var key = s.KeyLine is null ? null : s.KeyUri is null ? s.KeyLine : HlsPlaylist.ReplaceUriAttribute(s.KeyLine, "../s/" + session.Register(s.KeyUri, context) + ".key");
            var map = s.MapLine is null ? null : s.MapUri is null ? s.MapLine : HlsPlaylist.ReplaceUriAttribute(s.MapLine, "../s/" + session.Register(s.MapUri, context) + SegmentExtension(s.MapUri, ".mp4"));
            slot.Window.Add(new OutSegment(slot.NextSequence++, s.Duration, s.Title, id, s.Discontinuity || slot.PendingDiscontinuity, key, map, s.Tags.ToList()));
            slot.PendingDiscontinuity = false;
            slot.LastOriginSequence = s.Sequence;
            appended++;
        }

        if (appended > 0)
        {
            slot.LastAdvance = now;
        }

        if (parsed.TargetDuration > 0)
        {
            slot.TargetDuration = parsed.TargetDuration;
        }

        slot.Version = Math.Max(slot.Version, parsed.Version);
        slot.Ended = parsed.Ended;
        if (!parsed.Ended)
        {
            var keep = Math.Clamp(segments.Count, 6, 30);
            while (slot.Window.Count > keep)
            {
                if (slot.Window[0].Discontinuity)
                {
                    slot.DiscontinuitySequence++;
                }

                slot.Window.RemoveAt(0);
            }
        }

        return appended;
    }

    /// <summary>Gets the extension used in relay segment URLs (the origin one when it is a media extension).</summary>
    /// <param name="url">The origin URL.</param>
    /// <param name="fallback">The fallback extension.</param>
    /// <returns>The extension with its dot.</returns>
    internal static string SegmentExtension(Uri url, string fallback)
    {
        var ext = System.IO.Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
        return ext is ".ts" or ".m2ts" or ".mts" or ".aac" or ".ac3" or ".ec3" or ".mp3" or ".mp4" or ".m4s" or ".m4a" or ".m4v" or ".cmfv" or ".cmfa" or ".vtt" or ".webvtt" or ".fmp4"
            ? ext
            : fallback;
    }

    private static int MaxAttempts(RelaySession session) => ((session.Candidates?.Count ?? 1) * 2) + 2;

    private static bool IsTransient(Exception ex)
        => ex is HttpRequestException or TimeoutException or IOException or ResponseTooLargeException or SsrfException;

    private static FetchRequest Request(ResolvedStream context, Uri url, long maxBytes, TimeSpan timeout, string? range = null) => new()
    {
        ProviderId = context.ProviderId,
        Url = url,
        Headers = new Dictionary<string, string>(context.Headers, StringComparer.OrdinalIgnoreCase),
        EgressProfileId = context.EgressProfileId,
        AllowPrivateNetwork = context.AllowPrivateNetwork,
        MaxBytes = maxBytes,
        Timeout = timeout,
        Range = range,
    };

    private static HlsVariant? Closest(IReadOnlyList<HlsVariant> variants, int? height, long? bandwidth)
    {
        if (variants.Count == 0)
        {
            return null;
        }

        return height is { } h
            ? variants.OrderBy(v => Math.Abs((v.Height ?? 0) - h)).ThenBy(v => Math.Abs(v.Bandwidth - (bandwidth ?? v.Bandwidth))).First()
            : variants.OrderBy(v => Math.Abs(v.Bandwidth - (bandwidth ?? 0))).First();
    }

    private static string RewriteDash(RelaySession session, string manifest, Uri manifestUrl, ResolvedStream context)
    {
        var root = new Uri(manifestUrl, "./");
        var rootId = session.Register(root, context, isPrefix: true);
        string text;
        try
        {
            text = AbsoluteUrl.Replace(manifest, m =>
            {
                if (!Uri.TryCreate(System.Net.WebUtility.HtmlDecode(m.Value), UriKind.Absolute, out var u))
                {
                    return m.Value;
                }

                var dir = new Uri(u, "./");
                var id = session.Register(dir, context, isPrefix: true);
                var dirText = dir.AbsoluteUri;
                var rest = m.Value.StartsWith(dirText, StringComparison.Ordinal) ? m.Value[dirText.Length..] : u.AbsoluteUri[Math.Min(dirText.Length, u.AbsoluteUri.Length)..];
                return "d/" + id + "/" + rest;
            });

            var period = text.IndexOf("<Period", StringComparison.Ordinal);
            var firstBase = RelativeBaseUrl.Match(text);
            if (!text.Contains("<BaseURL", StringComparison.Ordinal))
            {
                var mpd = text.IndexOf("<MPD", StringComparison.Ordinal);
                var close = mpd >= 0 ? text.IndexOf('>', mpd) : -1;
                if (close > 0)
                {
                    text = text.Insert(close + 1, "<BaseURL>d/" + rootId + "/</BaseURL>");
                }
            }
            else if (firstBase.Success && (period < 0 || firstBase.Index < period))
            {
                text = string.Concat(text.AsSpan(0, firstBase.Index), "<BaseURL" + firstBase.Groups["a"].Value + ">d/" + rootId + "/" + firstBase.Groups["v"].Value + "</BaseURL>", text.AsSpan(firstBase.Index + firstBase.Length));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            throw new RelayException(502, "DASH manifest too complex");
        }

        return text;
    }

    private async Task<string> GetMediaPlaylistLockedAsync(RelaySession session, string slotName, CancellationToken cancellationToken)
    {
        var cfg = _config.Current;
        SlotState? slot = null;
        for (var attempt = 0; attempt < MaxAttempts(session); attempt++)
        {
            try
            {
                await EnsureCurrentAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (RelayException) when (slot is { Window.Count: > 0 })
            {
                return Render(slot);
            }

            if (session.Master is null || !session.Slots.ContainsKey(slotName))
            {
                MapSlots(session);
            }

            if (!session.Slots.TryGetValue(slotName, out slot))
            {
                throw new RelayException(404, "Unknown playlist");
            }

            if (session.SegmentAuthErrors >= 2)
            {
                session.SegmentAuthErrors = 0;
                ReResolveOrFailover(session, "segments refused by the origin");
                continue;
            }

            if (session.SegmentErrors >= Math.Max(1, cfg.FailoverSegmentErrors))
            {
                Failover(session, session.SegmentErrors.ToString(CultureInfo.InvariantCulture) + " segment errors");
                continue;
            }

            if (slot.Origin is null)
            {
                MapSlots(session);
                if (slot.Origin is null)
                {
                    return Render(slot);
                }
            }

            var current = session.Current!;
            FetchResponse response;
            try
            {
                response = await _http.FetchAsync(Request(current, slot.Origin, MaxPlaylistBytes, PlaylistTimeout), cancellationToken).ConfigureAwait(false);
            }
            catch (EgressUnavailableException ex)
            {
                // Kill switch: no silent direct fallback, the player gets a clear error.
                throw new RelayException(503, ex.Message, "hint.egress");
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                session.LastError = ex.Message;
                if (++slot.Errors >= Math.Max(1, cfg.FailoverSegmentErrors))
                {
                    Failover(session, ex.Message);
                    continue;
                }

                return slot.Window.Count > 0 ? Render(slot) : throw new RelayException(502, ex.Message, "hint.network");
            }

            if (response.StatusCode is 401 or 403 or 404 or 410)
            {
                ReResolveOrFailover(session, "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            var text = response.IsSuccess ? response.Text() : string.Empty;
            if (!response.IsSuccess || !HlsPlaylist.LooksLikeHls(text))
            {
                session.LastError = response.IsSuccess ? "invalid playlist" : "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture);
                if (++slot.Errors >= Math.Max(1, cfg.FailoverSegmentErrors))
                {
                    Failover(session, session.LastError);
                    continue;
                }

                return slot.Window.Count > 0 ? Render(slot) : throw new RelayException(502, session.LastError, "hint.serverError");
            }

            var parsed = HlsPlaylist.Parse(text, response.FinalUrl);
            if (parsed.IsMaster)
            {
                slot.Origin = HlsPlaylist.Choose(parsed.Variants, session.Selector)?.Uri;
                continue;
            }

            slot.Errors = 0;
            var now = _clock.GetUtcNow();
            var appended = Merge(session, slot, parsed, current, now);
            session.Touch(now);
            if (appended == 0 && !parsed.Ended && now - slot.LastAdvance > TimeSpan.FromSeconds(Math.Max(5, cfg.FailoverStallSeconds)))
            {
                Failover(session, "stalled for " + ((int)(now - slot.LastAdvance).TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s");
                continue;
            }

            return Render(slot);
        }

        return slot is { Window.Count: > 0 } ? Render(slot) : throw new RelayException(502, "No source available: " + session.LastError, "hint.allFailed");
    }

    private async Task EnsureCandidatesAsync(RelaySession session, CancellationToken cancellationToken)
    {
        if (session.Candidates is not null)
        {
            return;
        }

        var claims = session.Claims;
        var item = await _events.GetItemStreamsAsync(claims.Kind, claims.Key, cancellationToken).ConfigureAwait(false)
            ?? throw new RelayException(404, "The event or channel no longer exists", "hint.itemGone");
        IEnumerable<LiveStream> streams = item.Streams;
        if (claims.UserId != Guid.Empty)
        {
            streams = _views.Create(claims.UserId).VisibleStreams(streams);
        }

        var list = streams.ToList();
        var primary = list.Find(s => s.Id == claims.StreamId) ?? list.FirstOrDefault();
        session.Title = item.Title;
        session.Candidates = StreamOrdering.FailoverOrder(primary, list);
        if (session.Candidates.Count == 0)
        {
            throw new RelayException(404, "No stream available", "hint.noStream");
        }
    }

    private async Task EnsureCurrentAsync(RelaySession session, CancellationToken cancellationToken)
    {
        await EnsureCandidatesAsync(session, cancellationToken).ConfigureAwait(false);
        var started = session.Master is not null || session.Fanout is not null;
        while (session.Current is null)
        {
            var candidates = session.Candidates!;
            if (session.CandidateIndex >= candidates.Count)
            {
                if (started && session.Wraps < 2)
                {
                    session.Wraps++;
                    session.CandidateIndex = 0;
                }
                else
                {
                    throw new RelayException(502, "No source available: " + session.LastError, "hint.allFailed");
                }
            }

            var candidate = candidates[session.CandidateIndex];
            try
            {
                var resolved = await _resolver.ResolveAsync(candidate, false, cancellationToken).ConfigureAwait(false);
                var mode = ModeOf(resolved.Kind);
                if (session.Mode is { } fixedMode && fixedMode != mode)
                {
                    session.LastError = $"source #{session.CandidateIndex + 1}: format {resolved.Kind} cannot replace {fixedMode}";
                    session.CandidateIndex++;
                    continue;
                }

                session.Mode ??= mode;
                session.Current = resolved;
            }
            catch (EgressUnavailableException ex)
            {
                throw new RelayException(503, ex.Message, "hint.egress");
            }
            catch (ResolveException ex)
            {
                session.LastError = $"source #{session.CandidateIndex + 1} ({candidate.ProviderId}): {ex.Message}";
                _logger.LogInformation("Relay {Title}: {Error}", session.Title, session.LastError);
                session.CandidateIndex++;
                if (started)
                {
                    session.Generation++;
                    session.Failovers++;
                }
            }
        }
    }

    private void MapSlots(RelaySession session)
    {
        var current = session.Current ?? throw new RelayException(502, "No source");
        var playlist = current.Playlist;
        var v0 = GetSlot(session, "v0", "VIDEO");
        if (playlist is null || !playlist.IsMaster)
        {
            v0.Origin = current.Url;
            session.Master ??= "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-STREAM-INF:" + HlsPlaylist.SyntheticVariantAttributes(session.CurrentCandidate?.Bitrate) + "\np/v0.m3u8\n";
            foreach (var other in session.Slots.Values.Where(s => s.Name != "v0"))
            {
                other.Origin = null;
            }

            return;
        }

        var variant = session.Master is null
            ? HlsPlaylist.Choose(playlist.Variants, session.Selector)
            : Closest(playlist.Variants, session.ChosenHeight, session.ChosenBandwidth);
        if (variant is null)
        {
            throw new RelayException(502, "Empty master playlist", "hint.unknownFormat");
        }

        session.ChosenHeight ??= variant.Height;
        session.ChosenBandwidth ??= variant.Bandwidth;
        v0.Origin = variant.Uri;
        var renditions = playlist.Renditions
            .Where(r => (r.Type == "AUDIO" && r.GroupId == variant.AudioGroup) || (r.Type == "SUBTITLES" && r.GroupId == variant.SubtitlesGroup) || r.Type == "CLOSED-CAPTIONS")
            .ToList();

        if (session.Master is null)
        {
            var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:").Append(playlist.Version.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (playlist.IndependentSegments)
            {
                sb.Append("#EXT-X-INDEPENDENT-SEGMENTS\n");
            }

            int audio = 0, subs = 0;
            foreach (var r in renditions)
            {
                var line = "#EXT-X-MEDIA:" + r.Attributes;
                if (r.Uri is null)
                {
                    sb.Append(line).Append('\n');
                    continue;
                }

                var name = r.Type == "AUDIO" ? "a" + (audio++).ToString(CultureInfo.InvariantCulture) : "t" + (subs++).ToString(CultureInfo.InvariantCulture);
                var slot = GetSlot(session, name, r.Type);
                slot.Language = r.Language;
                slot.RenditionName = r.Name;
                slot.Origin = r.Uri;
                sb.Append(HlsPlaylist.ReplaceUriAttribute(line, "p/" + name + ".m3u8")).Append('\n');
            }

            sb.Append("#EXT-X-STREAM-INF:").Append(variant.Attributes).Append("\np/v0.m3u8\n");
            session.Master = sb.ToString();
            return;
        }

        foreach (var slot in session.Slots.Values.Where(s => s.Type != "VIDEO"))
        {
            var sameType = renditions.Where(r => r.Type == slot.Type && r.Uri is not null).ToList();
            var match = sameType.Find(r => r.Language == slot.Language && r.Name == slot.RenditionName)
                ?? sameType.Find(r => r.Language == slot.Language)
                ?? sameType.ElementAtOrDefault(int.TryParse(slot.Name.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0);
            slot.Origin = match?.Uri;
        }
    }

    private SlotState GetSlot(RelaySession session, string name, string type)
    {
        if (!session.Slots.TryGetValue(name, out var slot))
        {
            slot = new SlotState { Name = name, Type = type, LastAdvance = _clock.GetUtcNow() };
            session.Slots[name] = slot;
        }

        return slot;
    }

    private void Failover(RelaySession session, string reason)
    {
        _logger.LogWarning("Relay {Title}: switching away from source #{Index} ({Reason})", session.Title, session.CandidateIndex + 1, reason);
        session.LastError = reason;
        if (session.CurrentCandidate is { } candidate)
        {
            _resolver.Invalidate(candidate);
        }

        session.CandidateIndex++;
        session.Current = null;
        session.Generation++;
        session.Failovers++;
        session.SegmentErrors = 0;
        session.SegmentAuthErrors = 0;
        session.LastReResolve = null;
        foreach (var slot in session.Slots.Values)
        {
            slot.Origin = null;
            slot.Errors = 0;
        }
    }

    private void ReResolveOrFailover(RelaySession session, string reason)
    {
        var now = _clock.GetUtcNow();
        if (session.LastReResolve is { } last && now - last < TimeSpan.FromSeconds(60))
        {
            Failover(session, reason);
            return;
        }

        _logger.LogInformation("Relay {Title}: {Reason}, resolving source #{Index} again", session.Title, reason, session.CandidateIndex + 1);
        session.LastReResolve = now;
        if (session.CurrentCandidate is { } candidate)
        {
            _resolver.Invalidate(candidate);
        }

        session.Current = null;
        foreach (var slot in session.Slots.Values)
        {
            slot.Origin = null;
        }
    }

    private async Task<CachedSegment> FetchSegmentAsync(RelaySession session, Uri url, ResolvedStream context, CancellationToken cancellationToken)
    {
        CachedSegment segment;
        try
        {
            segment = await _segments.GetAsync(
                context.EgressProfileId + "|" + url.AbsoluteUri,
                async ct =>
                {
                    var response = await _http.FetchAsync(Request(context, url, MaxSegmentBytes, SegmentTimeout), ct).ConfigureAwait(false);
                    return new CachedSegment(response.StatusCode, response.IsSuccess ? response.Body : Array.Empty<byte>(), response.ContentType);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (EgressUnavailableException ex)
        {
            throw new RelayException(503, ex.Message, "hint.egress");
        }
        catch (Exception ex) when (IsTransient(ex) || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            session.SegmentErrors++;
            session.LastError = "segment: " + ex.Message;
            throw new RelayException(502, "Segment unavailable: " + ex.Message, "hint.network");
        }

        if (segment.Status is < 200 or >= 300)
        {
            session.SegmentErrors++;
            if (segment.Status is 401 or 403 or 410)
            {
                session.SegmentAuthErrors++;
            }

            session.LastError = "segment: HTTP " + segment.Status.ToString(CultureInfo.InvariantCulture);
            throw new RelayException(502, "Segment unavailable: HTTP " + segment.Status.ToString(CultureInfo.InvariantCulture), DirectResolver.HintFor(segment.Status));
        }

        session.SegmentErrors = 0;
        session.SegmentAuthErrors = 0;
        session.AddBytes(segment.Data.Length);
        session.Touch(_clock.GetUtcNow());
        return segment;
    }

    private async Task<StreamingResponse> OpenOriginAsync(ResolvedStream context, Uri url, string? range, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.OpenStreamAsync(Request(context, url, long.MaxValue, timeout, range), cancellationToken).ConfigureAwait(false);
        }
        catch (EgressUnavailableException ex)
        {
            throw new RelayException(503, ex.Message, "hint.egress");
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            throw new RelayException(502, ex.Message, "hint.network");
        }
    }

    private async Task<Stream> OpenTsOriginAsync(RelaySession session, int attempt, CancellationToken cancellationToken)
    {
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (attempt == 1)
            {
                ReResolveOrFailover(session, "TS connection lost");
            }
            else if (attempt > 1)
            {
                Failover(session, "TS connection lost");
            }

            for (var i = 0; i < MaxAttempts(session); i++)
            {
                await EnsureCurrentAsync(session, cancellationToken).ConfigureAwait(false);
                var current = session.Current!;
                StreamingResponse response;
                try
                {
                    response = await _http.OpenStreamAsync(Request(current, current.Url, long.MaxValue, TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(false);
                }
                catch (EgressUnavailableException)
                {
                    throw;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    Failover(session, ex.Message);
                    continue;
                }

                if (response.StatusCode is < 200 or >= 300)
                {
                    var status = response.StatusCode;
                    await response.DisposeAsync().ConfigureAwait(false);
                    ReResolveOrFailover(session, "HTTP " + status.ToString(CultureInfo.InvariantCulture));
                    continue;
                }

                session.Touch(_clock.GetUtcNow());
                return new RelayedStream(response, session);
            }

            throw new RelayException(502, "No source available: " + session.LastError, "hint.allFailed");
        }
        finally
        {
            session.Lock.Release();
        }
    }
}
