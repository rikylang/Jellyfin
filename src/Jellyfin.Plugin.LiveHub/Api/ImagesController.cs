using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Images;
using Jellyfin.Plugin.LiveHub.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveHub.Api;

/// <summary>
/// Images of events and channels: generated composites and remote images relayed through the provider egress.
/// Downloaded by the Jellyfin server itself (anonymous); remote images require a plugin signature.
/// </summary>
[ApiController]
[Route("LiveHub/Images")]
[AllowAnonymous]
public class ImagesController : ControllerBase
{
    private readonly EventImageService _images;
    private readonly ImageProxy _proxy;
    private readonly CatalogService _catalog;
    private readonly LiveTvLineup _lineup;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImagesController"/> class.
    /// </summary>
    /// <param name="images">Image service.</param>
    /// <param name="proxy">Image proxy.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="lineup">Live TV lineup.</param>
    public ImagesController(EventImageService images, ImageProxy proxy, CatalogService catalog, LiveTvLineup lineup)
    {
        _images = images;
        _proxy = proxy;
        _catalog = catalog;
        _lineup = lineup;
    }

    /// <summary>Gets the image of a Live TV channel (slot or 24/7 without logo).</summary>
    /// <param name="id">The channel id.</param>
    /// <returns>A PNG image.</returns>
    [HttpGet("Channel/{id}.png")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Channel([FromRoute] string id)
    {
        var channel = _lineup.Get().Channels.Find(c => c.Id == id);
        var catalogChannel = id.StartsWith("c-", StringComparison.Ordinal) ? _catalog.FindChannel(id[2..]) : null;
        var text = new ImageText
        {
            Title = channel?.Name ?? catalogChannel?.Name ?? "Live Hub",
            Top = channel?.Kind == LineupKind.Slot ? "Live Hub" : catalogChannel?.Category,
            Bottom = channel?.Number,
            ColorKey = channel?.Category ?? catalogChannel?.Category ?? id,
        };
        return Png(_images.Render("channel|" + id, text, ImageShape.Logo));
    }

    /// <summary>Gets the generated image of an event.</summary>
    /// <param name="key">The event key.</param>
    /// <param name="shape">thumb, poster or logo.</param>
    /// <returns>A PNG image.</returns>
    [HttpGet("Event/{key}/{shape}.png")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Event([FromRoute] string key, [FromRoute] string shape)
    {
        var ev = _catalog.FindEvent(key);
        if (ev is null)
        {
            return NotFound();
        }

        var s = Enum.TryParse<ImageShape>(shape, true, out var parsed) ? parsed : ImageShape.Thumb;
        return Png(_images.Render("event|" + ev.Key, EventImageService.TextFor(ev), s));
    }

    /// <summary>Gets a remote image through the provider egress (signed URLs only).</summary>
    /// <param name="u">The URL.</param>
    /// <param name="p">The provider.</param>
    /// <param name="s">The signature.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The image.</returns>
    [HttpGet("Remote")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> Remote([FromQuery] string u, [FromQuery] string? p, [FromQuery] string s, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(u) || !_proxy.Verify(u, p ?? string.Empty, s))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var image = await _proxy.GetAsync(u, p ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (image is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return File(image.Value.Data, image.Value.ContentType);
    }

    private ActionResult Png(byte[]? png)
    {
        Response.Headers.CacheControl = "public, max-age=86400";
        if (png is not null)
        {
            return File(png, "image/png");
        }

        var fallback = typeof(LiveHubChannel).Assembly.GetManifestResourceStream("Jellyfin.Plugin.LiveHub.Images.channel-logo.png");
        return fallback is null ? NotFound() : File(fallback, "image/png");
    }
}
