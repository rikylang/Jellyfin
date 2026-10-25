using System;
using System.Collections.Generic;
using Jellyfin.Plugin.LiveHub.Model;

namespace Jellyfin.Plugin.LiveHub.LiveTv;

/// <summary>Kind of Live TV channel.</summary>
public enum LineupKind
{
    /// <summary>A real 24/7 channel of a provider.</summary>
    Channel247,

    /// <summary>A stable per-category channel showing events ("Calcio 1").</summary>
    Slot,
}

/// <summary>A Live TV channel exposed by the tuner host.</summary>
public sealed class LineupChannel
{
    /// <summary>Gets or sets the tuner channel id (stable).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the number.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind.</summary>
    public LineupKind Kind { get; set; }

    /// <summary>Gets or sets the category (slot channels) or group.</summary>
    public string? Category { get; set; }

    /// <summary>Gets or sets the catalog key of the 24/7 channel.</summary>
    public string? ChannelKey { get; set; }

    /// <summary>Gets or sets the logo URL.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Gets or sets the tags (parental control).</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the channel is HD.</summary>
    public bool? IsHd { get; set; }
}

/// <summary>An event placed on a slot channel.</summary>
/// <param name="ChannelId">The slot channel id.</param>
/// <param name="Event">The event.</param>
/// <param name="Start">Start.</param>
/// <param name="End">Estimated end.</param>
public sealed record SlotAssignment(string ChannelId, LiveEvent Event, DateTimeOffset Start, DateTimeOffset End);

/// <summary>Persisted state of the slot allocator and channel numbering.</summary>
public sealed class LineupState
{
    /// <summary>Gets or sets the categories (name → state).</summary>
    public Dictionary<string, CategorySlots> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the event → slot assignment (event key → slot index).</summary>
    public Dictionary<string, int> EventSlots { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the numbers given to 24/7 channels (channel key → number).</summary>
    public Dictionary<string, int> ChannelNumbers { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Slots of one category.</summary>
public sealed class CategorySlots
{
    /// <summary>Gets or sets the display order (fixed at creation: numbers stay stable).</summary>
    public int Order { get; set; }

    /// <summary>Gets or sets the number of slot channels (only grows, up to the configured maximum).</summary>
    public int Slots { get; set; }

    /// <summary>Gets or sets when an event of the category was last seen.</summary>
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>The computed lineup.</summary>
public sealed class Lineup
{
    /// <summary>Gets the channels.</summary>
    public List<LineupChannel> Channels { get; } = new();

    /// <summary>Gets the slot assignments by channel id.</summary>
    public Dictionary<string, List<SlotAssignment>> Assignments { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the fingerprint of the channel set (ids, names, numbers).</summary>
    public string ChannelSetVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the fingerprint of the programs.</summary>
    public string ProgramsVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets the catalog revision the lineup was built from.</summary>
    public string Revision { get; set; } = string.Empty;
}
