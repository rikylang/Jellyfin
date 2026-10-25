using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Relay;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Api;

/// <summary>
/// The stream relay read by ffmpeg (and by clients that direct-play). Authentication is the HMAC-signed, expiring token
/// in the path; only URLs registered from origin playlists of that token's session can be fetched.
/// </summary>
[ApiController]
[Route(RelayService.RoutePrefix + "/{token}")]
[AllowAnonymous]
public class RelayController : ControllerBase
{
    private const string HlsType = "application/vnd.apple.mpegurl";

    private readonly RelayService _relay;
    private readonly ILogger<RelayController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelayController"/> class.
    /// </summary>
    /// <param name="relay">The relay.</param>
    /// <param name="logger">Logger.</param>
    public RelayController(RelayService relay, ILogger<RelayController> logger)
    {
        _relay = relay;
        _logger = logger;
    }

    /// <summary>Generic entry point: redirects to the right entry once the stream is resolved.</summary>
    /// <param name="token">The token.</param>
    /// <returns>A redirect.</returns>
    [HttpGet("stream")]
    public Task<ActionResult> Entry([FromRoute] string token)
        => RunAsync(token, async (session, ct) =>
        {
            var mode = await _relay.PrepareAsync(session, ct).ConfigureAwait(false);
            return Redirect(RelayService.EntryFile(mode));
        });

    /// <summary>Gets the master playlist.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The playlist.</returns>
    [HttpGet("master.m3u8")]
    public Task<ActionResult> Master([FromRoute] string token)
        => RunAsync(token, async (session, ct) => Playlist(await _relay.GetMasterAsync(session, ct).ConfigureAwait(false)));

    /// <summary>Gets a media playlist.</summary>
    /// <param name="token">The token.</param>
    /// <param name="slot">The slot.</param>
    /// <returns>The playlist.</returns>
    [HttpGet("p/{slot}.m3u8")]
    public Task<ActionResult> Media([FromRoute] string token, [FromRoute] string slot)
        => RunAsync(token, async (session, ct) => Playlist(await _relay.GetMediaPlaylistAsync(session, slot, ct).ConfigureAwait(false)));

    /// <summary>Gets a segment, key or init section.</summary>
    /// <param name="token">The token.</param>
    /// <param name="name">The relay id, optionally followed by the media extension.</param>
    /// <returns>The data.</returns>
    [HttpGet("s/{name}")]
    public Task<ActionResult> Segment([FromRoute] string token, [FromRoute] string name)
        => RunAsync(token, async (session, ct) =>
        {
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            var id = dot > 0 ? name[..dot] : name;
            var range = Request.Headers.Range.ToString();
            if (!string.IsNullOrEmpty(range))
            {
                var origin = await _relay.OpenSegmentRangeAsync(session, id, range, ct).ConfigureAwait(false);
                await CopyAsync(origin, session, ct).ConfigureAwait(false);
                return new EmptyResult();
            }

            var segment = await _relay.GetSegmentAsync(session, id, ct).ConfigureAwait(false);
            Response.Headers.CacheControl = "no-cache";
            return File(segment.Data, segment.ContentType ?? "video/mp2t");
        });

    /// <summary>Gets the shared MPEG-TS stream.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The stream.</returns>
    [HttpGet("live.ts")]
    public Task<ActionResult> Ts([FromRoute] string token)
        => RunAsync(token, async (session, ct) =>
        {
            var reader = await _relay.SubscribeTsAsync(session, ct).ConfigureAwait(false);
            try
            {
                Response.StatusCode = StatusCodes.Status200OK;
                Response.ContentType = "video/mp2t";
                Response.Headers.CacheControl = "no-cache";
                await foreach (var chunk in reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    await Response.Body.WriteAsync(chunk, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Client went away.
            }
            finally
            {
                session.Fanout?.Unsubscribe(reader);
            }

            return new EmptyResult();
        });

    /// <summary>Gets the progressive file (Range supported).</summary>
    /// <param name="token">The token.</param>
    /// <returns>The file.</returns>
    [HttpGet("file.mp4")]
    public Task<ActionResult> FileStream([FromRoute] string token)
        => RunAsync(token, async (session, ct) =>
        {
            var range = Request.Headers.Range.ToString();
            var origin = await _relay.OpenFileAsync(session, string.IsNullOrEmpty(range) ? null : range, ct).ConfigureAwait(false);
            await CopyAsync(origin, session, ct).ConfigureAwait(false);
            return new EmptyResult();
        });

    /// <summary>Gets the DASH manifest.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The manifest.</returns>
    [HttpGet("manifest.mpd")]
    public Task<ActionResult> Dash([FromRoute] string token)
        => RunAsync(token, async (session, ct) =>
        {
            Response.Headers.CacheControl = "no-cache";
            return Content(await _relay.GetDashManifestAsync(session, ct).ConfigureAwait(false), "application/dash+xml");
        });

    /// <summary>Gets a DASH resource.</summary>
    /// <param name="token">The token.</param>
    /// <param name="id">The prefix id.</param>
    /// <param name="rest">The relative path.</param>
    /// <returns>The data.</returns>
    [HttpGet("d/{id}/{**rest}")]
    public Task<ActionResult> DashResource([FromRoute] string token, [FromRoute] string id, [FromRoute] string rest)
        => RunAsync(token, async (session, ct) =>
        {
            var segment = await _relay.GetPrefixedAsync(session, id, rest, Request.QueryString.HasValue ? Request.QueryString.Value : null, ct).ConfigureAwait(false);
            return File(segment.Data, segment.ContentType ?? "application/octet-stream");
        });

    private ContentResult Playlist(string text)
    {
        Response.Headers.CacheControl = "no-cache, no-store";
        return Content(text, HlsType);
    }

    private async Task CopyAsync(Http.StreamingResponse origin, RelaySession session, CancellationToken ct)
    {
        await using (origin.ConfigureAwait(false))
        {
            Response.StatusCode = origin.StatusCode is 200 or 206 ? origin.StatusCode : StatusCodes.Status502BadGateway;
            if (Response.StatusCode == StatusCodes.Status502BadGateway)
            {
                return;
            }

            Response.ContentType = origin.ContentType ?? "application/octet-stream";
            if (origin.ContentLength is { } length)
            {
                Response.ContentLength = length;
            }

            if (origin.ContentRange is { } contentRange)
            {
                Response.Headers.ContentRange = contentRange;
            }

            Response.Headers.AcceptRanges = origin.Header("Accept-Ranges") ?? "bytes";
            var buffer = new byte[81920];
            int read;
            try
            {
                while ((read = await origin.Stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    session.AddBytes(read);
                    await Response.Body.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Client went away.
            }
        }
    }

    private async Task<ActionResult> RunAsync(string token, Func<RelaySession, CancellationToken, Task<ActionResult>> action)
    {
        if (!_relay.TryAuthorize(token, out var claims))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!_relay.CheckRate(claims.UserId))
        {
            Response.Headers.RetryAfter = "2";
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var session = _relay.GetSession(claims);
        try
        {
            return await action(session, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (RelayException ex)
        {
            _logger.LogDebug("Relay {Title}: {Status} {Message}", session.Title, ex.Status, ex.Message);
            return Response.HasStarted ? new EmptyResult() : StatusCode(ex.Status, ex.Message);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
    }
}
