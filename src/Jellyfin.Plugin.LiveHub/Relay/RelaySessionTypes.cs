using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Resolve;

namespace Jellyfin.Plugin.LiveHub.Relay;

/// <summary>How the relay serves a session.</summary>
public enum RelayMode
{
    /// <summary>HLS playlists rewritten by the relay.</summary>
    Hls,

    /// <summary>Continuous MPEG-TS shared by every viewer.</summary>
    Ts,

    /// <summary>Progressive file with Range support.</summary>
    File,

    /// <summary>DASH manifest rewritten by the relay.</summary>
    Dash,
}

/// <summary>An origin URL registered in a session (only registered URLs can be fetched: the relay is never open).</summary>
/// <param name="Url">The origin URL.</param>
/// <param name="Context">The resolution whose headers/egress must be used.</param>
/// <param name="IsPrefix">Whether the entry is a directory prefix (DASH).</param>
public sealed record OriginRef(Uri Url, ResolvedStream Context, bool IsPrefix);

/// <summary>A segment of an output media playlist.</summary>
/// <param name="Sequence">Output media sequence number.</param>
/// <param name="Duration">Duration.</param>
/// <param name="Title">EXTINF title.</param>
/// <param name="Id">Relay id of the segment.</param>
/// <param name="Discontinuity">Whether a discontinuity precedes it.</param>
/// <param name="KeyLine">Effective (rewritten) key line.</param>
/// <param name="MapLine">Effective (rewritten) map line.</param>
/// <param name="Tags">Other tags.</param>
public sealed record OutSegment(long Sequence, double Duration, string? Title, string Id, bool Discontinuity, string? KeyLine, string? MapLine, IReadOnlyList<string> Tags);

/// <summary>State of one output media playlist (video variant or alternative rendition).</summary>
public sealed class SlotState
{
    /// <summary>Gets or sets the slot name (v0, a0, t0...).</summary>
    public string Name { get; set; } = "v0";

    /// <summary>Gets or sets the rendition type (VIDEO for the main variant, AUDIO, SUBTITLES).</summary>
    public string Type { get; set; } = "VIDEO";

    /// <summary>Gets or sets the rendition language.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the rendition name.</summary>
    public string? RenditionName { get; set; }

    /// <summary>Gets or sets the origin playlist of the current generation (null = not mapped).</summary>
    public Uri? Origin { get; set; }

    /// <summary>Gets or sets the generation the origin belongs to.</summary>
    public int Generation { get; set; } = -1;

    /// <summary>Gets or sets the last origin media sequence appended.</summary>
    public long? LastOriginSequence { get; set; }

    /// <summary>Gets or sets the next output sequence.</summary>
    public long NextSequence { get; set; }

    /// <summary>Gets or sets the discontinuity sequence.</summary>
    public int DiscontinuitySequence { get; set; }

    /// <summary>Gets the output window.</summary>
    public List<OutSegment> Window { get; } = new();

    /// <summary>Gets or sets when a segment was last appended.</summary>
    public DateTimeOffset LastAdvance { get; set; }

    /// <summary>Gets or sets consecutive playlist errors.</summary>
    public int Errors { get; set; }

    /// <summary>Gets or sets a value indicating whether the next appended segment starts a discontinuity.</summary>
    public bool PendingDiscontinuity { get; set; }

    /// <summary>Gets or sets the target duration.</summary>
    public double TargetDuration { get; set; } = 6;

    /// <summary>Gets or sets the playlist version.</summary>
    public int Version { get; set; } = 3;

    /// <summary>Gets or sets a value indicating whether the origin ended the playlist.</summary>
    public bool Ended { get; set; }
}

/// <summary>A relay session: one item + stream + variant, shared by every viewer.</summary>
public sealed class RelaySession : IDisposable
{
    private readonly ConcurrentDictionary<string, OriginRef> _origins = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _originOrder = new();
    private readonly ConcurrentDictionary<Guid, int> _holders = new();
    private long _bytes;
    private long _lastActivityTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelaySession"/> class.
    /// </summary>
    /// <param name="claims">The claims of the token that created the session.</param>
    /// <param name="now">Now.</param>
    public RelaySession(RelayClaims claims, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(claims);
        Claims = claims;
        Key = claims.SessionKey;
        Selector = VariantSelector.Parse(claims.Variant);
        StartedAt = now;
        Touch(now);
    }

    /// <summary>Gets the session key.</summary>
    public string Key { get; }

    /// <summary>Gets the claims.</summary>
    public RelayClaims Claims { get; }

    /// <summary>Gets the variant selector.</summary>
    public VariantSelector Selector { get; }

    /// <summary>Gets the lock serializing playlist/resolution work.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets when the session started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Gets the last activity.</summary>
    public DateTimeOffset LastActivity => new(Interlocked.Read(ref _lastActivityTicks), TimeSpan.Zero);

    /// <summary>Gets the relayed bytes.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Gets or sets the failover candidates (null until loaded).</summary>
    public IReadOnlyList<LiveStream>? Candidates { get; set; }

