using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LiveHub.Model;

/// <summary>Kind of stream URL.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StreamKind>))]
public enum StreamKind
{
    /// <summary>Unknown: detected by the resolver.</summary>
    Auto = 0,

    /// <summary>HLS playlist (.m3u8).</summary>
    Hls = 1,

    /// <summary>DASH manifest (.mpd).</summary>
    Dash = 2,

    /// <summary>Progressive MP4.</summary>
    Mp4 = 3,

    /// <summary>MPEG transport stream.</summary>
    Ts = 4,

    /// <summary>Web page / iframe to embed (needs resolving).</summary>
    Embed = 5,
}

/// <summary>Effective status of an event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EventStatus>))]
public enum EventStatus
{
    /// <summary>Not started yet.</summary>
    Upcoming = 0,

    /// <summary>On air.</summary>
    Live = 1,

    /// <summary>Finished.</summary>
    Ended = 2,
}

/// <summary>A category (sport, genre...) exposed by a provider.</summary>
public sealed class LiveCategory
{
    /// <summary>Gets or sets the provider id.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the external id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the image URL.</summary>
    public string? ImageUrl { get; set; }
}

/// <summary>A participant (team, player).</summary>
public sealed class Participant
{
    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the logo URL.</summary>
    public string? LogoUrl { get; set; }
}

/// <summary>A playable (or resolvable) stream of an event or channel.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Domain model name required by the specification (LiveStream).")]
public sealed class LiveStream
{
    /// <summary>Gets or sets the stable stream key (unique in the catalog).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider id.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the origin URL (may be an embed page).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind.</summary>
    public StreamKind Kind { get; set; }

    /// <summary>Gets or sets the quality label (1080p, HD...).</summary>
    public string? QualityLabel { get; set; }

    /// <summary>Gets or sets the video height in pixels.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the bitrate in bits per second.</summary>
    public long? Bitrate { get; set; }

    /// <summary>Gets or sets the language (ISO 639-1).</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the country (ISO 3166-1 alpha-2).</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the HTTP headers required by the origin.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets when the URL expires.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Gets or sets the source label (provider/feed name).</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the provider priority (copied for ordering after merges).</summary>
    public int Priority { get; set; }

    /// <summary>Gets a value indicating whether the stream is HD (height ≥ 720).</summary>
    [JsonIgnore]
    public bool IsHd => Height is >= 720;
}

/// <summary>A live event (match, race...) with a start time.</summary>
public sealed class LiveEvent
{
    /// <summary>Gets or sets the catalog key ("e" + hash).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider id (primary, after deduplication).</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the id in the provider API.</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the category.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the category id in the provider API.</summary>
    public string? CategoryId { get; set; }

    /// <summary>Gets or sets the competition.</summary>
    public string? Competition { get; set; }

    /// <summary>Gets or sets the start time.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>Gets or sets the end time, when known.</summary>
    public DateTimeOffset? End { get; set; }

    /// <summary>Gets or sets the duration in minutes, when known.</summary>
    public int? DurationMinutes { get; set; }

    /// <summary>Gets or sets the live flag declared by the API.</summary>
    public bool? IsLiveFlag { get; set; }

    /// <summary>Gets or sets the status declared by the API.</summary>
    public EventStatus? StatusFlag { get; set; }

    /// <summary>Gets or sets the popularity score.</summary>
    public double? Popularity { get; set; }

    /// <summary>Gets or sets the vertical poster URL.</summary>
    public string? PosterUrl { get; set; }

    /// <summary>Gets or sets the 16:9 thumbnail URL.</summary>
    public string? ThumbUrl { get; set; }

    /// <summary>Gets or sets the logo URL.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Gets or sets the participants.</summary>
    public List<Participant> Participants { get; set; } = new();

    /// <summary>Gets or sets the country.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the language.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the event page URL.</summary>
    public string? PageUrl { get; set; }

    /// <summary>Gets or sets free tags.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Gets or sets the official rating (parental control).</summary>
    public string? ContentRating { get; set; }

    /// <summary>Gets or sets the streams.</summary>
    public List<LiveStream> Streams { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether more streams come from a lazy stream chain.</summary>
    public bool HasLazyStreams { get; set; }

    /// <summary>Gets or sets the provider priority.</summary>
    public int Priority { get; set; }

    /// <summary>Gets or sets the providers merged into this event.</summary>
    public List<string> SourceProviders { get; set; } = new();

    /// <summary>Gets or sets (provider, external id) pairs of merged events, used by lazy stream chains.</summary>
    public List<string> MergedRefs { get; set; } = new();

    /// <summary>Gets or sets the mapped raw fields, available to templates as {event.x}.</summary>
    public Dictionary<string, string> Context { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Copies the event and its collections (streams are shared, they are never mutated after mapping).</summary>
    /// <returns>The copy.</returns>
    public LiveEvent ShallowClone()
    {
        var copy = (LiveEvent)MemberwiseClone();
        copy.Streams = new List<LiveStream>(Streams);
        copy.Participants = Participants.ConvertAll(p => new Participant { Name = p.Name, LogoUrl = p.LogoUrl });
        copy.Tags = new List<string>(Tags);
        copy.SourceProviders = new List<string>(SourceProviders);
        copy.MergedRefs = new List<string>(MergedRefs);
        copy.Context = new Dictionary<string, string>(Context, StringComparer.Ordinal);
        return copy;
    }
}

/// <summary>A continuous 24/7 channel.</summary>
public sealed class LiveChannel
{
    /// <summary>Gets or sets the catalog key ("c" + hash).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider id.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the id in the provider.</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel number declared by the source.</summary>
    public string? Number { get; set; }

    /// <summary>Gets or sets the category / group.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the logo URL.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Gets or sets the country.</summary>
    public string? Country { get; set; }

    /// <summary>Gets or sets the language.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the EPG id (tvg-id / epg_channel_id).</summary>
    public string? EpgId { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the official rating.</summary>
    public string? ContentRating { get; set; }

    /// <summary>Gets or sets free tags.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Gets or sets the provider priority.</summary>
    public int Priority { get; set; }

    /// <summary>Gets or sets the streams.</summary>
    public List<LiveStream> Streams { get; set; } = new();
}

/// <summary>An EPG programme of a 24/7 channel.</summary>
public sealed class LiveProgram
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the channel key.</summary>
    public string ChannelKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the subtitle / episode title.</summary>
    public string? SubTitle { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the start.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>Gets or sets the end.</summary>
    public DateTimeOffset End { get; set; }

    /// <summary>Gets or sets the category.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the image URL.</summary>
    public string? ImageUrl { get; set; }
}

/// <summary>Everything a provider returned in one run.</summary>
public sealed class ProviderSnapshot
{
    /// <summary>Gets or sets the provider id.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets when the data was fetched.</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>Gets or sets the categories.</summary>
    public List<LiveCategory> Categories { get; set; } = new();

    /// <summary>Gets or sets the events.</summary>
    public List<LiveEvent> Events { get; set; } = new();

    /// <summary>Gets or sets the channels.</summary>
    public List<LiveChannel> Channels { get; set; } = new();

    /// <summary>Gets or sets the EPG programmes.</summary>
    public List<LiveProgram> Programs { get; set; } = new();

    /// <summary>Gets or sets non fatal warnings.</summary>
    public List<string> Warnings { get; set; } = new();
}
