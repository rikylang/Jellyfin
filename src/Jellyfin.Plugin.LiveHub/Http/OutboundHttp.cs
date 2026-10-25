using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveHub.Http;

/// <summary>
/// Outgoing HTTP: named HttpClient "LiveHub.Egress" (IHttpClientFactory) routed per egress profile, kill switch,
/// SSRF checks on every redirect hop, timeouts and response size limits.
/// </summary>
public sealed class OutboundHttp : IOutboundHttp
{
    /// <summary>Name of the HttpClient registered in IHttpClientFactory.</summary>
    public const string ClientName = "LiveHub.Egress";

    /// <summary>Default User-Agent.</summary>
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36 LiveHub/1";

    private const int MaxRedirects = 5;

    private readonly IHttpClientFactory _factory;
    private readonly IEgressGate _gate;

    /// <summary>
    /// Initializes a new instance of the <see cref="OutboundHttp"/> class.
    /// </summary>
    /// <param name="factory">The HttpClient factory.</param>
    /// <param name="gate">The kill switch gate.</param>
    public OutboundHttp(IHttpClientFactory factory, IEgressGate gate)
    {
        _factory = factory;
        _gate = gate;
    }

    /// <inheritdoc />
    public async Task<FetchResponse> FetchAsync(FetchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.Timeout);
        try
        {
            using var response = await SendWithRedirectsAsync(request, cts.Token).ConfigureAwait(false);
            var result = new FetchResponse
            {
                StatusCode = (int)response.StatusCode,
                ContentType = response.Content.Headers.ContentType?.MediaType,
                ETag = response.Headers.ETag?.ToString(),
                LastModified = response.Content.Headers.LastModified,
                FinalUrl = response.RequestMessage?.RequestUri ?? request.Url,
            };

            foreach (var h in response.Headers.Concat(response.Content.Headers))
            {
                result.Headers[h.Key] = string.Join(", ", h.Value);
            }

            if (response.StatusCode != HttpStatusCode.NotModified)
            {
                result.Body = await ReadLimitedAsync(response, request.MaxBytes, cts.Token).ConfigureAwait(false);
            }

            result.Elapsed = sw.Elapsed;
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout after {request.Timeout.TotalSeconds:0}s.");
        }
    }

    /// <inheritdoc />
    public async Task<StreamingResponse> OpenStreamAsync(FetchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.Timeout);
        HttpResponseMessage response;
        try
        {
            response = await SendWithRedirectsAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout after {request.Timeout.TotalSeconds:0}s.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new StreamingResponse(response, stream, response.RequestMessage?.RequestUri ?? request.Url);
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(FetchRequest request, CancellationToken cancellationToken)
    {
        _gate.EnsureAvailable(request.EgressProfileId, request.HealthProbe);
        var client = _factory.CreateClient(ClientName);
        var url = request.Url;
        var method = request.Method;
        var body = request.Body;
        for (var hop = 0; ; hop++)
        {
            SsrfGuard.Check(url, request.AllowPrivateNetwork);
            using var message = Build(request, url, method, body);
            var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (request.FollowRedirects && (status is 301 or 302 or 303 or 307 or 308) && response.Headers.Location is not null)
            {
                if (hop >= MaxRedirects)
                {
                    response.Dispose();
                    throw new HttpRequestException("Too many redirects.");
                }

                var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(url, response.Headers.Location);
                response.Dispose();
                if (status == 303 || ((status == 301 || status == 302) && method == HttpMethod.Post))
                {
                    method = HttpMethod.Get;
                    body = null;
                }

                url = next;
                continue;
            }

            return response;
        }
    }

    private static HttpRequestMessage Build(FetchRequest request, Uri url, HttpMethod method, string? body)
    {
        var message = new HttpRequestMessage(method, url);
        message.Options.Set(EgressManager.ProfileOption, request.EgressProfileId);
        message.Options.Set(EgressManager.AllowPrivateOption, request.AllowPrivateNetwork);
        message.Options.Set(EgressManager.ProbeOption, request.HealthProbe);
        var hasUa = false;
        foreach (var kv in request.Headers)
        {
            if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                hasUa = true;
            }

            message.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }

        if (!hasUa)
        {
            message.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        }

        if (!string.IsNullOrEmpty(request.IfNoneMatch))
        {
            message.Headers.TryAddWithoutValidation("If-None-Match", request.IfNoneMatch);
        }

        if (request.IfModifiedSince.HasValue)
        {
            message.Headers.IfModifiedSince = request.IfModifiedSince;
        }

        if (!string.IsNullOrEmpty(request.Range))
        {
            message.Headers.TryAddWithoutValidation("Range", request.Range);
        }

        if (body is not null && method != HttpMethod.Get)
        {
            var contentType = request.ContentType
                ?? request.Headers.FirstOrDefault(h => string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value
                ?? "application/json";
            message.Content = new StringContent(body, Encoding.UTF8);
            message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        return message;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        var length = response.Content.Headers.ContentLength;
        if (length.HasValue && length.Value > maxBytes)
        {
            throw new ResponseTooLargeException($"Response of {length.Value} bytes exceeds the limit of {maxBytes} bytes.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var ms = new MemoryStream(length.HasValue ? (int)Math.Min(length.Value, 1 << 20) : 16 * 1024);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > maxBytes)
                {
                    throw new ResponseTooLargeException($"Response exceeds the limit of {maxBytes} bytes.");
                }

                await ms.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return ms.ToArray();
        }
    }
}
