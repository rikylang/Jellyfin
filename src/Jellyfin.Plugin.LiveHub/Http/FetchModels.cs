using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveHub.Http;

/// <summary>An outgoing request.</summary>
public sealed class FetchRequest
{
    /// <summary>Gets or sets the provider id (diagnostics, rate limit).</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the URL.</summary>
    public Uri Url { get; set; } = new("about:blank");

    /// <summary>Gets or sets the method.</summary>
    public HttpMethod Method { get; set; } = HttpMethod.Get;

    /// <summary>Gets or sets the headers.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the body.</summary>
    public string? Body { get; set; }

    /// <summary>Gets or sets the body content type.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets or sets the maximum response size.</summary>
    public long MaxBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Gets or sets the timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets or sets the egress profile id.</summary>
    public string EgressProfileId { get; set; } = "direct";

    /// <summary>Gets or sets a value indicating whether private destinations are allowed.</summary>
    public bool AllowPrivateNetwork { get; set; }

    /// <summary>Gets or sets the If-None-Match validator.</summary>
    public string? IfNoneMatch { get; set; }

    /// <summary>Gets or sets the If-Modified-Since validator.</summary>
    public DateTimeOffset? IfModifiedSince { get; set; }

    /// <summary>Gets or sets a value indicating whether redirects are followed.</summary>
    public bool FollowRedirects { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether this is an egress health probe (allowed while the kill switch is engaged).</summary>
    public bool HealthProbe { get; set; }

    /// <summary>Gets or sets the HTTP Range header value (bytes=...), optional.</summary>
    public string? Range { get; set; }
}

/// <summary>A buffered response.</summary>
public sealed class FetchResponse
{
    /// <summary>Gets or sets the status code.</summary>
    public int StatusCode { get; set; }

    /// <summary>Gets or sets the body.</summary>
    public byte[] Body { get; set; } = Array.Empty<byte>();

    /// <summary>Gets or sets the content type.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets or sets the ETag.</summary>
    public string? ETag { get; set; }

    /// <summary>Gets or sets the Last-Modified date.</summary>
    public DateTimeOffset? LastModified { get; set; }

    /// <summary>Gets or sets the response headers (Link, Retry-After...).</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the final URL after redirects.</summary>
    public Uri FinalUrl { get; set; } = new("about:blank");

    /// <summary>Gets or sets the elapsed time.</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>Gets a value indicating whether the status is 2xx.</summary>
    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>Gets the body as UTF-8 text.</summary>
    /// <returns>The text.</returns>
    public string Text() => System.Text.Encoding.UTF8.GetString(Body).TrimStart('﻿');
}

/// <summary>A streaming response (relay); dispose to release the connection.</summary>
public sealed class StreamingResponse : IAsyncDisposable
{
    private readonly HttpResponseMessage _message;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamingResponse"/> class.
    /// </summary>
    /// <param name="message">The response.</param>
    /// <param name="stream">The body stream.</param>
    /// <param name="finalUrl">The final URL.</param>
    public StreamingResponse(HttpResponseMessage message, Stream stream, Uri finalUrl)
    {
        _message = message;
        Stream = stream;
        FinalUrl = finalUrl;
    }

    /// <summary>Gets the status code.</summary>
    public int StatusCode => (int)_message.StatusCode;

    /// <summary>Gets a response header (response or content headers).</summary>
    /// <param name="name">The header name.</param>
    /// <returns>The value or null.</returns>
    public string? Header(string name)
        => _message.Headers.TryGetValues(name, out var v) || _message.Content.Headers.TryGetValues(name, out v) ? string.Join(", ", v) : null;

    /// <summary>Gets the content type.</summary>
    public string? ContentType => _message.Content.Headers.ContentType?.ToString();

    /// <summary>Gets the content length.</summary>
    public long? ContentLength => _message.Content.Headers.ContentLength;

    /// <summary>Gets the content range.</summary>
    public string? ContentRange => _message.Content.Headers.ContentRange?.ToString();

    /// <summary>Gets the body stream.</summary>
    public Stream Stream { get; }

    /// <summary>Gets the final URL.</summary>
    public Uri FinalUrl { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        _message.Dispose();
    }
}

/// <summary>Thrown when a response exceeds the configured size.</summary>
public sealed class ResponseTooLargeException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResponseTooLargeException"/> class.
    /// </summary>
    public ResponseTooLargeException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResponseTooLargeException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ResponseTooLargeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ResponseTooLargeException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The inner exception.</param>
    public ResponseTooLargeException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Thrown when an outgoing request targets a forbidden destination.</summary>
public sealed class SsrfException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SsrfException"/> class.
    /// </summary>
    public SsrfException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SsrfException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public SsrfException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SsrfException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The inner exception.</param>
    public SsrfException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Thrown when the egress of a provider is down (kill switch) or misconfigured.</summary>
public sealed class EgressUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EgressUnavailableException"/> class.
    /// </summary>
    public EgressUnavailableException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EgressUnavailableException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public EgressUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EgressUnavailableException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="inner">The inner exception.</param>
    public EgressUnavailableException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
