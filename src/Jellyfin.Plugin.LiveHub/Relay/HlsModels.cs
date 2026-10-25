using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.LiveHub.Relay;

/// <summary>A variant (#EXT-X-STREAM-INF) of an HLS master playlist.</summary>
public sealed class HlsVariant
{
    /// <summary>Gets or sets the absolute URI.</summary>
    public Uri Uri { get; set; } = new("about:blank");

    /// <summary>Gets or sets the raw attribute list.</summary>
    public string Attributes { get; set; } = string.Empty;

    /// <summary>Gets or sets the peak bandwidth (bits/s).</summary>
    public long Bandwidth { get; set; }

    /// <summary>Gets or sets the average bandwidth (bits/s).</summary>
    public long? AverageBandwidth { get; set; }

    /// <summary>Gets or sets the width.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the height.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the codecs (RFC 6381).</summary>
    public string? Codecs { get; set; }

    /// <summary>Gets or sets the frame rate.</summary>
    public double? FrameRate { get; set; }

    /// <summary>Gets or sets the audio group id.</summary>
    public string? AudioGroup { get; set; }

    /// <summary>Gets or sets the subtitles group id.</summary>
    public string? SubtitlesGroup { get; set; }

    /// <summary>Gets a short description ("1080p · 5.0 Mbps").</summary>
    public string Describe => string.Join(" · ", new[] { Height is { } h ? h.ToString(CultureInfo.InvariantCulture) + "p" : null, (Bandwidth / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps", Codecs }.Where(s => s is not null));
}

/// <summary>An alternative rendition (#EXT-X-MEDIA).</summary>
public sealed class HlsRendition
{
    /// <summary>Gets or sets the type (AUDIO, SUBTITLES, VIDEO, CLOSED-CAPTIONS).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the group id.</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>Gets or sets the language.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets a value indicating whether this is the default rendition.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Gets or sets the absolute URI (null = muxed in the variant).</summary>
    public Uri? Uri { get; set; }

    /// <summary>Gets or sets the raw attribute list.</summary>
    public string Attributes { get; set; } = string.Empty;
}

/// <summary>A media segment.</summary>
public sealed class HlsSegment
{
    /// <summary>Gets or sets the media sequence number.</summary>
    public long Sequence { get; set; }

    /// <summary>Gets or sets the duration in seconds.</summary>
    public double Duration { get; set; }

    /// <summary>Gets or sets the EXTINF title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the absolute URI.</summary>
    public Uri Uri { get; set; } = new("about:blank");

    /// <summary>Gets or sets a value indicating whether a discontinuity precedes the segment.</summary>
    public bool Discontinuity { get; set; }

    /// <summary>Gets or sets the effective #EXT-X-KEY line (URI made absolute), if any.</summary>
    public string? KeyLine { get; set; }

    /// <summary>Gets or sets the absolute key URI.</summary>
    public Uri? KeyUri { get; set; }

    /// <summary>Gets or sets the effective #EXT-X-MAP line, if any.</summary>
    public string? MapLine { get; set; }

    /// <summary>Gets or sets the absolute init segment URI.</summary>
    public Uri? MapUri { get; set; }

    /// <summary>Gets the other tags preceding the segment (PROGRAM-DATE-TIME, BYTERANGE...).</summary>
    public List<string> Tags { get; } = new();
}

/// <summary>A parsed HLS playlist (master or media).</summary>
public sealed class HlsPlaylist
{
    /// <summary>Gets or sets a value indicating whether this is a master playlist.</summary>
    public bool IsMaster { get; set; }

    /// <summary>Gets or sets the version.</summary>
    public int Version { get; set; } = 3;

    /// <summary>Gets or sets a value indicating whether segments are independent.</summary>
    public bool IndependentSegments { get; set; }

    /// <summary>Gets the variants (master).</summary>
    public List<HlsVariant> Variants { get; } = new();

    /// <summary>Gets the renditions (master).</summary>
    public List<HlsRendition> Renditions { get; } = new();

    /// <summary>Gets or sets the target duration (media).</summary>
    public double TargetDuration { get; set; }

    /// <summary>Gets or sets the media sequence of the first segment.</summary>
    public long MediaSequence { get; set; }

    /// <summary>Gets or sets a value indicating whether #EXT-X-ENDLIST is present.</summary>
    public bool Ended { get; set; }

    /// <summary>Gets the segments (media).</summary>
    public List<HlsSegment> Segments { get; } = new();

    /// <summary>Returns whether a body looks like an HLS playlist.</summary>
    /// <param name="text">The text.</param>
    /// <returns>True when it starts with #EXTM3U.</returns>
    public static bool LooksLikeHls(string text) => text.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal);

    /// <summary>Parses a playlist.</summary>
    /// <param name="text">The playlist text.</param>
    /// <param name="baseUri">The playlist URL (relative URIs are resolved against it).</param>
    /// <returns>The playlist.</returns>
    public static HlsPlaylist Parse(string text, Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(baseUri);
        var playlist = new HlsPlaylist();
        string? pendingStreamInf = null;
        var pendingTags = new List<string>();
        double? pendingDuration = null;
        string? pendingTitle = null;
        var discontinuity = false;
        string? keyLine = null;
        Uri? keyUri = null;
        string? mapLine = null;
        Uri? mapUri = null;
        long sequence = 0;
        var sequenceSet = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    playlist.IsMaster = true;
                    pendingStreamInf = line["#EXT-X-STREAM-INF:".Length..];
                }
                else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
                {
                    playlist.IsMaster = true;
                    playlist.Renditions.Add(ParseRendition(line["#EXT-X-MEDIA:".Length..], baseUri));
                }
                else if (line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal))
                {
                    playlist.Version = int.TryParse(line.AsSpan("#EXT-X-VERSION:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 3;
                }
                else if (line.StartsWith("#EXT-X-INDEPENDENT-SEGMENTS", StringComparison.Ordinal))
                {
                    playlist.IndependentSegments = true;
                }
                else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                {
                    playlist.TargetDuration = ParseDouble(line["#EXT-X-TARGETDURATION:".Length..]) ?? 0;
                }
                else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
                {
                    if (long.TryParse(line.AsSpan("#EXT-X-MEDIA-SEQUENCE:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                    {
                        playlist.MediaSequence = s;
                        sequence = s;
                        sequenceSet = true;
                    }
                }
                else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
                {
                    var body = line["#EXTINF:".Length..];
                    var comma = body.IndexOf(',', StringComparison.Ordinal);
                    pendingDuration = ParseDouble(comma >= 0 ? body[..comma] : body) ?? 0;
                    pendingTitle = comma >= 0 && comma < body.Length - 1 ? body[(comma + 1)..] : null;
                }
                else if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal) && !line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal))
                {
                    discontinuity = true;
                }
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
                {
                    var attrs = ParseAttributes(line["#EXT-X-KEY:".Length..]);
                    if (attrs.TryGetValue("METHOD", out var method) && string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase))
                    {
                        keyLine = null;
                        keyUri = null;
                    }
                    else
                    {
                        keyUri = attrs.TryGetValue("URI", out var u) ? Resolve(baseUri, u) : null;
                        keyLine = line;
                    }
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                {
                    var attrs = ParseAttributes(line["#EXT-X-MAP:".Length..]);
                    mapUri = attrs.TryGetValue("URI", out var u) ? Resolve(baseUri, u) : null;
                    mapLine = line;
                }
                else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
                {
                    playlist.Ended = true;
                }
                else if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.Ordinal) || line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
                {
                    pendingTags.Add(line);
                }

                continue;
            }

