using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LiveHub.Security;

/// <summary>
/// Masks credentials in URLs, headers and messages before they reach logs, diagnostics or the UI.
/// </summary>
public static class Redactor
{
    private static readonly Regex SensitiveQuery = new(
        @"(?<k>[?&;](?:api[_-]?key|apikey|key|token|access[_-]?token|auth|authorization|password|pass|pwd|secret|client[_-]?secret|sig|signature|hash|username|user|session|sid)=)(?<v>[^&#;]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex UserInfo = new(@"(?<=://)[^/@\s]+:[^/@\s]+@", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "Api-Key", "X-Auth-Token", "X-Access-Token",
    };

    /// <summary>Redacts a text (URL or message).</summary>
    /// <param name="text">The text.</param>
    /// <param name="secrets">Known secret values to mask.</param>
    /// <returns>The redacted text.</returns>
    public static string Redact(string? text, IEnumerable<string>? secrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;
        if (secrets is not null)
        {
            foreach (var s in secrets.Where(s => !string.IsNullOrEmpty(s) && s.Length >= 3).OrderByDescending(s => s.Length))
            {
                result = result.Replace(s, "***", StringComparison.Ordinal);
                var encoded = Uri.EscapeDataString(s);
                if (!string.Equals(encoded, s, StringComparison.Ordinal))
                {
                    result = result.Replace(encoded, "***", StringComparison.Ordinal);
                }
            }
        }

        try
        {
            result = SensitiveQuery.Replace(result, m => m.Groups["k"].Value + "***");
            result = UserInfo.Replace(result, "***:***@");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[redacted]";
        }

        return result;
    }

    /// <summary>Returns whether a header carries credentials.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>True when sensitive.</returns>
    public static bool IsSensitiveHeader(string name) => SensitiveHeaders.Contains(name);

    /// <summary>Redacts header values.</summary>
    /// <param name="headers">The headers.</param>
    /// <returns>A redacted copy.</returns>
    public static Dictionary<string, string> RedactHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
        {
            return copy;
        }

        foreach (var kv in headers)
        {
            copy[kv.Key] = IsSensitiveHeader(kv.Key) ? "***" : kv.Value;
        }

        return copy;
    }
}
