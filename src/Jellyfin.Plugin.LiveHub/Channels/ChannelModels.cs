using System;

namespace Jellyfin.Plugin.LiveHub.Channels;

/// <summary>How the time prefix of an item name is written.</summary>
public enum NameStyle
{
    /// <summary>"21:00 · Title" (items of a single day).</summary>
    TimeOnly,

    /// <summary>"oggi 21:00 · Title" / "dom 26/10 21:00 · Title".</summary>
    DayAndTime,
}

/// <summary>Kind of channel item.</summary>
public enum ItemKind
{
    /// <summary>A folder.</summary>
    Folder,

    /// <summary>A live event.</summary>
    Event,

    /// <summary>A 24/7 channel.</summary>
    Channel,
}

/// <summary>
/// Channel item ids. Folders: "f|live", "f|cat|1a2b3c4d"; media: "e|{folderTag}|{eventKey}|{version}" and
/// "c|{folderTag}|{channelKey}|{version}". The folder tag keeps one item per folder (an item has a single parent);
/// the version changes when the displayed text changes (Jellyfin never updates the name of existing items).
/// </summary>
public static class ChannelIds
{
    /// <summary>Separator.</summary>
    public const char Sep = '|';

    /// <summary>Builds a folder id.</summary>
    /// <param name="parts">Parts after "f".</param>
    /// <returns>The id.</returns>
    public static string Folder(params string[] parts) => "f" + Sep + string.Join(Sep, parts);

    /// <summary>Builds an event item id.</summary>
    /// <param name="tag">Folder tag.</param>
    /// <param name="key">Event key.</param>
    /// <param name="version">Display version.</param>
    /// <returns>The id.</returns>
    public static string Event(string tag, string key, string version) => string.Join(Sep, "e", tag, key, version);

    /// <summary>Builds a channel item id.</summary>
    /// <param name="tag">Folder tag.</param>
    /// <param name="key">Channel key.</param>
    /// <param name="version">Display version.</param>
    /// <returns>The id.</returns>
    public static string Channel(string tag, string key, string version) => string.Join(Sep, "c", tag, key, version);

    /// <summary>Parses a media item id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="key">The event or channel key.</param>
    /// <returns>Whether the id is a Live Hub media item.</returns>
    public static bool TryParseMedia(string? id, out ItemKind kind, out string key)
    {
        kind = ItemKind.Folder;
        key = string.Empty;
        if (string.IsNullOrEmpty(id))
        {
            return false;
        }

        var parts = id.Split(Sep);
        if (parts.Length != 4 || (parts[0] != "e" && parts[0] != "c"))
        {
            return false;
        }

        kind = parts[0] == "e" ? ItemKind.Event : ItemKind.Channel;
        key = parts[2];
        return key.Length > 1;
    }

    /// <summary>Short stable hash used in folder ids (category names...).</summary>
    /// <param name="value">The value.</param>
    /// <returns>8 hex chars.</returns>
    public static string Short(string value) => Engine.Keys.Hash((value ?? string.Empty).Trim().ToUpperInvariant())[..8];

    /// <summary>Returns whether two folder parts are equal.</summary>
    /// <param name="a">First.</param>
    /// <param name="b">Second.</param>
    /// <returns>True when equal.</returns>
    public static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.Ordinal);
}
