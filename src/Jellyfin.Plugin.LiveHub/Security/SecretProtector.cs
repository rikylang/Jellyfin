using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LiveHub.Storage;

namespace Jellyfin.Plugin.LiveHub.Security;

/// <summary>
/// Encrypts secrets at rest with AES-256-GCM. The master key is generated once and stored in the plugin data folder;
/// sub-keys (secrets, relay HMAC) are derived with HKDF.
/// </summary>
public sealed class SecretProtector
{
    /// <summary>Prefix of encrypted values.</summary>
    public const string Prefix = "enc:v1:";

    /// <summary>Placeholder returned to the UI instead of secret values.</summary>
    public const string Mask = "••••••••";

    private readonly byte[] _secretKey;
    private readonly byte[] _relayKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretProtector"/> class.
    /// </summary>
    /// <param name="paths">Plugin paths.</param>
    public SecretProtector(LiveHubPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var master = LoadOrCreateMaster(paths.KeyFile);
        _secretKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes("livehub-secrets-v1"));
        _relayKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, info: Encoding.ASCII.GetBytes("livehub-relay-v1"));
    }

    /// <summary>Gets the key used to sign relay tokens.</summary>
    public ReadOnlySpan<byte> RelayKey => _relayKey;

    /// <summary>Returns whether a value is encrypted.</summary>
    /// <param name="value">The value.</param>
    /// <returns>True when encrypted.</returns>
    public static bool IsProtected(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Encrypts a value (already encrypted values are returned unchanged).</summary>
    /// <param name="plain">The plain text.</param>
    /// <returns>The protected value.</returns>
    public string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || IsProtected(plain))
        {
            return plain ?? string.Empty;
        }

        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(_secretKey, 16))
        {
            aes.Encrypt(nonce, data, cipher, tag);
        }

        var blob = new byte[12 + 16 + cipher.Length];
        nonce.CopyTo(blob, 0);
        tag.CopyTo(blob, 12);
        cipher.CopyTo(blob, 28);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>Decrypts a value (plain values are returned unchanged).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The plain text, or empty when it cannot be decrypted.</returns>
    public string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!IsProtected(value))
        {
            return value;
        }

        try
        {
            var blob = Convert.FromBase64String(value[Prefix.Length..]);
            if (blob.Length < 28)
            {
                return string.Empty;
            }

            var plain = new byte[blob.Length - 28];
            using var aes = new AesGcm(_secretKey, 16);
            aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return string.Empty;
        }
    }

    private static byte[] LoadOrCreateMaster(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32)
            {
                return existing;
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, key);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, overwrite: true);
        return key;
    }
}
