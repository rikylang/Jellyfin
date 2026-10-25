using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.LiveHub.Localization;

/// <summary>
/// Loads the plugin strings from the embedded resource files (strings.it.json, strings.en.json).
/// Used by the server side (folder names, statuses) and served to the web pages.
/// </summary>
public sealed class LocalizationService
{
    /// <summary>Supported UI languages.</summary>
    public static readonly IReadOnlyList<string> Supported = new[] { "it", "en" };

    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Normalizes a requested language to a supported one.</summary>
    /// <param name="language">Requested language (it, it-IT, en-US...).</param>
    /// <returns>A supported language code.</returns>
    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "en";
        }

        var two = language.Trim().ToLowerInvariant();
        if (two.Length > 2)
        {
            two = two[..2];
        }

        return Supported.Contains(two) ? two : "en";
    }

    /// <summary>Gets all strings for a language, falling back to English for missing keys.</summary>
    /// <param name="language">The language.</param>
    /// <returns>The strings.</returns>
    public IReadOnlyDictionary<string, string> GetStrings(string? language)
    {
        var lang = Normalize(language);
        return _cache.GetOrAdd(lang, static l =>
        {
            var english = Load("en");
            if (l == "en")
            {
                return english;
            }

            var merged = new Dictionary<string, string>(english, StringComparer.Ordinal);
            foreach (var kv in Load(l))
            {
                merged[kv.Key] = kv.Value;
            }

            return merged;
        });
    }

    /// <summary>Gets a string.</summary>
    /// <param name="language">The language.</param>
    /// <param name="key">The key.</param>
    /// <returns>The localized text, or the key when missing.</returns>
    public string Get(string? language, string key)
        => GetStrings(language).TryGetValue(key, out var v) ? v : key;

    /// <summary>Gets and formats a string.</summary>
    /// <param name="language">The language.</param>
    /// <param name="key">The key.</param>
    /// <param name="args">Format arguments ({0}, {1}...).</param>
    /// <returns>The formatted text.</returns>
    public string Format(string? language, string key, params object[] args)
        => string.Format(CultureInfo.GetCultureInfo(Normalize(language)), Get(language, key), args);

    private static Dictionary<string, string> Load(string lang)
    {
        var asm = typeof(LocalizationService).Assembly;
        var name = $"Jellyfin.Plugin.LiveHub.Localization.strings.{lang}.json";
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd())
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
