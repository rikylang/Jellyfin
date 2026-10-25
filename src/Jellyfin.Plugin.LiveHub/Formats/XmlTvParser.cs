using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace Jellyfin.Plugin.LiveHub.Formats;

/// <summary>
/// Streaming XMLTV parser (DTD and external entities disabled). Only programmes inside the requested window and for the
/// requested channels are kept, so very large guides use little memory.
/// </summary>
public static class XmlTvParser
{
    /// <summary>Parses a guide.</summary>
    /// <param name="xml">The XML text.</param>
    /// <param name="from">Window start.</param>
    /// <param name="to">Window end.</param>
    /// <param name="channelFilter">Channel ids to keep (null = all).</param>
    /// <param name="maxProgrammes">Safety limit.</param>
    /// <returns>The guide.</returns>
    public static XmlTvGuide Parse(string xml, DateTimeOffset from, DateTimeOffset to, ISet<string>? channelFilter = null, int maxProgrammes = 200_000)
    {
        ArgumentNullException.ThrowIfNull(xml);
        var guide = new XmlTvGuide();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            CheckCharacters = false,
            MaxCharactersFromEntities = 1024,
        };

        using var sr = new StringReader(xml);
        using var reader = XmlReader.Create(sr, settings);
        try
        {
            ParseInto(reader, guide, from, to, channelFilter, maxProgrammes);
        }
        catch (XmlException ex)
        {
            // Malformed guide (or an entity reference, never expanded): keep what was read so far.
            guide.Error = $"Malformed XMLTV at line {ex.LineNumber}: {ex.Message}";
        }

        return guide;
    }

    private static void ParseInto(XmlReader reader, XmlTvGuide guide, DateTimeOffset from, DateTimeOffset to, ISet<string>? channelFilter, int maxProgrammes)
    {
        reader.MoveToContent();
        while (!reader.EOF && reader.ReadState == ReadState.Interactive)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "channel")
            {
                var ch = ReadChannel(reader);
                if (ch is not null && (channelFilter is null || channelFilter.Contains(ch.Id)))
                {
                    guide.Channels.Add(ch);
                }

                reader.Read();
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.Name == "programme")
            {
                var channel = reader.GetAttribute("channel") ?? string.Empty;
                var start = ParseDate(reader.GetAttribute("start"));
                var stop = ParseDate(reader.GetAttribute("stop"));
                var end = stop ?? start?.AddHours(1);
                if (start is null || (channelFilter is not null && !channelFilter.Contains(channel)) || end < from || start > to || guide.Programmes.Count >= maxProgrammes)
                {
                    reader.Skip();
                    continue;
                }

                guide.Programmes.Add(ReadProgramme(reader, channel, start.Value, end!.Value));
                reader.Read();
                continue;
            }

            reader.Read();
        }
    }

    /// <summary>Parses an XMLTV date ("20261025180000 +0200", "202610251800", with or without offset).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The instant.</returns>
    public static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        var space = v.IndexOf(' ', StringComparison.Ordinal);
        var datePart = space > 0 ? v[..space] : v;
        var offsetPart = space > 0 ? v[(space + 1)..].Trim() : null;
        if (datePart.Length > 14)
        {
            // "20261025180000+0200"
            var sign = datePart.IndexOfAny(new[] { '+', '-' }, 8);
            if (sign > 0)
            {
                offsetPart = datePart[sign..];
                datePart = datePart[..sign];
            }
        }

        datePart = datePart.Length switch
        {
            >= 14 => datePart[..14],
            12 => datePart + "00",
            8 => datePart + "000000",
            _ => datePart,
        };

        if (!DateTime.TryParseExact(datePart, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return null;
        }

        var offset = TimeSpan.Zero;
        if (!string.IsNullOrEmpty(offsetPart) && offsetPart.Length >= 5 && (offsetPart[0] == '+' || offsetPart[0] == '-')
            && int.TryParse(offsetPart.AsSpan(1, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh)
            && int.TryParse(offsetPart.AsSpan(offsetPart.Length - 2, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm))
        {
            offset = new TimeSpan(hh, mm, 0);
            if (offsetPart[0] == '-')
            {
                offset = -offset;
            }
        }

        return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), offset).ToUniversalTime();
    }

    private static XmlTvChannel? ReadChannel(XmlReader reader)
    {
        var ch = new XmlTvChannel { Id = reader.GetAttribute("id") ?? string.Empty };
        if (reader.IsEmptyElement)
        {
            return ch.Id.Length > 0 ? ch : null;
        }

        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && reader.ReadState == ReadState.Interactive && reader.Depth > depth)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.Name)
            {
                case "display-name":
                    var name = ReadText(reader);
                    if (name.Length > 0)
                    {
                        ch.DisplayNames.Add(name);
                    }

                    break;
                case "icon":
                    ch.Icon ??= reader.GetAttribute("src");
                    reader.Skip();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return ch.Id.Length > 0 ? ch : null;
    }

    /// <summary>Reads the text content of the current element (mixed content allowed) and moves past its end tag.</summary>
    private static string ReadText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return string.Empty;
        }

        var depth = reader.Depth;
        var sb = new System.Text.StringBuilder();
        reader.Read();
        while (reader.ReadState == ReadState.Interactive && reader.Depth > depth)
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                sb.Append(reader.Value);
            }

            reader.Read();
        }

        if (reader.ReadState == ReadState.Interactive && reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
        {
            reader.Read();
        }

        return sb.ToString().Trim();
    }

    private static XmlTvProgramme ReadProgramme(XmlReader reader, string channel, DateTimeOffset start, DateTimeOffset stop)
    {
        var p = new XmlTvProgramme { Channel = channel, Start = start, Stop = stop };
        if (reader.IsEmptyElement)
        {
            return p;
        }

        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && reader.ReadState == ReadState.Interactive && reader.Depth > depth)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.Name)
            {
                case "title":
                    var title = ReadText(reader);
                    if (p.Title.Length == 0)
                    {
                        p.Title = title;
                    }

                    break;
                case "sub-title":
                    var sub = ReadText(reader);
                    p.SubTitle ??= sub;
                    break;
                case "desc":
                    var desc = ReadText(reader);
                    p.Description ??= desc;
                    break;
                case "category":
                    var cat = ReadText(reader);
                    if (cat.Length > 0)
                    {
                        p.Categories.Add(cat);
                    }

                    break;
                case "icon":
                    p.Icon ??= reader.GetAttribute("src");
                    reader.Skip();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return p;
    }
}
