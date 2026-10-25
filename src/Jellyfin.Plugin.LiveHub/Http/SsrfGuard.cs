using System;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.LiveHub.Http;

/// <summary>
/// Server-side request forgery protection: only http/https, no loopback/private/link-local/multicast targets
/// unless a provider explicitly allows private networks.
/// </summary>
public static class SsrfGuard
{
    /// <summary>Validates a URL before sending (host level). Throws <see cref="SsrfException"/>.</summary>
    /// <param name="uri">The URL.</param>
    /// <param name="allowPrivate">Whether private targets are allowed.</param>
    public static void Check(Uri uri, bool allowPrivate)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SsrfException($"Only http/https URLs are allowed ({uri.Scheme}).");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new SsrfException("Credentials in URLs are not allowed.");
        }

        if (allowPrivate)
        {
            return;
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            if (IsForbidden(ip))
            {
                throw new SsrfException($"Destination {ip} is a private or local address.");
            }

            return;
        }

        if (IsLocalName(host))
        {
            throw new SsrfException($"Destination '{host}' is a local host name.");
        }
    }

    /// <summary>Returns whether a host name is local (localhost, *.local, single label...).</summary>
    /// <param name="host">The host.</param>
    /// <returns>True when local.</returns>
    public static bool IsLocalName(string host)
    {
        var h = host.ToLowerInvariant();
        return h == "localhost"
            || !h.Contains('.', StringComparison.Ordinal)
            || h.EndsWith(".localhost", StringComparison.Ordinal)
            || h.EndsWith(".local", StringComparison.Ordinal)
            || h.EndsWith(".internal", StringComparison.Ordinal)
            || h.EndsWith(".lan", StringComparison.Ordinal)
            || h.EndsWith(".home", StringComparison.Ordinal)
            || h.EndsWith(".home.arpa", StringComparison.Ordinal)
            || h.EndsWith(".intranet", StringComparison.Ordinal);
    }

    /// <summary>Returns whether an IP address must never be reached.</summary>
    /// <param name="ip">The address.</param>
    /// <returns>True when forbidden.</returns>
    public static bool IsForbidden(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 0/8, 10/8, 127/8, CGNAT 100.64/10, link-local and cloud metadata 169.254/16, 172.16/12, 192.168/16,
            // IETF 192.0.0/24, benchmarking 198.18/15, multicast and reserved 224/3.
            return b[0] == 0
                || b[0] == 10
                || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                || b[0] >= 224;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal)
            {
                return true;
            }

            var b = ip.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7
        }

        return true;
    }
}
