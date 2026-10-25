using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.LiveHub.Http;

/// <summary>All outgoing HTTP of the plugin goes through this abstraction.</summary>
public interface IOutboundHttp
{
    /// <summary>Sends a request and buffers the response (size limited).</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The response.</returns>
    Task<FetchResponse> FetchAsync(FetchRequest request, CancellationToken cancellationToken);

    /// <summary>Sends a request and returns the body as a stream (relay segments, TS fan-out).</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The streaming response.</returns>
    Task<StreamingResponse> OpenStreamAsync(FetchRequest request, CancellationToken cancellationToken);
}
