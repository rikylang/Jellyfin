using System;
using System.Buffers.Text;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Http;
using Jellyfin.Plugin.LiveHub.Security;
using Jellyfin.Plugin.LiveHub.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Images;

/// <summary>
/// Remote images (posters, logos) are downloaded by the plugin through the provider egress — never by Jellyfin
/// directly — and cached on disk. Only URLs signed by the plugin (HMAC) are served, so this is not an open proxy.
/// </summary>
public sealed class ImageProxy
{
    private const long MaxBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly SecretProtector _secrets;
    private readonly IOutboundHttp _http;
    private readonly DefinitionStore _store;
    private readonly IEgressResolver _egress;
    private readonly LiveHubPaths _paths;
    private readonly ILogger<ImageProxy> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImageProxy"/> class.
    /// </summary>
    /// <param name="secrets">Key holder.</param>
    /// <param name="http">Outgoing HTTP.</param>
    /// <param name="store">Definitions (egress of the provider).</param>
    /// <param name="egress">Egress resolver.</param>
    /// <param name="paths">Paths.</param>
    /// <param name="logger">Logger.</param>
    public ImageProxy(SecretProtector secrets, IOutboundHttp http, DefinitionStore store, IEgressResolver egress, LiveHubPaths paths, ILogger<ImageProxy> logger)
    {
        _secrets = secrets;
        _http = http;
        _store = store;
        _egress = egress;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Builds the signed relative path of a remote image ("LiveHub/Images/Remote?…").</summary>
    /// <param name="url">The remote URL.</param>
    /// <param name="providerId">The provider whose egress is used.</param>
    /// <returns>The path (relative to the server base URL), or null for non-http URLs.</returns>
    public string? SignPath(string? url, string providerId)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return "LiveHub/Images/Remote?u=" + Uri.EscapeDataString(uri.AbsoluteUri) + "&p=" + Uri.EscapeDataString(providerId ?? string.Empty) + "&s=" + Signature(uri.AbsoluteUri, providerId ?? string.Empty);
    }

    /// <summary>Verifies a signature.</summary>
    /// <param name="url">The URL.</param>
    /// <param name="providerId">The provider.</param>
    /// <param name="signature">The signature.</param>
    /// <returns>True when valid.</returns>
    public bool Verify(string url, string providerId, string signature)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Signature(url, providerId)), Encoding.ASCII.GetBytes(signature ?? string.Empty));

    /// <summary>Gets a remote image (disk cache, then the provider egress).</summary>
    /// <param name="url">The URL.</param>
    /// <param name="providerId">The provider.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Bytes and content type, or null.</returns>
    public async Task<(byte[] Data, string ContentType)?> GetAsync(string url, string providerId, CancellationToken cancellationToken)
    {
        var hash = Keys.Hash(providerId + "|" + url);
        var dir = Path.Combine(_paths.ImagesDir, "remote");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, hash + ".bin");
        var typeFile = file + ".type";
        if (File.Exists(file) && File.Exists(typeFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < CacheTtl)
        {
            return (await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false), await File.ReadAllTextAsync(typeFile, cancellationToken).ConfigureAwait(false));
        }

        var def = string.IsNullOrEmpty(providerId) ? null : _store.Get(providerId);
        try
        {
            var response = await _http.FetchAsync(
                new FetchRequest
                {
                    ProviderId = providerId ?? string.Empty,
                    Url = new Uri(url),
                    EgressProfileId = _egress.ResolveProfileId(def?.EgressProfile, def?.Defaults?.Country),
                    AllowPrivateNetwork = def?.AllowPrivateNetwork ?? false,
                    MaxBytes = MaxBytes,
                    Timeout = TimeSpan.FromSeconds(15),
                },
                cancellationToken).ConfigureAwait(false);
            var type = response.ContentType ?? string.Empty;
            if (!response.IsSuccess || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var tmp = file + ".tmp";
            await File.WriteAllBytesAsync(tmp, response.Body, cancellationToken).ConfigureAwait(false);
            File.Move(tmp, file, overwrite: true);
            await File.WriteAllTextAsync(typeFile, type, cancellationToken).ConfigureAwait(false);
            return (response.Body, type);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TimeoutException or SsrfException or EgressUnavailableException or ResponseTooLargeException or IOException)
        {
            _logger.LogDebug("Image {Host} unavailable: {Error}", new Uri(url).Host, ex.Message);
            return null;
        }
    }

    private string Signature(string url, string providerId)
    {
        var mac = HMACSHA256.HashData(_secrets.RelayKey, Encoding.UTF8.GetBytes("img|" + providerId + "|" + url));
        return Base64Url.EncodeToString(mac.AsSpan(0, 12));
    }
}
