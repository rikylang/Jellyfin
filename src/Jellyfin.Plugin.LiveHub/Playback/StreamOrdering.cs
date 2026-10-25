using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Playback;

/// <summary>Ordering rules shared by the media source list and the relay failover.</summary>
public static class StreamOrdering
{
    /// <summary>
    /// Orders the failover candidates of a stream: the stream itself, then the same language, then the closest quality,
    /// then the provider priority (other providers included).
    /// </summary>
    /// <param name="primary">The chosen stream.</param>
    /// <param name="all">Every stream of the item.</param>
    /// <returns>The candidates, primary first.</returns>
    public static List<LiveStream> FailoverOrder(LiveStream? primary, IEnumerable<LiveStream> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        var list = all.ToList();
        if (primary is null)
        {
            return list.OrderByDescending(s => s.Priority).ThenByDescending(s => s.Height ?? 0).ToList();
        }

        var rest = list.Where(s => !ReferenceEquals(s, primary) && s.Id != primary.Id)
            .OrderByDescending(s => SameLanguage(s, primary))
            .ThenBy(s => Math.Abs((s.Height ?? 720) - (primary.Height ?? 720)))
            .ThenBy(s => s.ProviderId == primary.ProviderId ? 0 : 1)
            .ThenByDescending(s => s.Priority);
        return new[] { primary }.Concat(rest).ToList();
    }

    private static bool SameLanguage(LiveStream a, LiveStream b)
        => a.Language is null || b.Language is null || string.Equals(a.Language, b.Language, StringComparison.OrdinalIgnoreCase);
}
