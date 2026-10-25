using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LiveHub.Formats;

/// <summary>
/// Parser for IPTV M3U/M3U8 playlists (#EXTM3U, #EXTINF attributes, #EXTGRP, #EXTVLCOPT and #KODIPROP headers).
/// </summary>
public static class M3uParser
{
    private static readonly Regex AttributeRegex = new(
        "(?<key>[A-Za-z0-9_\\-:.]+)\\s*=\\s*(?:\"(?<v1>[^\"]*)\"|'(?<v2>[^']*)'|(?<v3>[^\\s,\"']+))",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>Returns whether a text is an HLS playlist (not an IPTV channel list).</summary>
    /// <param name="text">The text.</param>
    /// <returns>True for HLS master/media playlists.</returns>
    public static bool IsHlsPlaylist(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal)
            || text.Contains("#EXT-X-TARGETDURATION", StringComparison.Ordinal)
            || text.Contains("#EXT-X-MEDIA-SEQUENCE", StringComparison.Ordinal);
    }

    /// <summary>Parses a playlist.</summary>
    /// <param name="text">The playlist text.</param>
    /// <returns>The playlist.</returns>
    public static M3uPlaylist Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var playlist = new M3uPlaylist();
        M3uEntry? pending = null;
        string? group = null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(text);
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kv in Attributes(line))
                {
                    playlist.Attributes[kv.Key] = kv.Value;
                }

                continue;
            }

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                pending = ParseExtInf(line);
                continue;
            }

            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                group = line[8..].Trim();
                continue;
            }

            if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
            {
                var opt = line[11..];
                var eq = opt.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    var key = opt[..eq].Trim().ToLowerInvariant();
                    var value = opt[(eq + 1)..].Trim();
                    switch (key)
                    {
                        case "http-user-agent": headers["User-Agent"] = value; break;
                        case "http-referrer": case "http-referer": headers["Referer"] = value; break;
                        case "http-origin": headers["Origin"] = value; break;
                    }
                }

                continue;
            }

            if (line.StartsWith("#KODIPROP:", StringComparison.OrdinalIgnoreCase))
            {
                var prop = line[10..];
                const string Key = "inputstream.adaptive.stream_headers=";
                if (prop.StartsWith(Key, StringComparison.OrdinalIgnoreCase) || prop.StartsWith("inputstream.adaptive.manifest_headers=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = prop[(prop.IndexOf('=', StringComparison.Ordinal) + 1)..];
                    foreach (var pair in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = pair.IndexOf('=', StringComparison.Ordinal);
                        if (eq > 0)
                        {
                            headers[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
                        }
                    }
                }

                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            // URL line. Kodi style "url|User-Agent=x&Referer=y" headers are supported.
            var url = line;
            var pipe = url.IndexOf('|', StringComparison.Ordinal);
            if (pipe > 0)
            {
                foreach (var pair in url[(pipe + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=', StringComparison.Ordinal);
                    if (eq > 0)
                    {
                        headers[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
                    }
                }

                url = url[..pipe];
            }

            var entry = pending ?? new M3uEntry { Name = url };
            entry.Url = url;
            if (group is not null && !entry.Attributes.ContainsKey("group-title"))
            {
                entry.Attributes["group-title"] = group;
            }

            foreach (var h in headers)
            {
                entry.Headers[h.Key] = h.Value;
            }

            playlist.Entries.Add(entry);
            pending = null;
            group = null;
            headers.Clear();
        }

        return playlist;
    }

    private static M3uEntry ParseExtInf(string line)
    {
        var entry = new M3uEntry();
        var body = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..];

        // The display name follows the first comma that is not inside quotes (names may contain commas).
        var inQuotes = '\0';
        var nameStart = -1;
        for (var i = 0; i < body.Length && nameStart < 0; i++)
        {
            var c = body[i];
            if (inQuotes != '\0')
            {
                if (c == inQuotes)
                {
                    inQuotes = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                inQuotes = c;
            }
            else if (c == ',')
            {
                nameStart = i;
            }
        }

        var attrs = nameStart >= 0 ? body[..nameStart] : body;
        entry.Name = nameStart >= 0 ? body[(nameStart + 1)..].Trim() : string.Empty;
        var space = attrs.IndexOf(' ', StringComparison.Ordinal);
        var durationText = space > 0 ? attrs[..space] : attrs;
        if (double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            entry.Duration = d;
        }

        foreach (var kv in Attributes(attrs))
        {
            entry.Attributes[kv.Key] = kv.Value;
        }

        if (string.IsNullOrEmpty(entry.Name) && entry.Attributes.TryGetValue("tvg-name", out var tvgName))
        {
            entry.Name = tvgName;
        }

        return entry;
    }

    private static IEnumerable<KeyValuePair<string, string>> Attributes(string text)
    {
        MatchCollection matches;
        try
        {
            matches = AttributeRegex.Matches(text);
        }
        catch (RegexMatchTimeoutException)
        {
            yield break;
        }

        foreach (Match m in matches)
        {
            var value = m.Groups["v1"].Success ? m.Groups["v1"].Value : m.Groups["v2"].Success ? m.Groups["v2"].Value : m.Groups["v3"].Value;
            yield return new KeyValuePair<string, string>(m.Groups["key"].Value.ToLowerInvariant(), value.Trim());
        }
    }
}
