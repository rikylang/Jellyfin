using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Formats;

/// <summary>
/// Converts M3U playlists and XMLTV guides to the internal model.
/// </summary>
public static class FormatConverters
{
    /// <summary>Guide window: from now minus this.</summary>
    public static readonly TimeSpan GuidePast = TimeSpan.FromHours(3);

    /// <summary>Guide window: until now plus this.</summary>
    public static readonly TimeSpan GuideFuture = TimeSpan.FromDays(7);

    /// <summary>Maps a text body returned by a REST endpoint (M3U or XMLTV) into a result.</summary>
    /// <param name="format">The format.</param>
    /// <param name="text">The text.</param>
    /// <param name="ctx">The context.</param>
    /// <param name="role">The endpoint role.</param>
    /// <param name="result">The result to fill.</param>
    public static void MapTextFormat(BodyFormat format, string text, ProviderRunContext ctx, string role, EndpointResult result)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(result);
        if (format == BodyFormat.M3u)
        {
            var playlist = M3uParser.Parse(text);
            if (role == "events")
            {
                result.Events.AddRange(M3uToEvents(playlist, ctx, null));
            }
            else
            {
                result.Channels.AddRange(M3uToChannels(playlist, ctx, null));
            }
        }
        else if (format == BodyFormat.Xmltv)
        {
            var now = ctx.Clock.GetUtcNow();
            var guide = XmlTvParser.Parse(text, now - GuidePast, now + GuideFuture);
            foreach (var ch in guide.Channels)
            {
                result.Channels.Add(new LiveChannel
                {
                    ProviderId = ctx.ProviderId,
                    ExternalId = ch.Id,
                    Key = Keys.Channel(ctx.ProviderId, ch.Id),
                    Name = ch.DisplayNames.FirstOrDefault() ?? ch.Id,
                    EpgId = ch.Id,
                    LogoUrl = ch.Icon,
                    Priority = ctx.Priority,
                });
            }

            result.Programs.AddRange(XmlTvToPrograms(guide, result.Channels));
        }
    }

    /// <summary>Converts an M3U playlist to 24/7 channels.</summary>
    /// <param name="playlist">The playlist.</param>
    /// <param name="ctx">The context.</param>
    /// <param name="spec">The M3U settings (filters).</param>
    /// <returns>The channels.</returns>
    public static List<LiveChannel> M3uToChannels(M3uPlaylist playlist, ProviderRunContext ctx, M3uSpec? spec)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(ctx);
        var list = new List<LiveChannel>();
        var def = ctx.Definition;
        var index = 0;
        foreach (var e in playlist.Entries)
        {
            index++;
            if (!Accept(e.Attr("group-title"), spec?.IncludeGroups, spec?.ExcludeGroups))
            {
                continue;
            }

            if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var name = CleanName(e.Name);
            var externalId = e.Attr("tvg-id") is { } tvg ? tvg + "|" + name : Keys.Hash(e.Url);
            var language = LocaleNormalizer.NormalizeLanguage(e.Attr("tvg-language"))
                ?? LocaleNormalizer.NormalizeLanguage(def.Defaults?.Language)
                ?? LocaleNormalizer.GuessLanguage(e.Name);
            var country = LocaleNormalizer.NormalizeCountry(e.Attr("tvg-country"))
                ?? LocaleNormalizer.NormalizeCountry(def.Defaults?.Country)
                ?? LocaleNormalizer.GuessCountry(e.Name)
                ?? LocaleNormalizer.CountryForLanguage(language);

            var ch = new LiveChannel
            {
                ProviderId = ctx.ProviderId,
                ExternalId = externalId,
                Key = Keys.Channel(ctx.ProviderId, externalId),
                Name = name,
                Number = e.Attr("tvg-chno") ?? e.Attr("channel-number"),
                Category = e.Attr("group-title") ?? def.Defaults?.Category,
                LogoUrl = TransformEngine.AbsoluteUrl(ctx.BaseUrl, e.Attr("tvg-logo")),
                EpgId = e.Attr("tvg-id"),
                Language = language,
                Country = country,
                ContentRating = def.Defaults?.ContentRating,
                Priority = ctx.Priority,
            };
            if (def.Defaults?.Tags is { } tags)
            {
                ch.Tags.AddRange(tags);
            }

            var height = StreamClassifier.HeightFromLabel(e.Name);
            var stream = new LiveStream
            {
                Id = Keys.Stream(ctx.ProviderId, externalId, e.Url),
                ProviderId = ctx.ProviderId,
                Url = e.Url,
                Kind = StreamClassifier.FromUrl(e.Url),
                Height = height,
                QualityLabel = StreamClassifier.LabelFromHeight(height),
                Language = language,
                Country = country,
                Label = def.Name,
                Priority = ctx.Priority,
            };
            foreach (var h in e.Headers)
            {
                stream.Headers[h.Key] = h.Value;
            }

            var existing = list.Find(c => c.Key == ch.Key);
            if (existing is null)
            {
                ch.Streams.Add(stream);
                list.Add(ch);
            }
            else if (!existing.Streams.Exists(s => s.Id == stream.Id))
            {
                existing.Streams.Add(stream);
            }

            if (list.Count >= ctx.Options.MaxItems)
            {
                ctx.Diagnostics.Warn($"Playlist truncated at {ctx.Options.MaxItems} channels.");
                break;
            }
        }

        return list;
    }

    /// <summary>Converts an M3U playlist of events (names such as "Inter vs Milan 20:45") to events.</summary>
    /// <param name="playlist">The playlist.</param>
    /// <param name="ctx">The context.</param>
    /// <param name="spec">The settings (eventTitlePattern, filters).</param>
    /// <returns>The events.</returns>
    public static List<LiveEvent> M3uToEvents(M3uPlaylist playlist, ProviderRunContext ctx, M3uSpec? spec)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(ctx);
        var pattern = string.IsNullOrWhiteSpace(spec?.EventTitlePattern)
            ? @"^(?<title>.+?)\s*[\(\[\-|@]?\s*(?:(?<date>\d{1,2}[/.]\d{1,2}(?:[/.]\d{2,4})?)\s+)?(?<time>\d{1,2}[:.]\d{2})\s*[\)\]]?\s*$"
            : spec!.EventTitlePattern!;
        Regex regex;
        try
        {
            regex = TransformEngine.GetRegex(pattern);
        }
        catch (ArgumentException ex)
        {
            ctx.Diagnostics.Errors.Add("eventTitlePattern: " + ex.Message);
            return new List<LiveEvent>();
        }

        var events = new List<LiveEvent>();
        var localNow = TimeZoneInfo.ConvertTime(ctx.Clock.GetUtcNow(), ctx.TimeZone);
        foreach (var e in playlist.Entries)
        {
            if (!Accept(e.Attr("group-title"), spec?.IncludeGroups, spec?.ExcludeGroups))
            {
                continue;
            }

            Match m;
            try
            {
                m = regex.Match(e.Name);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (!m.Success || !m.Groups["time"].Success)
            {
                continue;
            }

            var timeText = m.Groups["time"].Value.Replace('.', ':');
            if (!TimeSpan.TryParseExact(timeText, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var time))
            {
                continue;
            }

            var day = localNow.Date;
            if (m.Groups["date"].Success && m.Groups["date"].Value.Length > 0)
            {
                var parts = m.Groups["date"].Value.Split('/', '.');
                if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dd) && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm))
                {
                    var yy = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? (y < 100 ? 2000 + y : y) : localNow.Year;
                    try
                    {
                        day = new DateTime(yy, mm, dd, 0, 0, 0, DateTimeKind.Unspecified);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                    }
                }
            }

            var start = DateParser.FromWallClock(day + time, ctx.TimeZone);
            var title = (m.Groups["title"].Success ? m.Groups["title"].Value : e.Name).Trim(' ', '-', '|', ':');
            var externalId = Keys.Hash(title + "|" + start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            var language = LocaleNormalizer.NormalizeLanguage(e.Attr("tvg-language")) ?? LocaleNormalizer.NormalizeLanguage(ctx.Definition.Defaults?.Language) ?? LocaleNormalizer.GuessLanguage(e.Name);
            var ev = events.Find(x => x.ExternalId == externalId);
            if (ev is null)
            {
                ev = new LiveEvent
                {
                    ProviderId = ctx.ProviderId,
                    ExternalId = externalId,
                    Key = Keys.Event(ctx.ProviderId, externalId),
                    Title = title,
                    Category = e.Attr("group-title") ?? ctx.Definition.Defaults?.Category,
                    Start = start,
                    ThumbUrl = e.Attr("tvg-logo"),
                    Participants = ItemMapper.ParticipantsFromTitle(title),
                    Language = language,
                    Country = LocaleNormalizer.NormalizeCountry(e.Attr("tvg-country")) ?? LocaleNormalizer.NormalizeCountry(ctx.Definition.Defaults?.Country),
                    Priority = ctx.Priority,
                };
                ev.SourceProviders.Add(ctx.ProviderId);
                ev.MergedRefs.Add(ctx.ProviderId + "\u001f" + externalId);
                ev.Context["id"] = externalId;
                ev.Context["title"] = title;
                events.Add(ev);
            }

            var stream = new LiveStream
            {
                Id = Keys.Stream(ctx.ProviderId, externalId, e.Url),
                ProviderId = ctx.ProviderId,
                Url = e.Url,
                Kind = StreamClassifier.FromUrl(e.Url),
                Height = StreamClassifier.HeightFromLabel(e.Name),
                Language = language,
                Label = e.Attr("group-title") ?? ctx.Definition.Name,
                Priority = ctx.Priority,
            };
            stream.QualityLabel = StreamClassifier.LabelFromHeight(stream.Height);
            foreach (var h in e.Headers)
            {
                stream.Headers[h.Key] = h.Value;
            }

            if (!ev.Streams.Exists(s => s.Id == stream.Id))
            {
                ev.Streams.Add(stream);
            }
        }

        return events;
    }

    /// <summary>Converts XMLTV programmes to programmes of the given channels (matched by EPG id, then by name).</summary>
    /// <param name="guide">The guide.</param>
    /// <param name="channels">The channels.</param>
    /// <returns>The programmes.</returns>
    public static List<LiveProgram> XmlTvToPrograms(XmlTvGuide guide, IReadOnlyList<LiveChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(guide);
        ArgumentNullException.ThrowIfNull(channels);
        var byEpg = new Dictionary<string, List<LiveChannel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ch in channels)
        {
            var key = ch.EpgId;
            if (string.IsNullOrWhiteSpace(key))
            {
                var match = guide.Channels.FirstOrDefault(g => g.DisplayNames.Any(n => string.Equals(Normalize(n), Normalize(ch.Name), StringComparison.Ordinal)));
                key = match?.Id;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!byEpg.TryGetValue(key, out var l))
            {
                l = new List<LiveChannel>();
                byEpg[key] = l;
            }

            l.Add(ch);
        }

        var programs = new List<LiveProgram>();
        foreach (var p in guide.Programmes)
        {
            if (!byEpg.TryGetValue(p.Channel, out var targets))
            {
                continue;
            }

            foreach (var ch in targets)
            {
                programs.Add(new LiveProgram
                {
                    Id = "p" + Keys.Hash(ch.Key + "|" + p.Start.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
                    ChannelKey = ch.Key,
                    Title = string.IsNullOrWhiteSpace(p.Title) ? ch.Name : p.Title,
                    SubTitle = p.SubTitle,
                    Description = p.Description,
                    Start = p.Start,
                    End = p.Stop > p.Start ? p.Stop : p.Start.AddMinutes(30),
                    Category = p.Categories.FirstOrDefault(),
                    ImageUrl = p.Icon,
                });
            }
        }

        // Fill missing logos from the guide.
        foreach (var ch in channels.Where(c => c.LogoUrl is null && c.EpgId is not null))
        {
            ch.LogoUrl = guide.Channels.FirstOrDefault(g => string.Equals(g.Id, ch.EpgId, StringComparison.OrdinalIgnoreCase))?.Icon;
        }

        return programs;
    }

    /// <summary>Applies include/exclude regexes to a group.</summary>
    /// <param name="group">The group.</param>
    /// <param name="include">Include regex.</param>
    /// <param name="exclude">Exclude regex.</param>
    /// <returns>Whether the entry is kept.</returns>
    public static bool Accept(string? group, string? include, string? exclude)
    {
        var g = group ?? string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(include) && !TransformEngine.GetRegex(include).IsMatch(g))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(exclude) && TransformEngine.GetRegex(exclude).IsMatch(g))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return true;
        }

        return true;
    }

    private static string CleanName(string name)
    {
        var n = name.Trim();
        return n.Length == 0 ? "Channel" : n;
    }

    private static string Normalize(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
