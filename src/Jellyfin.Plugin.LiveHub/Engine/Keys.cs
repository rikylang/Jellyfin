using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.LiveHub.Engine;

/// <summary>
/// Stable, compact keys: "e" + 16 hex chars for events, "c" for channels, "s" for streams. Keys never contain ':' or '|'
/// so they can be embedded in channel item ids.
/// </summary>
public static class Keys
{
    /// <summary>Event key from provider and external id.</summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="externalId">The external id.</param>
    /// <returns>The key.</returns>
    public static string Event(string providerId, string externalId) => "e" + Hash(providerId + "\u001f" + externalId);

    /// <summary>Channel key from provider and external id.</summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="externalId">The external id.</param>
    /// <returns>The key.</returns>
    public static string Channel(string providerId, string externalId) => "c" + Hash(providerId + "\u001f" + externalId);

    /// <summary>Stream key.</summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="owner">Owning event/channel external id.</param>
    /// <param name="idOrUrl">Mapped stream id or URL.</param>
    /// <returns>The key.</returns>
    public static string Stream(string providerId, string owner, string idOrUrl) => "s" + Hash(providerId + "\u001f" + owner + "\u001f" + idOrUrl);

    /// <summary>Short hex hash (64 bits of SHA-256).</summary>
    /// <param name="value">The input.</param>
    /// <returns>16 hex chars.</returns>
    public static string Hash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), digest);
        return Convert.ToHexStringLower(digest[..8]);
    }
}
