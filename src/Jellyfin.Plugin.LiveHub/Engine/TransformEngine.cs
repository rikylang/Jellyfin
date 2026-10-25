using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.LiveHub.Definitions;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Applies declarative, side-effect free transforms. No code is ever executed; regexes run with a timeout.
/// </summary>
public static class TransformEngine
{
    /// <summary>Supported operations.</summary>
    public static readonly IReadOnlySet<string> Operations = new HashSet<string>(StringComparer.Ordinal)
    {
        "lowercase", "uppercase", "trim", "default", "map", "replace", "extract", "concat", "split", "join",
        "prefix", "suffix", "number", "boolean", "absoluteUrl", "stripHtml", "template", "first", "last",
    };

    /// <summary>Default values considered "true" by the boolean transform.</summary>
    public static readonly IReadOnlyList<string> DefaultTrueValues = new[]
    {
        "true", "1", "yes", "y", "on", "si", "sì", "live", "inplay", "in_play", "in-play", "in play", "playing", "started", "running", "onair", "on_air",
    };

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.Ordinal);
    private static readonly Regex TagRegex = new("<[^>]*>", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SpaceRegex = new(@"\s{2,}", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex NumberToken = new(@"-?\d[\d.,]*(?:[eE][-+]?\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Gets a cached regex with timeout.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The regex.</returns>
    /// <exception cref="ArgumentException">Invalid pattern.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3012:Review code for regex injection vulnerabilities", Justification = "Admin-authored patterns are a feature; every Regex has a match timeout (ReDoS safe).")]
    public static Regex GetRegex(string pattern)
    {
        if (RegexCache.TryGetValue(pattern, out var r))
        {
            return r;
        }

        r = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
        if (RegexCache.Count < 2048)
        {
            RegexCache[pattern] = r;
        }

        return r;
    }

    /// <summary>Applies a list of transforms.</summary>
    /// <param name="value">The input.</param>
    /// <param name="transforms">The transforms.</param>
    /// <param name="ctx">Template context ({value}, item paths).</param>
    /// <returns>The output.</returns>
    public static MappedValue Apply(MappedValue value, IReadOnlyList<TransformSpec>? transforms, TemplateContext ctx)
    {
        if (transforms is null || transforms.Count == 0)
        {
            return value;
        }

        var current = value;
        foreach (var t in transforms)
        {
            current = ApplyOne(current, t, ctx);
        }

        return current;
    }

    private static MappedValue ApplyOne(MappedValue v, TransformSpec t, TemplateContext ctx)
    {
        try
        {
            switch (t.Op)
            {
                case "lowercase": return Each(v, s => s?.ToLowerInvariant());
                case "uppercase": return Each(v, s => s?.ToUpperInvariant());
                case "trim": return Each(v, s => s?.Trim());
                case "default": return v.IsEmpty ? MappedValue.Of(ctx.Render(t.Value)) : v;
                case "map": return Each(v, s => Map(s, t));
                case "replace":
                    {
                        var re = GetRegex(t.Pattern ?? string.Empty);
                        return Each(v, s => s is null ? null : re.Replace(s, t.Replacement ?? string.Empty));
                    }

                case "extract":
                    {
                        var re = GetRegex(t.Pattern ?? string.Empty);
                        return Each(v, s => Extract(re, s, t.Group));
                    }

                case "concat":
                    {
                        var c = ctx.Fork();
                        c.Value = v.Text;
                        return MappedValue.Of(string.Concat((t.Values ?? new List<string>()).Select(c.Render)));
                    }

                case "split":
                    {
                        if (v.Text is null)
                        {
                            return v;
                        }

                        var parts = v.Text.Split(t.Separator ?? ",", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        if (t.Index is int idx)
                        {
                            var i = idx < 0 ? parts.Length + idx : idx;
                            return MappedValue.Of(i >= 0 && i < parts.Length ? parts[i] : null);
                        }

                        return MappedValue.OfList(parts);
                    }

                case "join": return MappedValue.Of(string.Join(t.Separator ?? ", ", v.All()));
                case "prefix": return Each(v, s => string.IsNullOrEmpty(s) ? s : ctx.Render(t.Value) + s);
                case "suffix": return Each(v, s => string.IsNullOrEmpty(s) ? s : s + ctx.Render(t.Value));
                case "number": return Each(v, ToNumber);
                case "boolean":
                    {
                        var trueValues = t.TrueValues is { Count: > 0 } ? t.TrueValues : DefaultTrueValues;
                        return Each(v, s => s is null ? null : (trueValues.Any(x => string.Equals(x, s.Trim(), StringComparison.OrdinalIgnoreCase)) ? "true" : "false"));
                    }

                case "absoluteUrl":
                    {
                        var baseUrl = ctx.Render(t.Base ?? "{baseUrl}");
                        return Each(v, s => AbsoluteUrl(baseUrl, s));
                    }

                case "stripHtml": return Each(v, s => s is null ? null : SpaceRegex.Replace(WebUtility.HtmlDecode(TagRegex.Replace(s, " ")), " ").Trim());
                case "template":
                    return Each(v, s =>
                    {
                        if (string.IsNullOrEmpty(s))
                        {
                            return s;
                        }

                        var c = ctx.Fork();
                        c.Value = s;
                        return c.Render(t.Value);
                    });
                case "first": return MappedValue.Of(v.All().FirstOrDefault());
                case "last": return MappedValue.Of(v.All().LastOrDefault());
                default: return v;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return v;
        }
    }

    private static MappedValue Each(MappedValue v, Func<string?, string?> f)
    {
        if (v.IsList)
        {
            return MappedValue.OfList(v.List!.Select(f).ToList());
        }

        return new MappedValue(f(v.Text), null, v.Raw);
    }

    private static string? Map(string? s, TransformSpec t)
    {
        if (s is null)
        {
            return t.Default;
        }

        var table = t.Table;
        if (table is not null)
        {
            var ignoreCase = t.IgnoreCase ?? true;
            foreach (var kv in table)
            {
                if (string.Equals(kv.Key, s.Trim(), ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return kv.Value;
                }
            }
        }

        if (t.Strict == true)
        {
            return null;
        }

        return t.Default ?? s;
    }

    private static string? Extract(Regex re, string? s, string? group)
    {
        if (s is null)
        {
            return null;
        }

        var m = re.Match(s);
        if (!m.Success)
        {
            return null;
        }

        if (string.IsNullOrEmpty(group))
        {
            return m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
        }

        if (int.TryParse(group, NumberStyles.Integer, CultureInfo.InvariantCulture, out var gi))
        {
            return gi < m.Groups.Count ? m.Groups[gi].Value : null;
        }

        var named = m.Groups[group];
        return named.Success ? named.Value : null;
    }

    private static string? ToNumber(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        Match token;
        try
        {
            token = NumberToken.Match(s);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!token.Success)
        {
            return null;
        }

        var cleaned = token.Value;
        var commas = cleaned.Count(ch => ch == ',');
        var dots = cleaned.Count(ch => ch == '.');
        if (commas == 1 && dots == 0)
        {
            // "1,500" (thousands) vs "2,5" (decimal comma): three digits after the comma means thousands.
            var after = cleaned.Length - cleaned.IndexOf(',', StringComparison.Ordinal) - 1;
            cleaned = after == 3 ? cleaned.Replace(",", string.Empty, StringComparison.Ordinal) : cleaned.Replace(',', '.');
        }
        else if (dots > 1 && commas <= 1)
        {
            // "1.234.567,8"
            cleaned = cleaned.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        }
        else
        {
            cleaned = cleaned.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Resolves a possibly relative URL against a base.</summary>
    /// <param name="baseUrl">The base.</param>
    /// <param name="value">The value.</param>
    /// <returns>An absolute URL, or the input when it cannot be resolved.</returns>
    public static string? AbsoluteUrl(string? baseUrl, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            return abs.ToString();
        }

        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var b))
        {
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return b.Scheme + ":" + trimmed;
            }

            if (Uri.TryCreate(b, trimmed, out var combined))
            {
                return combined.ToString();
            }
        }

        return trimmed;
    }
}