            var uri = Resolve(baseUri, line);
            if (uri is null)
            {
                pendingStreamInf = null;
                continue;
            }

            if (pendingStreamInf is not null)
            {
                playlist.Variants.Add(ParseVariant(pendingStreamInf, uri));
                pendingStreamInf = null;
                continue;
            }

            if (pendingDuration is not null || !playlist.IsMaster)
            {
                var segment = new HlsSegment
                {
                    Sequence = sequence++,
                    Duration = pendingDuration ?? playlist.TargetDuration,
                    Title = pendingTitle,
                    Uri = uri,
                    Discontinuity = discontinuity,
                    KeyLine = keyLine,
                    KeyUri = keyUri,
                    MapLine = mapLine,
                    MapUri = mapUri,
                };
                segment.Tags.AddRange(pendingTags);
                playlist.Segments.Add(segment);
                pendingTags.Clear();
                pendingDuration = null;
                pendingTitle = null;
                discontinuity = false;
            }
        }

        if (!sequenceSet)
        {
            playlist.MediaSequence = 0;
        }

        return playlist;
    }

    /// <summary>Parses an attribute list (KEY=VALUE,KEY="quoted, value").</summary>
    /// <param name="list">The attribute list.</param>
    /// <returns>The attributes (keys upper case).</returns>
    public static Dictionary<string, string> ParseAttributes(string list)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < list.Length)
        {
            var eq = list.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }

            var key = list[i..eq].Trim().TrimStart(',').Trim();
            i = eq + 1;
            string value;
            if (i < list.Length && list[i] == '"')
            {
                var end = list.IndexOf('"', i + 1);
                if (end < 0)
                {
                    end = list.Length;
                }

                value = list[(i + 1)..end];
                i = Math.Min(list.Length, end + 1);
                var comma = list.IndexOf(',', i);
                i = comma < 0 ? list.Length : comma + 1;
            }
            else
            {
                var comma = list.IndexOf(',', i);
                value = (comma < 0 ? list[i..] : list[i..comma]).Trim();
                i = comma < 0 ? list.Length : comma + 1;
            }

            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Replaces (or removes) the URI attribute of a tag line.</summary>
    /// <param name="line">The tag line.</param>
    /// <param name="newUri">The new URI (null removes the attribute).</param>
    /// <returns>The rewritten line.</returns>
    public static string ReplaceUriAttribute(string line, string? newUri)
    {
        ArgumentNullException.ThrowIfNull(line);
        var start = line.IndexOf("URI=\"", StringComparison.Ordinal);
        if (start < 0)
        {
            return line;
        }

        var end = line.IndexOf('"', start + 5);
        if (end < 0)
        {
            return line;
        }

        if (newUri is null)
        {
            var from = start > 0 && line[start - 1] == ',' ? start - 1 : start;
            var to = end + 1 < line.Length && line[end + 1] == ',' && from == start ? end + 2 : end + 1;
            return line.Remove(from, to - from);
        }

        return string.Concat(line.AsSpan(0, start + 5), newUri, line.AsSpan(end));
    }

    /// <summary>Picks a variant according to a selector.</summary>
    /// <param name="variants">The variants.</param>
    /// <param name="selector">The selector.</param>
    /// <returns>The chosen variant, or null when there are none.</returns>
    public static HlsVariant? Choose(IReadOnlyList<HlsVariant> variants, VariantSelector selector)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(selector);
        var usable = variants.Where(v => v.Codecs is null || !IsAudioOnly(v.Codecs)).ToList();
        if (usable.Count == 0)
        {
            usable = variants.ToList();
        }

        if (usable.Count == 0)
        {
            return null;
        }

        IEnumerable<HlsVariant> pool = usable;
        if (selector.MaxBitrate is { } max)
        {
            var under = usable.Where(v => v.Bandwidth <= max).ToList();
            pool = under.Count > 0 ? under : new List<HlsVariant> { usable.MinBy(v => v.Bandwidth)! };
        }

        if (selector.Height is { } h)
        {
            return pool.OrderBy(v => Math.Abs((v.Height ?? 0) - h)).ThenByDescending(v => v.Bandwidth).First();
        }

        return selector.PreferLow
            ? pool.Where(v => (v.Height ?? 480) >= 360).DefaultIfEmpty(pool.MinBy(v => v.Bandwidth)!).MinBy(v => v.Bandwidth)
            : pool.MaxBy(v => v.Bandwidth);
    }

    /// <summary>Renders an attribute list for a synthetic variant.</summary>
    /// <param name="bandwidth">Bandwidth.</param>
    /// <returns>The attributes.</returns>
    public static string SyntheticVariantAttributes(long? bandwidth) => "BANDWIDTH=" + (bandwidth is > 0 ? bandwidth.Value : 2_000_000).ToString(CultureInfo.InvariantCulture);

    /// <summary>Formats a duration for #EXTINF.</summary>
    /// <param name="seconds">The duration.</param>
    /// <returns>The text.</returns>
    public static string FormatDuration(double seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture);

    internal static bool IsAudioOnly(string codecs)
        => codecs.Split(',').Select(c => c.Trim().ToLowerInvariant()).All(c => c.StartsWith("mp4a", StringComparison.Ordinal) || c.StartsWith("ac-3", StringComparison.Ordinal) || c.StartsWith("ec-3", StringComparison.Ordinal) || c.StartsWith("opus", StringComparison.Ordinal));

    private static HlsVariant ParseVariant(string attributes, Uri uri)
    {
        var a = ParseAttributes(attributes);
        var variant = new HlsVariant { Uri = uri, Attributes = attributes };
        if (a.TryGetValue("BANDWIDTH", out var bw) && long.TryParse(bw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            variant.Bandwidth = b;
        }

        if (a.TryGetValue("AVERAGE-BANDWIDTH", out var abw) && long.TryParse(abw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ab))
        {
            variant.AverageBandwidth = ab;
        }

        if (a.TryGetValue("RESOLUTION", out var res))
        {
            var parts = res.Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
            {
                variant.Width = w;
                variant.Height = h;
            }
        }

        variant.Codecs = a.TryGetValue("CODECS", out var codecs) ? codecs : null;
        variant.FrameRate = a.TryGetValue("FRAME-RATE", out var fr) ? ParseDouble(fr) : null;
        variant.AudioGroup = a.TryGetValue("AUDIO", out var ag) ? ag : null;
        variant.SubtitlesGroup = a.TryGetValue("SUBTITLES", out var sg) ? sg : null;
        return variant;
    }

    private static HlsRendition ParseRendition(string attributes, Uri baseUri)
    {
        var a = ParseAttributes(attributes);
        return new HlsRendition
        {
            Attributes = attributes,
            Type = a.TryGetValue("TYPE", out var t) ? t.ToUpperInvariant() : string.Empty,
            GroupId = a.TryGetValue("GROUP-ID", out var g) ? g : string.Empty,
            Language = a.TryGetValue("LANGUAGE", out var l) ? l : null,
            Name = a.TryGetValue("NAME", out var n) ? n : null,
            IsDefault = a.TryGetValue("DEFAULT", out var d) && string.Equals(d, "YES", StringComparison.OrdinalIgnoreCase),
            Uri = a.TryGetValue("URI", out var u) ? Resolve(baseUri, u) : null,
        };
    }

    private static Uri? Resolve(Uri baseUri, string reference)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            return abs;
        }

        if (reference.Contains("://", StringComparison.Ordinal))
        {
            // Other schemes (file://, data:, skd://) are never followed.
            return null;
        }

        return Uri.TryCreate(baseUri, reference, out var rel) ? rel : null;
    }

    private static double? ParseDouble(string s)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}

