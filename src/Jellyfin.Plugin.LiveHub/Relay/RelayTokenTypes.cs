using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Security;

namespace Jellyfin.Plugin.LiveHub.Relay;

/// <summary>The claims carried by a relay token.</summary>
public sealed class RelayClaims
{
    /// <summary>Gets or sets the item kind (e = event, c = 24/7 channel).</summary>
    [JsonPropertyName("t")]
    public string Type { get; set; } = "e";

    /// <summary>Gets or sets the event/channel key.</summary>
    [JsonPropertyName("k")]
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the preferred stream id.</summary>
    [JsonPropertyName("s")]
    public string StreamId { get; set; } = string.Empty;

    /// <summary>Gets or sets the variant selector (see <see cref="VariantSelector"/>).</summary>
    [JsonPropertyName("v")]
    public string Variant { get; set; } = "auto";

    /// <summary>Gets or sets the user id ("N" format, empty for system use such as the admin test).</summary>
    [JsonPropertyName("u")]
    public string User { get; set; } = string.Empty;

    /// <summary>Gets or sets the expiry (unix seconds).</summary>
    [JsonPropertyName("x")]
    public long Expires { get; set; }

    /// <summary>Gets the item kind.</summary>
    [JsonIgnore]
    public ItemKind Kind => Type == "c" ? ItemKind.Channel : ItemKind.Event;

    /// <summary>Gets the user id.</summary>
    [JsonIgnore]
    public Guid UserId => Guid.TryParse(User, out var g) ? g : Guid.Empty;

    /// <summary>Gets the session key (shared by every user watching the same stream and variant).</summary>
    [JsonIgnore]
    public string SessionKey => Type + "|" + Key + "|" + StreamId + "|" + Variant;
}

/// <summary>
/// Signs and verifies relay tokens: base64url(JSON claims) + "." + base64url(HMAC-SHA256 truncated to 128 bits).
/// The key is derived from the plugin master key, so tokens survive restarts but cannot be forged.
/// </summary>
public sealed class RelayTokenService
{
    private readonly SecretProtector _secrets;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayTokenService"/> class.
    /// </summary>
    /// <param name="secrets">Key holder.</param>
    /// <param name="clock">Clock.</param>
    public RelayTokenService(SecretProtector secrets, TimeProvider? clock = null)
    {
        _secrets = secrets;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Creates a token.</summary>
    /// <param name="claims">The claims (expiry is set when zero).</param>
    /// <param name="lifetime">Lifetime.</param>
    /// <returns>The token.</returns>
    public string Create(RelayClaims claims, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.Expires == 0)
        {
            claims.Expires = _clock.GetUtcNow().Add(lifetime).ToUnixTimeSeconds();
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(claims);
        var mac = Sign(payload);
        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(mac);
    }

    /// <summary>Validates a token.</summary>
    /// <param name="token">The token.</param>
    /// <param name="claims">The claims when valid.</param>
    /// <returns>True when the signature is valid and the token has not expired.</returns>
    public bool TryValidate(string? token, out RelayClaims claims)
    {
        claims = new RelayClaims();
        if (string.IsNullOrEmpty(token) || token.Length > 4096)
        {
            return false;
        }

        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == token.Length - 1)
        {
            return false;
        }

        byte[] payload;
        byte[] mac;
        try
        {
            payload = Base64Url.DecodeFromChars(token.AsSpan(0, dot));
            mac = Base64Url.DecodeFromChars(token.AsSpan(dot + 1));
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(Sign(payload), mac))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RelayClaims>(payload);
            if (parsed is null || parsed.Expires < _clock.GetUtcNow().ToUnixTimeSeconds() || string.IsNullOrEmpty(parsed.Key))
            {
                return false;
            }

            claims = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private byte[] Sign(byte[] payload)
    {
        var full = HMACSHA256.HashData(_secrets.RelayKey, payload);
        return full.AsSpan(0, 16).ToArray();
    }

    /// <summary>Builds a short, URL-safe id (relay URL segments).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The id.</returns>
    internal static string ShortId(string value) => Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 9));
}
