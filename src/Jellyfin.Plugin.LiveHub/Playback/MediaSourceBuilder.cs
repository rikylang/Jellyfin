using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Resolve;
using Jellyfin.Plugin.LiveHub.Users;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.LiveHub.Playback;

/// <summary>
/// Builds one Jellyfin media source per stream ("1080p · Italiano · Fonte X #2"), ordered by the user's preferences,
/// each pointing to the local relay with a signed token. Known HLS variant data becomes media streams so that ffmpeg
/// probing (several seconds) can be skipped.
/// </summary>
public sealed class MediaSourceBuilder
{
    private readonly RelayService _relay;
    private readonly StreamResolverService _resolver;
    private readonly DefinitionStore _store;
    private readonly ConfigAccessor _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaSourceBuilder"/> class.
    /// </summary>
    /// <param name="relay">Relay.</param>
    /// <param name="resolver">Resolver (cache only).</param>
    /// <param name="store">Definitions (provider names).</param>
    /// <param name="config">Configuration.</param>
    public MediaSourceBuilder(RelayService relay, StreamResolverService resolver, DefinitionStore store, ConfigAccessor config)
    {
        _relay = relay;
        _resolver = resolver;
        _store = store;
        _config = config;
    }

    /// <summary>Gets the variant selector matching the quality preference of a user.</summary>
    /// <param name="prefs">The preferences.</param>
    /// <returns>The selector.</returns>
    public static VariantSelector SelectorFor(UserPreferences prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        var s = new VariantSelector();
        switch (prefs.Quality)
        {
            case QualityPreference.PreferSd:
                s.PreferLow = true;
                break;
            case QualityPreference.MaxBitrate when prefs.MaxBitrateKbps is > 0:
                s.MaxBitrate = prefs.MaxBitrateKbps.Value * 1000L;
                break;
        }

        return s;
    }

    /// <summary>Orders (and filters, for "HD only") the streams for a user.</summary>
    /// <param name="streams">The visible streams.</param>
    /// <param name="view">The user view.</param>
    /// <returns>The ordered streams.</returns>
    public static List<LiveStream> Order(IEnumerable<LiveStream> streams, UserView view)
    {
        ArgumentNullException.ThrowIfNull(streams);
        ArgumentNullException.ThrowIfNull(view);
        var list = streams.ToList();
        var prefs = view.Prefs;
        if (prefs.Quality == QualityPreference.HdOnly && list.Exists(s => s.Height is >= 720))
        {
            list = list.Where(s => s.Height is null or >= 720).ToList();
        }

        int LanguageRank(LiveStream s)
        {
            if (s.Language is null)
            {
                return view.Languages.Count;
            }

            var i = view.Languages.ToList().FindIndex(l => string.Equals(l, s.Language, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? view.Languages.Count + 1 : i;
        }

        double QualityRank(LiveStream s) => prefs.Quality switch
        {
            QualityPreference.PreferSd => s.Height is { } h ? (h >= 360 ? h : 10_000 - h) : 5_000,
            QualityPreference.MaxBitrate when prefs.MaxBitrateKbps is > 0 && s.Bitrate is { } b && b > prefs.MaxBitrateKbps.Value * 1000L => 100_000 - (s.Height ?? 0),
            _ => -(s.Height ?? 600),
        };

        return list
            .OrderBy(LanguageRank)
            .ThenBy(QualityRank)
            .ThenByDescending(s => s.Priority)
            .ToList();
    }

    /// <summary>Converts HLS variant data into Jellyfin media streams (video + audio).</summary>
    /// <param name="variant">The variant.</param>
    /// <param name="language">ISO 639-1 language of the audio.</param>
    /// <returns>The media streams (empty when the codecs are unknown).</returns>
    public static List<MediaStream> MediaStreamsFor(HlsVariant variant, string? language)
    {
        ArgumentNullException.ThrowIfNull(variant);
        var streams = new List<MediaStream>();
        if (string.IsNullOrEmpty(variant.Codecs))
        {
            return streams;
        }

        string? video = null;
        string? audio = null;
        string? profile = null;
        foreach (var c in variant.Codecs.Split(',').Select(c => c.Trim().ToLowerInvariant()))
        {
            if (c.StartsWith("avc1", StringComparison.Ordinal) || c.StartsWith("avc3", StringComparison.Ordinal))
            {
                video = "h264";
                profile = AvcProfile(c);
            }
            else if (c.StartsWith("hvc1", StringComparison.Ordinal) || c.StartsWith("hev1", StringComparison.Ordinal))
            {
                video = "hevc";
            }
            else if (c.StartsWith("av01", StringComparison.Ordinal))
            {
                video = "av1";
            }
            else if (c.StartsWith("vp09", StringComparison.Ordinal))
            {
                video = "vp9";
            }
            else if (c.StartsWith("mp4a.40.34", StringComparison.Ordinal))
            {
                audio = "mp3";
            }
            else if (c.StartsWith("mp4a", StringComparison.Ordinal))
            {
                audio = "aac";
            }
            else if (c.StartsWith("ac-3", StringComparison.Ordinal))
            {
                audio = "ac3";
            }
            else if (c.StartsWith("ec-3", StringComparison.Ordinal))
            {
                audio = "eac3";
            }
            else if (c.StartsWith("opus", StringComparison.Ordinal))
            {
                audio = "opus";
            }
        }

        if (video is null)
        {
            // Unknown video codec: let ffmpeg probe.
            return streams;
        }

        var index = 0;
        streams.Add(new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = index++,
            Codec = video,
            Profile = profile,
            Width = variant.Width,
            Height = variant.Height,
            BitRate = (int?)Math.Min(int.MaxValue, variant.AverageBandwidth ?? variant.Bandwidth),
            AverageFrameRate = (float?)variant.FrameRate,
            RealFrameRate = (float?)variant.FrameRate,
            IsInterlaced = false,
            IsDefault = true,
            PixelFormat = "yuv420p",
        });

        if (audio is not null)
        {
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = index,
                Codec = audio,
                Language = LocaleNormalizer.ToIso6392(language),
                Channels = 2,
                SampleRate = 48000,
                IsDefault = true,
            });
        }