/// <summary>How the relay chooses a variant of a master playlist.</summary>
public sealed class VariantSelector
{
    /// <summary>Gets the best-quality selector.</summary>
    public static VariantSelector Auto { get; } = new();

    /// <summary>Gets or sets the target height (closest wins).</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the maximum bandwidth.</summary>
    public long? MaxBitrate { get; set; }

    /// <summary>Gets or sets a value indicating whether the lowest reasonable quality is preferred.</summary>
    public bool PreferLow { get; set; }

    /// <summary>Parses the compact form used in relay tokens: "auto", "h1080", "low", "max4000000", "h720-max3000000".</summary>
    /// <param name="text">The text.</param>
    /// <returns>The selector.</returns>
    public static VariantSelector Parse(string? text)
    {
        var s = new VariantSelector();
        foreach (var part in (text ?? "auto").Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith('h') && int.TryParse(part.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
            {
                s.Height = h;
            }
            else if (part.StartsWith("max", StringComparison.Ordinal) && long.TryParse(part.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            {
                s.MaxBitrate = m;
            }
            else if (part == "low")
            {
                s.PreferLow = true;
            }
        }

        return s;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var parts = new List<string>();
        if (Height is { } h)
        {
            parts.Add("h" + h.ToString(CultureInfo.InvariantCulture));
        }

        if (PreferLow)
        {
            parts.Add("low");
        }

        if (MaxBitrate is { } m)
        {
            parts.Add("max" + m.ToString(CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "auto" : string.Join('-', parts);
    }
}
