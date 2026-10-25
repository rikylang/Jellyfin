using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.Catalog;

/// <summary>Coarse display bucket of an event (drives the versioned channel item ids).</summary>
public enum DisplayBucket
{
    /// <summary>More than an hour away.</summary>
    Later,

    /// <summary>Starts within the hour.</summary>
    Soon,

    /// <summary>Starts within 15 minutes.</summary>
    Imminent,

    /// <summary>On air.</summary>
    Live,

    /// <summary>Finished.</summary>
    Ended,
}

/// <summary>An immutable view of the merged catalog.</summary>
public sealed class CatalogView
{
    /// <summary>Gets an empty view.</summary>
    public static CatalogView Empty { get; } = new();

    /// <summary>Gets the events (deduplicated, not expired, sorted by start).</summary>
    public IReadOnlyList<LiveEvent> Events { get; init; } = Array.Empty<LiveEvent>();

    /// <summary>Gets the 24/7 channels.</summary>
    public IReadOnlyList<LiveChannel> Channels { get; init; } = Array.Empty<LiveChannel>();

    /// <summary>Gets the programmes by channel key.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LiveProgram>> Programs { get; init; } = new Dictionary<string, IReadOnlyList<LiveProgram>>();

    /// <summary>Gets the category names.</summary>
    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    /// <summary>Gets merged event key → primary key.</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the event index by key.</summary>
    public IReadOnlyDictionary<string, LiveEvent> EventIndex { get; init; } = new Dictionary<string, LiveEvent>();

    /// <summary>Gets the channel index by key.</summary>
    public IReadOnlyDictionary<string, LiveChannel> ChannelIndex { get; init; } = new Dictionary<string, LiveChannel>();

    /// <summary>Gets the data revision (changes when content changes).</summary>
    public string DataRevision { get; init; } = "0";

    /// <summary>Gets the display revision (changes when content or a status bucket changes).</summary>
    public string DisplayRevision { get; init; } = "0";

    /// <summary>Gets when the view was built.</summary>
    public DateTimeOffset BuiltAt { get; init; }
}

/// <summary>What changed in the catalog.</summary>
public sealed class CatalogChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogChangedEventArgs"/> class.
    /// </summary>
    /// <param name="view">The new view.</param>
    /// <param name="dataChanged">Whether the content changed (otherwise only status buckets).</param>
    public CatalogChangedEventArgs(CatalogView view, bool dataChanged)
    {
        View = view;
        DataChanged = dataChanged;
    }

    /// <summary>Gets the new view.</summary>
    public CatalogView View { get; }

    /// <summary>Gets a value indicating whether the content changed.</summary>
    public bool DataChanged { get; }
}

/// <summary>Per-provider data held by the catalog.</summary>
public sealed class ProviderData
{
    /// <summary>Gets or sets the provider id.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets the latest successful result per endpoint.</summary>
    public Dictionary<string, EndpointResult> Endpoints { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets a snapshot loaded from disk (until the first successful sync).</summary>
    public ProviderSnapshot? Snapshot { get; set; }

    /// <summary>Gets or sets when the data was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
