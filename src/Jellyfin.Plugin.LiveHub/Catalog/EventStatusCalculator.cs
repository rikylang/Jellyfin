using System;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Catalog;

/// <summary>
/// Computes the effective status and end of an event from the API flags, the start time and the estimated duration
/// (event duration → category rules → default).
/// </summary>
public static class EventStatusCalculator
{
    /// <summary>Gets the estimated end of an event.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>The end.</returns>
    public static DateTimeOffset EstimatedEnd(LiveEvent ev, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(config);
        if (ev.End is { } end && end > ev.Start)
        {
            return end;
        }

        return ev.Start.AddMinutes(DurationMinutes(ev, config));
    }

    /// <summary>Gets the estimated duration in minutes.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>Minutes.</returns>
    public static int DurationMinutes(LiveEvent ev, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(config);
        if (ev.DurationMinutes is > 0)
        {
            return ev.DurationMinutes.Value;
        }

        var haystack = (ev.Category ?? string.Empty) + " | " + (ev.Competition ?? string.Empty);
        foreach (var rule in config.CategoryDurations)
        {
            if (string.IsNullOrWhiteSpace(rule.Match) || rule.Minutes <= 0)
            {
                continue;
            }

            try
            {
                if (TransformEngine.GetRegex(rule.Match).IsMatch(haystack))
                {
                    return rule.Minutes;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
            }
        }

        return Math.Clamp(config.DefaultEventDurationMinutes, 5, 1440);
    }

    /// <summary>Computes the effective status.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="now">Now.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>The status.</returns>
    public static EventStatus Status(LiveEvent ev, DateTimeOffset now, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(ev);
        if (ev.StatusFlag == EventStatus.Ended)
        {
            return EventStatus.Ended;
        }

        var end = EstimatedEnd(ev, config);
        if (ev.StatusFlag == EventStatus.Live || ev.IsLiveFlag == true)
        {
            // Trust the API, but not forever (stale flags): stop at end + 3 hours.
            return now < end.AddHours(3) ? EventStatus.Live : EventStatus.Ended;
        }

        if (now < ev.Start)
        {
            return EventStatus.Upcoming;
        }

        if (now >= end)
        {
            return EventStatus.Ended;
        }

        // In the time window. An explicit "not live" right after the start means "delayed", not "on air".
        if (ev.IsLiveFlag == false && ev.StatusFlag == EventStatus.Upcoming && now < ev.Start.AddMinutes(30))
        {
            return EventStatus.Upcoming;
        }

        return EventStatus.Live;
    }

    /// <summary>Returns whether the event must be removed from the catalog.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="now">Now.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>True when ended for longer than the grace period or too far in the future.</returns>
    public static bool IsExpired(LiveEvent ev, DateTimeOffset now, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(ev);
        ArgumentNullException.ThrowIfNull(config);
        if (ev.Start > now.AddDays(Math.Clamp(config.UpcomingDays, 1, 30)))
        {
            return true;
        }

        if (Status(ev, now, config) != EventStatus.Ended)
        {
            return false;
        }

        var end = ev.StatusFlag == EventStatus.Ended && ev.Start < now ? Min(now, EstimatedEnd(ev, config)) : EstimatedEnd(ev, config);
        return now > end.AddMinutes(Math.Clamp(config.EndedGraceMinutes, 0, 1440));
    }

    /// <summary>Gets the display bucket.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="now">Now.</param>
    /// <param name="config">The configuration.</param>
    /// <returns>The bucket.</returns>
    public static DisplayBucket Bucket(LiveEvent ev, DateTimeOffset now, PluginConfiguration config)
    {
        var status = Status(ev, now, config);
        return status switch
        {
            EventStatus.Live => DisplayBucket.Live,
            EventStatus.Ended => DisplayBucket.Ended,
            _ => (ev.Start - now) <= TimeSpan.FromMinutes(15) ? DisplayBucket.Imminent
                : (ev.Start - now) <= TimeSpan.FromHours(1) ? DisplayBucket.Soon
                : DisplayBucket.Later,
        };
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
