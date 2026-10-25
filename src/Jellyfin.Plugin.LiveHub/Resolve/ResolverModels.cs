using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Relay;

namespace Jellyfin.Plugin.LiveHub.Resolve;

/// <summary>Input of a resolver.</summary>
public sealed class ResolveRequest
{
    /// <summary>Gets or sets the stream to resolve.</summary>
    public LiveStream Stream { get; set; } = new();

    /// <summary>Gets or sets the URL to resolve (the stream URL or an extracted candidate).</summary>
    public Uri Url { get; set; } = new("about:blank");

    /// <summary>Gets or sets the provider definition (plain secrets), if known.</summary>
    public ProviderDefinition? Definition { get; set; }

    /// <summary>Gets or sets the egress profile.</summary>
    public string EgressProfileId { get; set; } = "direct";

    /// <summary>Gets or sets a value indicating whether private destinations are allowed.</summary>
    public bool AllowPrivateNetwork { get; set; }

    /// <summary>Gets or sets the headers sent to the origin.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the request timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets the diagnostic steps.</summary>
    public List<string> Steps { get; init; } = new();
}

/// <summary>A playable stream produced by the resolver chain.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Domain name required by the specification (ResolvedStream).")]
public sealed class ResolvedStream
{
    /// <summary>Gets or sets the playable URL (final URL after redirects).</summary>
    public Uri Url { get; set; } = new("about:blank");

    /// <summary>Gets or sets the headers required by the origin.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets when the URL expires.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Gets or sets the detected kind.</summary>
    public StreamKind Kind { get; set; }

    /// <summary>Gets or sets the parsed HLS playlist (master or media) fetched while validating.</summary>
    public HlsPlaylist? Playlist { get; set; }

    /// <summary>Gets the HLS variants (empty for media playlists and non-HLS kinds).</summary>
    public IReadOnlyList<HlsVariant> Variants => Playlist?.Variants ?? (IReadOnlyList<HlsVariant>)Array.Empty<HlsVariant>();

    /// <summary>Gets or sets the strategy that produced the URL (direct, extract, headless).</summary>
    public string Strategy { get; set; } = "direct";

    /// <summary>Gets or sets a value indicating whether the URL was found in a web page.</summary>
    public bool FromEmbed { get; set; }

    /// <summary>Gets or sets the egress profile to use for every request of this stream.</summary>
    public string EgressProfileId { get; set; } = "direct";

    /// <summary>Gets or sets a value indicating whether private destinations are allowed.</summary>
    public bool AllowPrivateNetwork { get; set; }

    /// <summary>Gets or sets the provider id.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the resolution latency.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>Gets or sets the content type returned by the origin.</summary>
    public string? ContentType { get; set; }
}

/// <summary>A resolver of the chain (direct → page extraction → headless sidecar).</summary>
public interface IStreamResolver
{
    /// <summary>Gets the strategy name.</summary>
    string Name { get; }

    /// <summary>Tries to resolve the request.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The resolved stream, or null when this resolver does not apply.</returns>
    Task<ResolvedStream?> ResolveAsync(ResolveRequest request, CancellationToken cancellationToken);
}

/// <summary>Thrown when a stream cannot be resolved; carries a diagnosis for the admin.</summary>
public sealed class ResolveException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResolveException"/> class.
    /// </summary>
    public ResolveException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResolveException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ResolveException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResolveException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The inner exception.</param>
    public ResolveException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResolveException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="statusCode">HTTP status of the origin.</param>
    /// <param name="hint">Diagnosis key (i18n "hint.*").</param>
    public ResolveException(string message, int? statusCode, string? hint)
        : base(message)
    {
        StatusCode = statusCode;
        Hint = hint;
    }

    /// <summary>Gets the HTTP status of the origin, if any.</summary>
    public int? StatusCode { get; }

    /// <summary>Gets the diagnosis key (i18n "hint.*").</summary>
    public string? Hint { get; }
}
