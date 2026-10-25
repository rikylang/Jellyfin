using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LiveHub.Users;

/// <summary>Quality preference.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualityPreference>))]
public enum QualityPreference
{
    /// <summary>Best available.</summary>
    Auto,

    /// <summary>Only HD/FHD sources (720p and above) when available.</summary>
    HdOnly,

    /// <summary>Prefer SD / data saving.</summary>
    PreferSd,

    /// <summary>Best source under a maximum bitrate.</summary>
    MaxBitrate,
}

/// <summary>Sort order of the channel folders.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SortPreference>))]
public enum SortPreference
{
    /// <summary>By start time.</summary>
    Time,

    /// <summary>By popularity.</summary>
    Popularity,

    /// <summary>By name.</summary>
    Name,
}

/// <summary>Notification preferences.</summary>
public sealed class NotificationPreferences
{
    /// <summary>Gets or sets a value indicating whether notifications are enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets how many minutes before the start a notification is sent (0 = when it goes live).</summary>
    public int LeadMinutes { get; set; }

    /// <summary>Gets or sets a value indicating whether favourite categories notify.</summary>
    public bool Categories { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether favourite teams notify.</summary>
    public bool Teams { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether favourite channels notify.</summary>
    public bool Channels { get; set; } = true;
}

/// <summary>Personal preferences of a Jellyfin user (valid on every device).</summary>
public sealed class UserPreferences
{
    /// <summary>Gets or sets the preferred language (ISO 639-1).</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the fallback languages.</summary>
    public List<string> FallbackLanguages { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether only events with at least one stream in my languages are shown.</summary>
    public bool OnlyMyLanguage { get; set; }

    /// <summary>Gets or sets the quality preference.</summary>
    public QualityPreference Quality { get; set; }

    /// <summary>Gets or sets the maximum bitrate in kbps (MaxBitrate mode).</summary>
    public int? MaxBitrateKbps { get; set; }

    /// <summary>Gets or sets the time zone for times shown in the channel (IANA).</summary>
    public string? TimeZone { get; set; }

    /// <summary>Gets or sets the countries to show (empty = all).</summary>
    public List<string> Countries { get; set; } = new();

    /// <summary>Gets or sets the providers to show (empty = all allowed).</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>Gets or sets the favourite categories.</summary>
    public List<string> FavoriteCategories { get; set; } = new();

    /// <summary>Gets or sets the favourite teams/participants.</summary>
    public List<string> FavoriteTeams { get; set; } = new();

    /// <summary>Gets or sets the favourite 24/7 channel keys.</summary>
    public List<string> FavoriteChannels { get; set; } = new();

    /// <summary>Gets or sets the hidden items: event/channel keys, "cat:Name", "src:providerId".</summary>
    public List<string> Hidden { get; set; } = new();

    /// <summary>Gets or sets the sort order.</summary>
    public SortPreference Sort { get; set; }

    /// <summary>Gets or sets the notification preferences.</summary>
    public NotificationPreferences Notifications { get; set; } = new();

    /// <summary>Gets or sets the version, incremented on every save (channel cache key).</summary>
    public int Version { get; set; }
}