    /// <summary>Gets or sets the index of the current candidate.</summary>
    public int CandidateIndex { get; set; }

    /// <summary>Gets or sets the current resolution.</summary>
    public ResolvedStream? Current { get; set; }

    /// <summary>Gets or sets the generation (incremented on every switch of origin).</summary>
    public int Generation { get; set; }

    /// <summary>Gets or sets when the current candidate was last re-resolved after an auth error.</summary>
    public DateTimeOffset? LastReResolve { get; set; }

    /// <summary>Gets or sets the number of failovers.</summary>
    public int Failovers { get; set; }

    /// <summary>Gets or sets how many times the candidate list was restarted from the first source.</summary>
    public int Wraps { get; set; }

    /// <summary>Gets or sets the serving mode (fixed by the first resolution).</summary>
    public RelayMode? Mode { get; set; }

    /// <summary>Gets or sets the height chosen for the video variant (kept across failovers).</summary>
    public int? ChosenHeight { get; set; }

    /// <summary>Gets or sets the bandwidth chosen for the video variant.</summary>
    public long? ChosenBandwidth { get; set; }

    /// <summary>Gets or sets the output master playlist (built once).</summary>
    public string? Master { get; set; }

    /// <summary>Gets the output slots.</summary>
    public Dictionary<string, SlotState> Slots { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets consecutive segment errors (any slot).</summary>
    public int SegmentErrors { get; set; }

    /// <summary>Gets or sets consecutive segment authorization errors (403/410).</summary>
    public int SegmentAuthErrors { get; set; }

    /// <summary>Gets or sets the TS broadcaster.</summary>
    public TsFanout? Fanout { get; set; }

    /// <summary>Gets or sets the last error (diagnostics).</summary>
    public string? LastError { get; set; }

    /// <summary>Gets the number of open live streams (Jellyfin consumers) holding the session.</summary>
    public int Holders
    {
        get
        {
            var n = 0;
            foreach (var kv in _holders)
            {
                n += kv.Value;
            }

            return n;
        }
    }

    /// <summary>Gets the users holding the session.</summary>
    public IEnumerable<Guid> Users
    {
        get
        {
            foreach (var kv in _holders)
            {
                if (kv.Value > 0)
                {
                    yield return kv.Key;
                }
            }
        }
    }

    /// <summary>Gets the current candidate, if any.</summary>
    public LiveStream? CurrentCandidate => Candidates is { } c && CandidateIndex < c.Count ? c[CandidateIndex] : null;

    /// <summary>Records activity.</summary>
    /// <param name="now">Now.</param>
    public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastActivityTicks, now.UtcTicks);

    /// <summary>Adds relayed bytes.</summary>
    /// <param name="count">The count.</param>
    public void AddBytes(long count) => Interlocked.Add(ref _bytes, count);

    /// <summary>Adds a holder.</summary>
    /// <param name="user">The user.</param>
    public void Acquire(Guid user) => _holders.AddOrUpdate(user, 1, (_, n) => n + 1);

    /// <summary>Removes a holder.</summary>
    /// <param name="user">The user.</param>
    public void Release(Guid user) => _holders.AddOrUpdate(user, 0, (_, n) => Math.Max(0, n - 1));

    /// <summary>Counts the holds of a user.</summary>
    /// <param name="user">The user.</param>
    /// <returns>The count.</returns>
    public int HoldsOf(Guid user) => _holders.TryGetValue(user, out var n) ? n : 0;

    /// <summary>Registers an origin URL and returns its relay id.</summary>
    /// <param name="url">The URL.</param>
    /// <param name="context">The resolution context.</param>
    /// <param name="isPrefix">Whether it is a directory prefix.</param>
    /// <returns>The id.</returns>
    public string Register(Uri url, ResolvedStream context, bool isPrefix = false)
    {
        ArgumentNullException.ThrowIfNull(url);
        var id = RelayTokenService.ShortId((isPrefix ? "p:" : "u:") + url.AbsoluteUri);
        if (_origins.TryAdd(id, new OriginRef(url, context, isPrefix)))
        {
            _originOrder.Enqueue(id);
            while (_originOrder.Count > 6000 && _originOrder.TryDequeue(out var old))
            {
                _origins.TryRemove(old, out _);
            }
        }
        else
        {
            _origins[id] = new OriginRef(url, context, isPrefix);
        }

        return id;
    }

    /// <summary>Looks up a registered origin.</summary>
    /// <param name="id">The id.</param>
    /// <param name="origin">The origin.</param>
    /// <returns>True when registered.</returns>
    public bool TryGetOrigin(string id, out OriginRef origin) => _origins.TryGetValue(id, out origin!);

    /// <inheritdoc />
    public void Dispose()
    {
        Lock.Dispose();
        if (Fanout is not null)
        {
            _ = Fanout.DisposeAsync().AsTask();
        }
    }
}
