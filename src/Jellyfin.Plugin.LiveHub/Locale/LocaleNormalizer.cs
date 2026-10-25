using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LiveHub.Locale;

/// <summary>
/// Normalizes languages to ISO 639-1 and countries to ISO 3166-1 alpha-2, and guesses them from titles and M3U attributes.
/// </summary>
public static class LocaleNormalizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Dictionary<string, string> LanguageAliases = BuildLanguageAliases();

    private static readonly Dictionary<string, string> CountryAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uk"] = "GB", ["england"] = "GB", ["great britain"] = "GB", ["united kingdom"] = "GB", ["usa"] = "US", ["united states"] = "US",
        ["america"] = "US", ["italia"] = "IT", ["italy"] = "IT", ["espana"] = "ES", ["españa"] = "ES", ["spain"] = "ES",
        ["deutschland"] = "DE", ["germany"] = "DE", ["france"] = "FR", ["francia"] = "FR", ["portugal"] = "PT", ["brasil"] = "BR",
        ["brazil"] = "BR", ["argentina"] = "AR", ["mexico"] = "MX", ["méxico"] = "MX", ["nederland"] = "NL", ["netherlands"] = "NL",
        ["holland"] = "NL", ["belgique"] = "BE", ["belgium"] = "BE", ["schweiz"] = "CH", ["suisse"] = "CH", ["svizzera"] = "CH",
        ["switzerland"] = "CH", ["österreich"] = "AT", ["austria"] = "AT", ["polska"] = "PL", ["poland"] = "PL", ["turkey"] = "TR",
        ["türkiye"] = "TR", ["russia"] = "RU", ["canada"] = "CA", ["australia"] = "AU", ["ireland"] = "IE", ["latam"] = "MX",
        ["arabia"] = "SA", ["ksa"] = "SA", ["uae"] = "AE", ["greece"] = "GR", ["romania"] = "RO", ["croatia"] = "HR", ["serbia"] = "RS",
        ["albania"] = "AL", ["sweden"] = "SE", ["norway"] = "NO", ["denmark"] = "DK", ["finland"] = "FI", ["japan"] = "JP", ["india"] = "IN",
    };

    private static readonly HashSet<string> IsoCountries = new(
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(c =>
            {
                try
                {
                    return new RegionInfo(c.Name).TwoLetterISORegionName;
                }
                catch (ArgumentException)
                {
                    return null;
                }
            })
            .Where(s => s is { Length: 2 })
            .Select(s => s!)

            // Fallback when the host runs with invariant globalization.
            .Concat("IT GB US ES DE FR PT BR AR MX NL BE CH AT PL TR RU CA AU IE SA AE GR RO HR RS AL SE NO DK FI JP IN CN KR SM VA MT SI CZ SK HU BG UA".Split(' ')),
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> AmbiguousTags = new(StringComparer.OrdinalIgnoreCase) { "vs", "fc", "ac", "sc", "cf", "hd", "sd", "tv", "the", "and", "de", "la", "le", "el", "il", "di", "da", "do", "us", "am", "pm", "to", "on", "in", "at", "an" };

    private static readonly Regex TagRegex = new(
        @"(?:^|[\s\[\(\|/\-:])(?<tag>[A-Za-z]{2,3})(?:[\s\]\)\|/\-:]|$)",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>Normalizes a language (ITA, Italian, italiano, it-IT, ita → it). Unknown → null.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>ISO 639-1 code or null.</returns>
    public static string? NormalizeLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = RemoveDiacritics(value.Trim()).ToLowerInvariant();
        if (LanguageAliases.TryGetValue(v, out var code))
        {
            return code;
        }

        var cut = v.Split(new[] { '-', '_', ' ', '/', ',', ';', '.' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (cut is not null && LanguageAliases.TryGetValue(cut, out code))
        {
            return code;
        }

        return null;
    }

    /// <summary>Normalizes a country (UK → GB, Italia → IT, it-IT → IT). Unknown → null.</summary>
    /// <param name="value">The raw value.</param>
    /// <returns>ISO 3166-1 alpha-2 or null.</returns>
    public static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        if (CountryAliases.TryGetValue(v, out var c))
        {
            return c;
        }

        if (v.Length > 2 && (v[2] == '-' || v[2] == '_') && v.Length >= 5)
        {
            var region = v[3..].Trim();
            if (region.Length == 2 && IsoCountries.Contains(region))
            {
                return region.ToUpperInvariant();
            }
        }

        if (v.Length == 2 && IsoCountries.Contains(v))
        {
            return v.ToUpperInvariant();
        }

        if (v.Length == 3)
        {
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
            {
                try
                {
                    var r = new RegionInfo(culture.Name);
                    if (string.Equals(r.ThreeLetterISORegionName, v, StringComparison.OrdinalIgnoreCase))
                    {
                        return r.TwoLetterISORegionName;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var r = new RegionInfo(culture.Name);
                if (string.Equals(r.EnglishName, v, StringComparison.OrdinalIgnoreCase) || string.Equals(r.NativeName, v, StringComparison.OrdinalIgnoreCase))
                {
                    return r.TwoLetterISORegionName;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    /// <summary>Guesses a language from tags in a text ("[ENG]", "(ES)", "Sky Sport IT", "| ITA |").</summary>
    /// <param name="text">The text.</param>
    /// <returns>The language or null.</returns>
    public static string? GuessLanguage(string? text)
    {
        foreach (var tag in Tags(text))
        {
            var lang = NormalizeLanguage(tag);
            if (lang is not null && !AmbiguousTags.Contains(tag))
            {
                return lang;
            }
        }

        return null;
    }

    /// <summary>Guesses a country from tags in a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The country or null.</returns>
    public static string? GuessCountry(string? text)
    {
        foreach (var tag in Tags(text))
        {
            if (tag.Length == 2 && IsUpper(tag) && IsoCountries.Contains(tag))
            {
                return string.Equals(tag, "UK", StringComparison.Ordinal) ? "GB" : tag;
            }

            if (string.Equals(tag, "UK", StringComparison.Ordinal))
            {
                return "GB";
            }
        }

        return null;
    }

    /// <summary>Gets the default country of a language (it → IT, en → GB...).</summary>
    /// <param name="language">ISO 639-1.</param>
    /// <returns>A country or null.</returns>
    public static string? CountryForLanguage(string? language) => language switch
    {
        "it" => "IT", "en" => "GB", "es" => "ES", "de" => "DE", "fr" => "FR", "pt" => "PT", "nl" => "NL", "pl" => "PL",
        "tr" => "TR", "ru" => "RU", "ar" => "SA", "el" => "GR", "ro" => "RO", "sv" => "SE", "da" => "DK", "no" => "NO",
        "fi" => "FI", "hr" => "HR", "sr" => "RS", "sq" => "AL", "ja" => "JP", "zh" => "CN", "ko" => "KR", "hi" => "IN",
        _ => null,
    };

    /// <summary>Gets the localized display name of a language (it → Italiano / Italian).</summary>
    /// <param name="language">ISO 639-1.</param>
    /// <param name="uiLanguage">UI language.</param>
    /// <returns>The name.</returns>
    public static string LanguageName(string? language, string uiLanguage)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            var name = string.Equals(uiLanguage, culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase)
                ? culture.NativeName
                : (string.Equals(uiLanguage, "it", StringComparison.OrdinalIgnoreCase) ? ItalianLanguageName(language) ?? culture.EnglishName : culture.EnglishName);
            return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..] : name;
        }
        catch (CultureNotFoundException)
        {
            return language.ToUpperInvariant();
        }
    }

    /// <summary>Converts ISO 639-1 to the ISO 639-2 code used by Jellyfin media streams (it → ita).</summary>
    /// <param name="language">ISO 639-1.</param>
    /// <returns>The three-letter code, or null.</returns>
    public static string? ToIso6392(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        try
        {
            var three = CultureInfo.GetCultureInfo(language).ThreeLetterISOLanguageName;
            return three.Length == 3 ? three : null;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private static bool IsUpper(string s) => s.All(c => !char.IsLetter(c) || char.IsUpper(c));

    private static IEnumerable<string> Tags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        MatchCollection matches;
        try
        {
            matches = TagRegex.Matches(text);
        }
        catch (RegexMatchTimeoutException)
        {
            yield break;
        }

        foreach (Match m in matches)
        {
            var tag = m.Groups["tag"].Value;

            // Only upper-case tags are considered (avoid ordinary words).
            if (IsUpper(tag))
            {
                yield return tag;
            }
        }
    }

    private static string? ItalianLanguageName(string code) => code switch
    {
        "it" => "italiano", "en" => "inglese", "es" => "spagnolo", "de" => "tedesco", "fr" => "francese", "pt" => "portoghese",
        "nl" => "olandese", "pl" => "polacco", "tr" => "turco", "ru" => "russo", "ar" => "arabo", "el" => "greco", "ro" => "rumeno",
        "sv" => "svedese", "da" => "danese", "no" => "norvegese", "fi" => "finlandese", "hr" => "croato", "sr" => "serbo",
        "sq" => "albanese", "ja" => "giapponese", "zh" => "cinese", "ko" => "coreano", "hi" => "hindi",
        _ => null,
    };

    private static string RemoveDiacritics(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static Dictionary<string, string> BuildLanguageAliases()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            var two = culture.TwoLetterISOLanguageName;
            if (two.Length != 2 || string.Equals(two, "iv", StringComparison.Ordinal))
            {
                continue;
            }

            d.TryAdd(two, two);
            if (culture.ThreeLetterISOLanguageName.Length == 3)
            {
                d.TryAdd(culture.ThreeLetterISOLanguageName, two);
            }

            if (culture.ThreeLetterWindowsLanguageName.Length == 3)
            {
                d.TryAdd(culture.ThreeLetterWindowsLanguageName, two);
            }

            d.TryAdd(RemoveDiacritics(culture.EnglishName).ToLowerInvariant(), two);
            d.TryAdd(RemoveDiacritics(culture.NativeName).ToLowerInvariant(), two);
        }

        // Common aliases and bibliographic codes not covered by cultures.
        foreach (var (alias, code) in new (string Alias, string Code)[]
        {
            ("italiano", "it"), ("italian", "it"), ("ita", "it"), ("eng", "en"), ("english", "en"), ("inglese", "en"),
            ("esp", "es"), ("spa", "es"), ("spanish", "es"), ("espanol", "es"), ("castellano", "es"), ("spagnolo", "es"),
            ("ger", "de"), ("deu", "de"), ("german", "de"), ("deutsch", "de"), ("tedesco", "de"),
            ("fre", "fr"), ("fra", "fr"), ("french", "fr"), ("francais", "fr"), ("francese", "fr"),
            ("por", "pt"), ("portuguese", "pt"), ("portugues", "pt"), ("br", "pt"), ("ptbr", "pt"),
            ("dut", "nl"), ("nld", "nl"), ("dutch", "nl"), ("nederlands", "nl"),
            ("ara", "ar"), ("arabic", "ar"), ("tur", "tr"), ("turkish", "tr"), ("rus", "ru"), ("russian", "ru"),
            ("pol", "pl"), ("polish", "pl"), ("gre", "el"), ("greek", "el"), ("rum", "ro"), ("ron", "ro"),
            ("alb", "sq"), ("albanian", "sq"), ("chi", "zh"), ("zho", "zh"), ("chinese", "zh"), ("jpn", "ja"),
            ("latino", "es"), ("uk", "en"), ("us", "en"), ("gb", "en"),
        })
        {
            d[alias] = code;
        }

        // Fallback 2-letter codes (invariant globalization hosts). "uk" is kept as United Kingdom (English), the usual IPTV meaning.
        foreach (var code in "it en es de fr pt nl ar tr ru pl el ro sq zh ja ko hi sv da no fi hr sr cs hu bg sl sk he fa".Split(' '))
        {
            d.TryAdd(code, code);
        }

        return d;
    }
}
