using System;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Formats;
using Xunit;

namespace Jellyfin.Plugin.LiveHub.Tests.Engine;

public class DateParserTests
{
    private static readonly TimeZoneInfo Rome = DateParser.FindZone("Europe/Rome")!;

    [Fact]
    public void UnixSecondsAndMilliseconds()
    {
        var expected = new DateTimeOffset(2026, 10, 25, 19, 45, 0, TimeSpan.Zero);
        Assert.Equal(expected, DateParser.Parse("1792957500", "unix", null));
        Assert.Equal(expected, DateParser.Parse("1792957500000", "unixMs", null));
        Assert.Equal(expected, DateParser.Parse("1792957500", "auto", null));
        Assert.Equal(expected, DateParser.Parse("1792957500000", "auto", null));
    }

    [Fact]
    public void IsoWithOffsetWinsOverZone()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 19, 45, 0, TimeSpan.Zero), DateParser.Parse("2026-10-25T20:45:00+01:00", "iso8601", Rome));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 19, 45, 0, TimeSpan.Zero), DateParser.Parse("2026-10-25T19:45:00Z", "auto", Rome));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 45, 0, TimeSpan.Zero), DateParser.Parse("2026-10-25T19:45:00-05:00", "auto", Rome));
    }

    [Fact]
    public void WallClockUsesTheZoneIncludingDst()
    {
        // 25 Oct 2026 is the DST change day in Europe: 01:30 local is UTC+2 before, 20:45 is UTC+1 after.
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 19, 45, 0, TimeSpan.Zero), DateParser.Parse("25/10/2026 20:45", "dd/MM/yyyy HH:mm", Rome));
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 18, 45, 0, TimeSpan.Zero), DateParser.Parse("01/07/2026 20:45", "dd/MM/yyyy HH:mm", Rome));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 14, 0, 0, TimeSpan.Zero), DateParser.Parse("2026-10-25 14:00:00", "yyyy-MM-dd HH:mm:ss", TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 13, 0, 0, TimeSpan.Zero), DateParser.Parse("2026-10-25 14:00", "auto", Rome));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("99/99/9999 99:99")]
    public void InvalidValuesReturnNull(string? raw)
    {
        Assert.Null(DateParser.Parse(raw, "dd/MM/yyyy HH:mm", Rome));
    }

    [Fact]
    public void Rfc1123()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 19, 45, 0, TimeSpan.Zero), DateParser.Parse("Sun, 25 Oct 2026 19:45:00 GMT", "rfc1123", Rome));
    }

    [Fact]
    public void XmlTvDates()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 16, 0, 0, TimeSpan.Zero), XmlTvParser.ParseDate("20261025180000 +0200"));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 18, 0, 0, TimeSpan.Zero), XmlTvParser.ParseDate("20261025180000"));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), XmlTvParser.ParseDate("202610251800 -0500"));
        Assert.Null(XmlTvParser.ParseDate("garbage"));
    }

    [Fact]
    public void UnknownZoneIsNull()
    {
        Assert.Null(DateParser.FindZone("Mars/Olympus"));
        Assert.Same(TimeZoneInfo.Utc, DateParser.FindZone("UTC"));
    }
}
