using System;
using System.Collections.Concurrent;
using System.Globalization;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Parses dates in the formats found in live APIs: unix seconds/milliseconds, ISO 8601, RFC 1123 and custom .NET patterns
/// (with an IANA time zone for values without offset).
/// </summary>
public static class DateParser
{
    private static readonly ConcurrentDictionary<string, TimeZoneInfo?> Zones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds a time zone by IANA or Windows id (cached). Returns null when unknown.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The zone or null.</returns>
    public static TimeZoneInfo? FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return Zones.GetOrAdd(id.Trim(), static z =>
        {
            if (string.Equals(z, "UTC", StringComparison.OrdinalIgnoreCase) || string.Equals(z, "Z", StringComparison.OrdinalIgnoreCase) || string.Equals(z, "GMT", StringComparison.OrdinalIgnoreCase))
            {
                return TimeZoneInfo.Utc;
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(z);
            }
            catch (TimeZoneNotFoundException)
            {
                return null;
            }
            catch (InvalidTimeZoneException)
            {
                return null;
            }
        });
    }

    /// <summary>Parses a date.</summary>
    /// <param name="raw">The raw value.</param>
    /// <param name="format">auto | unix | unixMs | iso8601 | rfc1123 | .NET pattern.</param>
    /// <param name="zone">Zone for values without offset (defaults to UTC).</param>
    /// <returns>The instant, or null when it cannot be parsed.</returns>
    public static DateTimeOffset? Parse(string? raw, string? format, TimeZoneInfo? zone)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim();
        zone ??= TimeZoneInfo.Utc;
        var fmt = string.IsNullOrWhiteSpace(format) ? "auto" : format.Trim();

        switch (fmt)
        {
            case "unix":
                return FromNumber(s, millis: false);
            case "unixMs":
                return FromNumber(s, millis: true);
            case "auto":
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                {
                    var abs = Math.Abs(n);
                    return abs > 1e14 ? Safe(() => DateTimeOffset.FromUnixTimeMilliseconds((long)(n / 1000)))
                        : abs > 1e11 ? Safe(() => DateTimeOffset.FromUnixTimeMilliseconds((long)n))
                        : Safe(() => DateTimeOffset.FromUnixTimeMilliseconds((long)(n * 1000)));
                }

                return ParseFree(s, zone);
            case "iso8601":
                return ParseFree(s, zone);
            case "rfc1123":
                return DateTimeOffset.TryParseExact(s, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r) ? r : ParseFree(s, zone);
            default:
                return ParseExact(s, fmt, zone);
        }
    }

    private static DateTimeOffset? FromNumber(string s, bool millis)
    {
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            return null;
        }

        return Safe(() => DateTimeOffset.FromUnixTimeMilliseconds((long)(millis ? n : n * 1000)));
    }

    private static DateTimeOffset? ParseFree(string s, TimeZoneInfo zone)
    {
        // An explicit offset or Z wins; otherwise the value is wall-clock time in the given zone.
        if (HasOffset(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withOffset))
        {
            return withOffset.ToUniversalTime();
        }

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local))
        {
            return FromWallClock(local, zone);
        }

        return null;
    }

    private static DateTimeOffset? ParseExact(string s, string pattern, TimeZoneInfo zone)
    {
        var hasZone = pattern.Contains('z', StringComparison.Ordinal) || pattern.Contains('K', StringComparison.Ordinal);
        if (hasZone && DateTimeOffset.TryParseExact(s, pattern, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto))
        {
            return dto.ToUniversalTime();
        }

        if (DateTime.TryParseExact(s, pattern, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
        {
            return FromWallClock(dt, zone);
        }

        // Tolerate a pattern mismatch when the value is ISO-like.
        return ParseFree(s, zone);
    }

    /// <summary>Converts a wall-clock time in a zone to an instant.</summary>
    /// <param name="wallClock">The local time.</param>
    /// <param name="zone">The zone.</param>
    /// <returns>The instant.</returns>
    public static DateTimeOffset FromWallClock(DateTime wallClock, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        var offset = zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    private static bool HasOffset(string s)
    {
        if (s.EndsWith('Z') || s.EndsWith('z'))
        {
            return true;
        }

        var t = s.IndexOf('T', StringComparison.Ordinal);
        var tail = t >= 0 ? s[t..] : (s.Length > 10 ? s[10..] : string.Empty);
        return tail.Contains('+', StringComparison.Ordinal) || tail.LastIndexOf('-') > 0
            || tail.Contains("GMT", StringComparison.OrdinalIgnoreCase) || tail.Contains("UTC", StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset? Safe(Func<DateTimeOffset> f)
    {
        try
        {
            return f();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