        return streams;
    }

    /// <summary>Builds the media sources of an item for a user.</summary>
    /// <param name="kind">Event or channel.</param>
    /// <param name="key">The item key.</param>
    /// <param name="streams">The visible streams.</param>
    /// <param name="view">The user view.</param>
    /// <returns>The media sources, best first.</returns>
    public List<MediaSourceInfo> Build(ItemKind kind, string key, IEnumerable<LiveStream> streams, UserView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var list = streams.ToList();

        // Known variant heights (pre-resolved masters) improve the ordering of streams without a declared quality.
        var effective = list.Select(s => s.Height is null && MaxVariantHeight(_resolver.Peek(s)) is { } h ? CloneWithHeight(s, h) : s).ToList();
        var ordered = Order(effective, view).Select(e => list.Find(s => s.Id == e.Id) ?? e).ToList();
        var selector = SelectorFor(view.Prefs);
        var perProvider = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<MediaSourceInfo>();
        foreach (var stream in ordered)
        {
            perProvider[stream.ProviderId] = perProvider.TryGetValue(stream.ProviderId, out var n) ? n + 1 : 1;
            foreach (var variant in VariantChoices(_resolver.Peek(stream), view.Prefs, selector))
            {
                var claims = new RelayClaims
                {
                    Type = kind == ItemKind.Channel ? "c" : "e",
                    Key = key,
                    StreamId = stream.Id,
                    Variant = variant,
                    User = view.UserId == Guid.Empty ? string.Empty : view.UserId.ToString("N"),
                };
                result.Add(Create(claims, stream, perProvider[stream.ProviderId], view.UiLanguage));
            }
        }

        // Jellyfin re-sorts media sources by video width (descending, stable). Only the preferred source carries the
        // known media streams, so it stays first and the others keep the preference order (they are probed if chosen).
        foreach (var source in result.Skip(1))
        {
            source.MediaStreams = new List<MediaStream>();
            source.SupportsProbing = true;
        }

        return result;
    }

    /// <summary>
    /// Lists the relay variants offered for a stream: one per distinct HLS rendition height (so the quality can be
    /// picked in the native version selector), ordered by the quality preference, or the preference selector alone.
    /// </summary>
    /// <param name="resolved">The cached resolution, if any.</param>
    /// <param name="prefs">The user preferences.</param>
    /// <param name="selector">The preference selector.</param>
    /// <returns>The variant selectors.</returns>
    public static List<string> VariantChoices(ResolvedStream? resolved, UserPreferences prefs, VariantSelector selector)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        ArgumentNullException.ThrowIfNull(selector);
        var heights = resolved?.Playlist is { IsMaster: true } master
            ? master.Variants.Where(v => v.Height is > 0 && (v.Codecs is null || !HlsPlaylist.IsAudioOnly(v.Codecs)))
                .GroupBy(v => v.Height!.Value)
                .Select(g => (Height: g.Key, Bandwidth: g.Max(v => v.Bandwidth)))
                .ToList()
            : new List<(int Height, long Bandwidth)>();
        if (heights.Count < 2)
        {
            return new List<string> { selector.ToString() };
        }

        IEnumerable<(int Height, long Bandwidth)> ordered = prefs.Quality switch
        {
            QualityPreference.PreferSd => heights.OrderBy(h => h.Height >= 360 ? 0 : 1).ThenBy(h => h.Height),
            QualityPreference.HdOnly => heights.Any(h => h.Height >= 720) ? heights.Where(h => h.Height >= 720).OrderByDescending(h => h.Height) : heights.OrderByDescending(h => h.Height),
            QualityPreference.MaxBitrate when selector.MaxBitrate is { } max => heights.OrderBy(h => h.Bandwidth <= max ? 0 : 1).ThenByDescending(h => h.Height),
            _ => heights.OrderByDescending(h => h.Height),
        };

        return ordered.Take(4).Select(h => "h" + h.Height.ToString(CultureInfo.InvariantCulture)).ToList();
    }

    private static int? MaxVariantHeight(ResolvedStream? resolved)
        => resolved?.Playlist is { IsMaster: true } m ? m.Variants.Max(v => v.Height) : null;

    private static LiveStream CloneWithHeight(LiveStream s, int height) => new()
    {
        Id = s.Id,
        ProviderId = s.ProviderId,
        Url = s.Url,
        Kind = s.Kind,
        QualityLabel = s.QualityLabel,
        Height = height,
        Bitrate = s.Bitrate,
        Language = s.Language,
        Country = s.Country,
        Label = s.Label,
        Priority = s.Priority,
    };

    /// <summary>Builds the media source of a token (used when a live stream is opened).</summary>
    /// <param name="claims">The claims.</param>
    /// <param name="stream">The stream.</param>
    /// <param name="ordinal">Number of the stream within its provider.</param>
    /// <param name="uiLanguage">UI language.</param>
    /// <returns>The media source.</returns>
    public MediaSourceInfo Create(RelayClaims claims, LiveStream stream, int ordinal, string uiLanguage)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(stream);
        var cfg = _config.Current;
        var token = _relay.CreateToken(claims);
        var resolved = _resolver.Peek(stream);
        var kind = resolved?.Kind ?? stream.Kind;
        RelayMode? mode = kind is StreamKind.Auto or StreamKind.Embed ? null : RelayService.ModeOf(kind);
        var selector = VariantSelector.Parse(claims.Variant);
        var variant = resolved?.Playlist is { IsMaster: true } master ? HlsPlaylist.Choose(master.Variants, selector) : null;
        var mediaStreams = variant is null ? new List<MediaStream>() : MediaStreamsFor(variant, stream.Language);
        var explicitVariant = selector.Height is not null && variant?.Height is not null;
        var height = explicitVariant ? variant!.Height : stream.Height ?? variant?.Height;
        return new MediaSourceInfo
        {
            // Deterministic GUID: some clients expect media source ids in GUID format.
            Id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("livehub|" + claims.SessionKey)).AsSpan(0, 16)).ToString("N"),
            Name = Name(stream, height, ordinal, uiLanguage, explicitVariant),
            Path = _relay.BuildUrl(token, mode),
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Container = mode switch
            {
                RelayMode.Ts => "mpegts",
                RelayMode.File => "mp4",
                RelayMode.Dash => "dash",
                _ => "hls",
            },
            IsRemote = true,
            IsInfiniteStream = mode != RelayMode.File,
            RequiresOpening = true,
            RequiresClosing = true,
            OpenToken = token,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            SupportsProbing = mediaStreams.Count == 0,
            MediaStreams = mediaStreams,
            AnalyzeDurationMs = cfg.FastStart ? Math.Clamp(cfg.AnalyzeDurationMs, 500, 10_000) : null,
            Bitrate = (int?)Math.Min(int.MaxValue, variant?.Bandwidth ?? stream.Bitrate ?? 0) is > 0 and var b ? b : null,
            ReadAtNativeFramerate = false,
            IgnoreDts = mode == RelayMode.Ts,
            GenPtsInput = mode == RelayMode.Ts,
        };
    }

    private static string? AvcProfile(string codec)
    {
        var dot = codec.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0 || codec.Length < dot + 3 || !int.TryParse(codec.AsSpan(dot + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var p))
        {
            return null;
        }

        return p switch
        {
            66 => "Constrained Baseline",
            77 => "Main",
            100 => "High",
            _ => null,
        };
    }

    private string Name(LiveStream stream, int? height, int ordinal, string uiLanguage, bool explicitVariant)
    {
        var parts = new List<string>();
        var quality = !explicitVariant && stream.QualityLabel is { Length: > 0 } q && q.Length <= 12 ? q : StreamClassifier.LabelFromHeight(height);
        parts.Add(quality ?? "Auto");
        if (!string.IsNullOrEmpty(stream.Language))
        {
            parts.Add(LocaleNormalizer.LanguageName(stream.Language, uiLanguage));
        }

        var source = _store.Get(stream.ProviderId)?.Name ?? stream.ProviderId;
        if (!string.IsNullOrWhiteSpace(stream.Label) && !string.Equals(stream.Label, quality, StringComparison.OrdinalIgnoreCase))
        {
            source = stream.Label.Length <= 24 ? stream.Label : source;
        }

        var shortSource = source.Split('·')[0].Trim();
        parts.Add(shortSource + " #" + ordinal.ToString(CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }
}
