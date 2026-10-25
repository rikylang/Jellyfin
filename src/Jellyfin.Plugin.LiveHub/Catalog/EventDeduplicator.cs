using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Catalog;

/// <summary>
/// Merges the same event coming from different providers: same participants (or normalized title) and start within
/// ±window. The highest priority provider wins the fields; streams are united.
/// </summary>
public static class EventDeduplicator
{
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "fc", "cf", "ac", "as", "sc", "ssc", "afc", "cfc", "bc", "sk", "fk", "nk", "us", "ud", "cd", "sd", "rc", "rcd", "ss",
        "calcio", "club", "football", "futbol", "the", "live", "diretta", "en", "vivo", "stream", "hd", "fhd", "vs", "v", "x",
    };

    /// <summary>Deduplicates events.</summary>
    /// <param name="events">Events from all providers.</param>
    /// <param name="window">Start-time tolerance.</param>
    /// <param name="aliases">Receives merged key → primary key.</param>
    /// <returns>The merged events.</returns>
    public static List<LiveEvent> Merge(IEnumerable<LiveEvent> events, TimeSpan window, IDictionary<string, string> aliases)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(aliases);
        var groups = new List<List<LiveEvent>>();
        var byKey = new Dictionary<string, List<List<LiveEvent>>>(StringComparer.Ordinal);
        foreach (var ev in events.OrderByDescending(e => e.Priority).ThenByDescending(e => e.Streams.Count).ThenBy(e => e.ProviderId, StringComparer.Ordinal))
        {
            var key = MatchKey(ev);
            if (!byKey.TryGetValue(key, out var candidates))
            {
                candidates = new List<List<LiveEvent>>();
                byKey[key] = candidates;
            }

            var group = candidates.FirstOrDefault(g => Math.Abs((g[0].Start - ev.Start).TotalMinutes) <= window.TotalMinutes
                && !g.Exists(x => x.ProviderId == ev.ProviderId && x.ExternalId != ev.ExternalId));
            if (group is null)
            {
                group = new List<LiveEvent>();
                candidates.Add(group);
                groups.Add(group);
            }

            group.Add(ev);
        }

        var result = new List<LiveEvent>(groups.Count);
        foreach (var group in groups)
        {
            var merged = MergeGroup(group);
            foreach (var other in group.Skip(1))
            {
                aliases[other.Key] = merged.Key;
            }

            result.Add(merged);
        }

        return result;
    }

    /// <summary>Builds the matching key of an event.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>The key.</returns>
    public static string MatchKey(LiveEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (ev.Participants.Count >= 2)
        {
            var names = ev.Participants.Take(2).Select(p => Normalize(p.Name)).Where(n => n.Length > 0).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (names.Count == 2)
            {
                return "p:" + string.Join('|', names);
            }
        }

        return "t:" + Normalize(ev.Title);
    }

    /// <summary>Normalizes a name: lower case, no accents, no punctuation, no noise tokens.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !NoiseTokens.Contains(t));
        return string.Join(' ', tokens);
    }

    private static LiveEvent MergeGroup(List<LiveEvent> group)
    {
        var primary = group[0];
        if (group.Count == 1)
        {
            return primary;
        }

        var merged = new LiveEvent
        {
            Key = primary.Key,
            ProviderId = primary.ProviderId,
            ExternalId = primary.ExternalId,
            Title = primary.Title,
            Category = primary.Category,
            CategoryId = primary.CategoryId,
            Competition = primary.Competition,
            Start = primary.Start,
            End = primary.End,
            DurationMinutes = primary.DurationMinutes,
            IsLiveFlag = primary.IsLiveFlag,
            StatusFlag = primary.StatusFlag,
            Popularity = primary.Popularity,
            PosterUrl = primary.PosterUrl,
            ThumbUrl = primary.ThumbUrl,
            LogoUrl = primary.LogoUrl,
            Participants = primary.Participants.Select(p => new Participant { Name = p.Name, LogoUrl = p.LogoUrl }).ToList(),
            Country = primary.Country,
            Language = primary.Language,
            Description = primary.Description,
            PageUrl = primary.PageUrl,
            Tags = primary.Tags.ToList(),
            ContentRating = primary.ContentRating,
            Priority = primary.Priority,
            HasLazyStreams = group.Exists(e => e.HasLazyStreams),
            Context = new Dictionary<string, string>(primary.Context, StringComparer.Ordinal),
        };

        foreach (var other in group)
        {
            merged.Competition ??= other.Competition;
            merged.Category ??= other.Category;
            merged.End ??= other.End;
            merged.DurationMinutes ??= other.DurationMinutes;
            merged.PosterUrl ??= other.PosterUrl;
            merged.ThumbUrl ??= other.ThumbUrl;
            merged.LogoUrl ??= other.LogoUrl;
            merged.Description ??= other.Description;
            merged.Country ??= other.Country;
            merged.Language ??= other.Language;
            merged.PageUrl ??= other.PageUrl;
            merged.ContentRating ??= other.ContentRating;
            if (other.IsLiveFlag == true || other.StatusFlag == EventStatus.Live)
            {
                merged.IsLiveFlag = true;
            }

            if (other.Popularity is { } pop && (merged.Popularity is null || pop > merged.Popularity))
            {
                merged.Popularity = pop;
            }

            for (var i = 0; i < Math.Min(merged.Participants.Count, other.Participants.Count); i++)
            {
                merged.Participants[i].LogoUrl ??= other.Participants[i].LogoUrl;
            }

            foreach (var tag in other.Tags.Where(t => !merged.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
            {
                merged.Tags.Add(tag);
            }

            if (!merged.SourceProviders.Contains(other.ProviderId))
            {
                merged.SourceProviders.Add(other.ProviderId);
            }

            foreach (var r in other.MergedRefs.Where(r => !merged.MergedRefs.Contains(r)))
            {
                merged.MergedRefs.Add(r);
            }
        }

        var seenUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in group.SelectMany(e => e.Streams).OrderByDescending(s => s.Priority))
        {
            if (seenUrls.Add(s.Url))
            {
                merged.Streams.Add(s);
            }
        }

        return merged;
    }
}
