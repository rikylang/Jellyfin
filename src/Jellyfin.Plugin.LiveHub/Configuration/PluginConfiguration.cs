using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiveHub.Configuration;

/// <summary>
/// Global plugin configuration (serialized as XML by Jellyfin). Provider definitions are stored separately as JSON files.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the UI language of the plugin pages: auto, it or en.</summary>
    public string UiLanguage { get; set; } = "auto";

    /// <summary>Gets or sets the default preferred language (ISO 639-1) for users without preferences.</summary>
    public string DefaultLanguage { get; set; } = "it";

    /// <summary>Gets or sets the comma separated fallback languages.</summary>
    public string FallbackLanguages { get; set; } = "en";

    /// <summary>Gets or sets the default IANA time zone (empty = server time zone).</summary>
    public string DefaultTimeZone { get; set; } = string.Empty;

    /// <summary>Gets or sets the default polling interval for live endpoints, in seconds.</summary>
    public int LiveRefreshSeconds { get; set; } = 45;

    /// <summary>Gets or sets the default polling interval for today's schedule, in seconds.</summary>
    public int TodayRefreshSeconds { get; set; } = 300;

    /// <summary>Gets or sets the default polling interval for the full calendar, in seconds.</summary>
    public int FullRefreshSeconds { get; set; } = 1800;

    /// <summary>Gets or sets the default duration of an event without end time, in minutes.</summary>
    public int DefaultEventDurationMinutes { get; set; } = 120;

    /// <summary>Gets or sets the grace period after the estimated end before an event is removed, in minutes.</summary>
    public int EndedGraceMinutes { get; set; } = 10;

    /// <summary>Gets or sets how many days of upcoming events are kept.</summary>
    public int UpcomingDays { get; set; } = 7;

    /// <summary>
    /// Gets or sets a value indicating whether first-run defaults were applied.
    /// Lists start empty because XmlSerializer appends to pre-populated collections.
    /// </summary>
    public bool DefaultsApplied { get; set; }

    /// <summary>Gets or sets the per-category estimated durations.</summary>
    public List<CategoryDurationRule> CategoryDurations { get; set; } = new();

    /// <summary>Gets or sets the region mapping rules (category/competition/title → country).</summary>
    public List<RegionRule> RegionRules { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether equal events from different providers are merged.</summary>
    public bool EnableDeduplication { get; set; } = true;

    /// <summary>Gets or sets the start-time tolerance used by deduplication, in minutes.</summary>
    public int DedupWindowMinutes { get; set; } = 15;

    /// <summary>Gets or sets how many events the "Popular" folder shows.</summary>
    public int PopularCount { get; set; } = 20;

    /// <summary>Gets or sets a value indicating whether the Live TV integration is enabled.</summary>
    public bool EnableLiveTv { get; set; } = true;

    /// <summary>Gets or sets the first channel number used for 24/7 channels.</summary>
    public int ChannelNumberBase247 { get; set; } = 8000;

    /// <summary>Gets or sets the first channel number used for event slot channels.</summary>
    public int SlotChannelNumberBase { get; set; } = 9000;

    /// <summary>Gets or sets the maximum number of slot channels per category.</summary>
    public int MaxSlotsPerCategory { get; set; } = 4;

    /// <summary>Gets or sets a value indicating whether 24/7 channels without EPG get filler programmes.</summary>
    public bool Channel247FillerPrograms { get; set; } = true;

    /// <summary>Gets or sets the minimum interval between two guide refreshes triggered by the plugin, in minutes.</summary>
    public int GuideRefreshMinMinutes { get; set; } = 10;

    /// <summary>Gets or sets a value indicating whether ffmpeg probing is skipped when codecs are known.</summary>
    public bool FastStart { get; set; } = true;

    /// <summary>Gets or sets the analyze duration passed to ffmpeg, in milliseconds.</summary>
    public int AnalyzeDurationMs { get; set; } = 1500;

    /// <summary>Gets or sets the maximum number of concurrent live streams per user.</summary>
    public int MaxStreamsPerUser { get; set; } = 4;

    /// <summary>Gets or sets the relay request budget per user per minute.</summary>
    public int RelayRequestsPerMinute { get; set; } = 1500;

    /// <summary>Gets or sets after how many seconds without playlist progress the relay fails over.</summary>
    public int FailoverStallSeconds { get; set; } = 15;

    /// <summary>Gets or sets after how many consecutive segment errors the relay fails over.</summary>
    public int FailoverSegmentErrors { get; set; } = 3;

    /// <summary>Gets or sets the validity of relay tokens, in hours.</summary>
    public int RelayTokenHours { get; set; } = 12;

    /// <summary>Gets or sets the idle time after which relay sessions are closed, in seconds.</summary>
    public int RelayIdleSeconds { get; set; } = 90;

    /// <summary>Gets or sets a value indicating whether favorite/popular events are resolved ahead of time.</summary>
    public bool PreResolveEnabled { get; set; } = true;

    /// <summary>Gets or sets how many minutes before the start events are pre-resolved.</summary>
    public int PreResolveMinutes { get; set; } = 10;

    /// <summary>Gets or sets how many of the most popular upcoming events are pre-resolved.</summary>
    public int PreResolveTopPopular { get; set; } = 5;

    /// <summary>Gets or sets a value indicating whether the headless browser sidecar may be used.</summary>
    public bool HeadlessResolverEnabled { get; set; }

    /// <summary>Gets or sets the URL of the headless resolver sidecar.</summary>
    public string HeadlessResolverUrl { get; set; } = "http://gluetun:3000";

    /// <summary>Gets or sets the egress profiles.</summary>
    public List<EgressProfile> EgressProfiles { get; set; } = new();

    /// <summary>Gets or sets the default egress profile id.</summary>
    public string DefaultEgressProfileId { get; set; } = EgressProfile.DirectId;

    /// <summary>Gets or sets the region → egress profile assignments.</summary>
    public List<RegionEgressRule> RegionEgress { get; set; } = new();

    /// <summary>Gets or sets the URL used by "check connection" (must return JSON with ip and country).</summary>
    public string IpCheckUrl { get; set; } = "https://ipinfo.io/json";

    /// <summary>Gets or sets a value indicating whether "live now" notifications are sent.</summary>
    public bool EnableNotifications { get; set; } = true;

    /// <summary>Gets or sets the per-user provider visibility rules.</summary>
    public List<UserAccessRule> UserAccess { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether composite images are generated when an API has none.</summary>
    public bool GenerateImages { get; set; } = true;

    /// <summary>
    /// Applies first-run defaults and repairs invariants (the direct profile always exists).
    /// </summary>
    /// <returns><c>true</c> when the configuration changed and should be saved.</returns>
    public bool EnsureDefaults()
    {
        var changed = false;
        if (!DefaultsApplied)
        {
            if (CategoryDurations.Count == 0)
            {
                CategoryDurations.AddRange(DefaultCategoryDurations());
            }

            DefaultsApplied = true;
            changed = true;
        }

        if (!EgressProfiles.Exists(p => p.Id == EgressProfile.DirectId))
        {
            EgressProfiles.Insert(0, EgressProfile.CreateDirect());
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(DefaultEgressProfileId) || !EgressProfiles.Exists(p => p.Id == DefaultEgressProfileId))
        {
            DefaultEgressProfileId = EgressProfile.DirectId;
            changed = true;
        }

        return changed;
    }

    /// <summary>Builds the default category durations.</summary>
    /// <returns>The rules.</returns>
    public static List<CategoryDurationRule> DefaultCategoryDurations() => new()
    {
        new CategoryDurationRule { Match = "(?i)calcio|football|soccer|fútbol|fussball", Minutes = 115 },
        new CategoryDurationRule { Match = "(?i)basket|nba", Minutes = 150 },
        new CategoryDurationRule { Match = "(?i)tennis", Minutes = 180 },
        new CategoryDurationRule { Match = "(?i)f1|formula|motogp|motorsport", Minutes = 120 },
        new CategoryDurationRule { Match = "(?i)rugby|hockey|volley|pallavolo", Minutes = 120 },
        new CategoryDurationRule { Match = "(?i)ciclismo|cycling|golf", Minutes = 300 },
    };
}
