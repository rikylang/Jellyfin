using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveHub.Formats;

/// <summary>A parsed M3U playlist.</summary>
public sealed class M3uPlaylist
{
    /// <summary>Gets the #EXTM3U header attributes (url-tvg, x-tvg-url...).</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the entries.</summary>
    public List<M3uEntry> Entries { get; } = new();

    /// <summary>Gets the guide URL declared in the header, if any.</summary>
    public string? GuideUrl => Attributes.TryGetValue("url-tvg", out var u) ? u : Attributes.TryGetValue("x-tvg-url", out var x) ? x : null;
}

/// <summary>An M3U entry.</summary>
public sealed class M3uEntry
{
    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the #EXTINF duration.</summary>
    public double Duration { get; set; } = -1;

    /// <summary>Gets the attributes (lower-case keys: tvg-id, tvg-name, tvg-logo, group-title, tvg-language, tvg-country, tvg-chno).</summary>
    public Dictionary<string, string> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the HTTP headers (#EXTVLCOPT, #KODIPROP, url|headers).</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets an attribute or null.</summary>
    /// <param name="name">The attribute.</param>
    /// <returns>The value.</returns>
    public string? Attr(string name) => Attributes.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}

/// <summary>A parsed XMLTV guide.</summary>
public sealed class XmlTvGuide
{
    /// <summary>Gets the channels.</summary>
    public List<XmlTvChannel> Channels { get; } = new();

    /// <summary>Gets the programmes.</summary>
    public List<XmlTvProgramme> Programmes { get; } = new();

    /// <summary>Gets or sets the parse error when the document is malformed (partial results are kept).</summary>
    public string? Error { get; set; }
}

/// <summary>An XMLTV channel.</summary>
public sealed class XmlTvChannel
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets the display names.</summary>
    public List<string> DisplayNames { get; } = new();

    /// <summary>Gets or sets the icon.</summary>
    public string? Icon { get; set; }
}

/// <summary>An XMLTV programme.</summary>
public sealed class XmlTvProgramme
{
    /// <summary>Gets or sets the channel id.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Gets or sets the start.</summary>
    public DateTimeOffset Start { get; set; }

    /// <summary>Gets or sets the stop.</summary>
    public DateTimeOffset Stop { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the sub-title.</summary>
    public string? SubTitle { get; set; }

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets the categories.</summary>
    public List<string> Categories { get; } = new();

    /// <summary>Gets or sets the icon.</summary>
    public string? Icon { get; set; }
}
