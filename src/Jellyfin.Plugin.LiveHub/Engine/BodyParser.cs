using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.LiveHub.Definitions;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Detects and parses response bodies (JSON, JSONP, M3U, XMLTV, gzip).
/// </summary>
public static class BodyParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    /// <summary>Detects the format of a body.</summary>
    /// <param name="text">The body text.</param>
    /// <param name="contentType">The content type.</param>
    /// <returns>The format (Auto when unknown).</returns>
    public static BodyFormat Detect(string text, string? contentType)
    {
        var t = (text ?? string.Empty).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (t.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) || t.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
        {
            return BodyFormat.M3u;
        }

        if (t.StartsWith('<'))
        {
            if (t.Contains("<tv", StringComparison.OrdinalIgnoreCase) && (t.Contains("<programme", StringComparison.OrdinalIgnoreCase) || t.Contains("<channel", StringComparison.OrdinalIgnoreCase)))
            {
                return BodyFormat.Xmltv;
            }

            return BodyFormat.Auto;
        }

        if (t.StartsWith('{') || t.StartsWith('[') || (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return BodyFormat.Json;
        }

        return BodyFormat.Auto;
    }

    /// <summary>Parses a JSON body (also unwraps JSONP "callback({...})").</summary>
    /// <param name="text">The text.</param>
    /// <returns>The root node.</returns>
    /// <exception cref="ProviderRequestException">The body is not JSON.</exception>
    public static JsonNode? ParseJson(string text)
    {
        var t = (text ?? string.Empty).Trim().TrimStart('﻿');
        if (t.Length == 0)
        {
            return null;
        }

        if (!(t.StartsWith('{') || t.StartsWith('[')))
        {
            var open = t.IndexOf('(', StringComparison.Ordinal);
            var close = t.LastIndexOf(')');
            if (open > 0 && close > open && IsIdentifier(t.AsSpan(0, open).Trim()))
            {
                t = t.Substring(open + 1, close - open - 1);
            }
            else
            {
                var preview = t.Length > 80 ? t[..80] + "…" : t;
                throw new ProviderRequestException("The response is not JSON: " + preview);
            }
        }

        try
        {
            return JsonNode.Parse(t, null, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new ProviderRequestException("Invalid JSON: " + ex.Message, ex);
        }
    }

    /// <summary>Decompresses gzip bodies (by magic number), respecting a size limit.</summary>
    /// <param name="body">The bytes.</param>
    /// <param name="maxBytes">The limit for the decompressed data.</param>
    /// <returns>The decompressed (or original) bytes.</returns>
    public static byte[] MaybeGunzip(byte[] body, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length < 2 || body[0] != 0x1F || body[1] != 0x8B)
        {
            return body;
        }

        using var input = new MemoryStream(body);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > maxBytes)
            {
                throw new Http.ResponseTooLargeException($"Decompressed body exceeds {maxBytes} bytes.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>Decodes bytes to text (UTF-8, BOM aware).</summary>
    /// <param name="body">The bytes.</param>
    /// <returns>The text.</returns>
    public static string Decode(byte[] body) => Encoding.UTF8.GetString(body ?? Array.Empty<byte>()).TrimStart('﻿');

    private static bool IsIdentifier(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '$' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
