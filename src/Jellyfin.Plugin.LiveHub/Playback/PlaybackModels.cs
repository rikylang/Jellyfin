using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveHub.Definitions;

namespace Jellyfin.Plugin.LiveHub.Playback;

/// <summary>Request of "Test playback".</summary>
public sealed class PlaybackTestRequest
{
    /// <summary>Gets or sets the definition being edited (masked secrets are restored from the store).</summary>
    public ProviderDefinition? Definition { get; set; }

    /// <summary>Gets or sets a saved provider id (used when no definition is given).</summary>
    public string? ProviderId { get; set; }

    /// <summary>Gets or sets the event/channel key (preview row).</summary>
    public string? ItemKey { get; set; }

    /// <summary>Gets or sets the stream id.</summary>
    public string? StreamId { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is a 24/7 channel.</summary>
    public bool IsChannel { get; set; }

    /// <summary>Gets or sets a URL to test directly (no definition needed).</summary>
    public string? Url { get; set; }
}

/// <summary>Result of "Test playback".</summary>
public sealed class PlaybackTestResult
{
    /// <summary>Gets or sets a value indicating whether the stream is playable.</summary>
    public bool Playable { get; set; }

    /// <summary>Gets or sets the summary (localized diagnosis).</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets the format.</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets the strategy that resolved the URL.</summary>
    public string? Strategy { get; set; }

    /// <summary>Gets or sets a value indicating whether the URL was extracted from a page.</summary>
    public bool FromEmbed { get; set; }

    /// <summary>Gets the codecs.</summary>
    public List<string> Codecs { get; } = new();

    /// <summary>Gets the variants.</summary>
    public List<string> Variants { get; } = new();

    /// <summary>Gets the required headers (sensitive values masked).</summary>
    public Dictionary<string, string> RequiredHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the time to the first segment.</summary>
    public double LatencyMs { get; set; }

    /// <summary>Gets or sets the resolved URL (redacted).</summary>
    public string? ResolvedUrl { get; set; }

    /// <summary>Gets or sets the egress profile used.</summary>
    public string? Egress { get; set; }

    /// <summary>Gets the steps.</summary>
    public List<string> Steps { get; } = new();
}
