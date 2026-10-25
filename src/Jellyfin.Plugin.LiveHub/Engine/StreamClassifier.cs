using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Heuristics on stream URLs and quality labels.
/// </summary>
public static class StreamClassifier
{
    private static readonly Regex HeightRegex = new(@"(?<h>\d{3,4})\s*[pPiI]\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ResolutionRegex = new(@"\b\d{3,4}\s*[xX×]\s*(?<h>\d{3,4})\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>Detects the kind from a declared type and the URL.</summary>
    /// <param name="declared">Declared type text (hls, m3u8, dash, embed, iframe...).</param>
    /// <param name="url">The URL.</param>
    /// <returns>The kind.</returns>
    public static StreamKind Classify(string? declared, string? url)
    {
        if (!string.IsNullOrWhiteSpace(declared))
        {
            switch (declared.Trim().ToLowerInvariant())
            {
                case "hls": case "m3u8": case "application/x-mpegurl": case "application/vnd.apple.mpegurl": return StreamKind.Hls;
                case "dash": case "mpd": case "application/dash+xml": return StreamKind.Dash;
                case "mp4": case "video/mp4": return StreamKind.Mp4;
                case "ts": case "mpegts": case "video/mp2t": return StreamKind.Ts;
                case "embed": case "iframe": case "page": case "html": case "web": case "player": return StreamKind.Embed;
            }
        }

        return FromUrl(url);
    }

    /// <summary>Detects the kind from the URL path.</summary>
    /// <param name="url">The URL.</param>
    /// <returns>The kind (Auto when unknown).</returns>
    public static StreamKind FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return StreamKind.Auto;
        }

        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.EndsWith(".m3u8", StringComparison.Ordinal) || (path.Contains("/hls/", StringComparison.Ordinal) && path.EndsWith(".m3u", StringComparison.Ordinal)))
        {
            return StreamKind.Hls;
        }

        if (path.EndsWith(".mpd", StringComparison.Ordinal))
        {
            return StreamKind.Dash;
        }

        if (path.EndsWith(".mp4", StringComparison.Ordinal) || path.EndsWith(".m4v", StringComparison.Ordinal))
        {
            return StreamKind.Mp4;
        }

        if (path.EndsWith(".ts", StringComparison.Ordinal) || path.EndsWith(".mpegts", StringComparison.Ordinal))
        {
            return StreamKind.Ts;
        }

        var query = uri.Query.ToLowerInvariant();
        if (query.Contains(".m3u8", StringComparison.Ordinal) || query.Contains("type=m3u8", StringComparison.Ordinal) || query.Contains("output=m3u8", StringComparison.Ordinal))
        {
            return StreamKind.Hls;
        }

        if (path.EndsWith(".html", StringComparison.Ordinal) || path.EndsWith(".php", StringComparison.Ordinal) || path.Contains("/embed", StringComparison.Ordinal)
            || path.Contains("/player", StringComparison.Ordinal) || path.Contains("/watch", StringComparison.Ordinal))
        {
            return StreamKind.Embed;
        }

        return StreamKind.Auto;
    }

    /// <summary>Parses a quality label into a height (4K → 2160, FHD → 1080, HD → 720, SD → 480).</summary>
    /// <param name="label">The label.</param>
    /// <returns>The height or null.</returns>
    public static int? HeightFromLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        try
        {
            var m = HeightRegex.Match(label);
            if (m.Success)
            {
                return int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            }

            m = ResolutionRegex.Match(label);
            if (m.Success)
            {
                return int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        var l = label.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (l.Contains("4K", StringComparison.Ordinal) || l.Contains("UHD", StringComparison.Ordinal) || l.Contains("2160", StringComparison.Ordinal))
        {
            return 2160;
        }

        if (l.Contains("QHD", StringComparison.Ordinal) || l.Contains("1440", StringComparison.Ordinal))
        {
            return 1440;
        }

        if (l.Contains("FHD", StringComparison.Ordinal) || l.Contains("FULLHD", StringComparison.Ordinal) || l.Contains("1080", StringComparison.Ordinal))
        {
            return 1080;
        }

        if (l == "HD" || l.Contains("HDREADY", StringComparison.Ordinal) || l.Contains("720", StringComparison.Ordinal) || l.EndsWith("HD", StringComparison.Ordinal))
        {
            return 720;
        }

        if (l == "SD" || l.Contains("480", StringComparison.Ordinal) || l.Contains("576", StringComparison.Ordinal))
        {
            return 480;
        }

        if (l == "LD" || l.Contains("360", StringComparison.Ordinal))
        {
            return 360;
        }

        if (l.Contains("240", StringComparison.Ordinal) || l == "LOW")
        {
            return 240;
        }

        return null;
    }

    /// <summary>Normalizes a bitrate (values below 50 000 are assumed to be kbps).</summary>
    /// <param name="value">The value.</param>
    /// <returns>Bits per second.</returns>
    public static long? NormalizeBitrate(double? value)
    {
        if (value is null or <= 0)
        {
            return null;
        }

        return value < 50_000 ? (long)(value.Value * 1000) : (long)value.Value;
    }

    /// <summary>Builds a readable quality label from a height.</summary>
    /// <param name="height">The height.</param>
    /// <returns>The label.</returns>
    public static string? LabelFromHeight(int? height) => height switch
    {
        null => null,
        >= 2160 => "4K",
        >= 1440 => "1440p",
        >= 1080 => "1080p",
        >= 720 => "720p",
        >= 480 => "480p",
        _ => height.Value.ToString(CultureInfo.InvariantCulture) + "p",
    };
}
