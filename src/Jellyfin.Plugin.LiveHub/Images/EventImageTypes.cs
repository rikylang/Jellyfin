using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Engine;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Relay;
using Jellyfin.Plugin.LiveHub.Storage;

namespace Jellyfin.Plugin.LiveHub.Images;

/// <summary>Image format requested.</summary>
public enum ImageShape
{
    /// <summary>16:9 thumbnail (TV cards).</summary>
    Thumb,

    /// <summary>2:3 poster.</summary>
    Poster,

    /// <summary>Square logo.</summary>
    Logo,
}

/// <summary>Text of a generated image.</summary>
public sealed class ImageText
{
    /// <summary>Gets or sets the main title (used when there are no participants).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the participants ("Home", "Away").</summary>
    public IReadOnlyList<string> Participants { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the top line (competition / category).</summary>
    public string? Top { get; set; }

    /// <summary>Gets or sets the bottom line (local time, channel number).</summary>
    public string? Bottom { get; set; }

    /// <summary>Gets or sets the key used to pick the colours (category).</summary>
    public string? ColorKey { get; set; }
}

/// <summary>
/// Chooses the best image of an event/channel for a shape. API images are served through the signed image proxy
/// (downloaded via the provider egress); when the API has none a composite is generated with SkiaSharp and cached.
/// The parameterless constructor (tests) returns the API URLs unchanged and generates nothing.
/// </summary>
public class EventImageService
{
    private readonly IRelayBaseUrl? _baseUrl;
    private readonly ImageProxy? _proxy;
    private readonly SkiaImageRenderer? _renderer;
    private readonly ConfigAccessor? _config;
    private readonly LiveHubPaths? _paths;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventImageService"/> class without proxy or generation.
    /// </summary>
    public EventImageService()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="EventImageService"/> class.
    /// </summary>
    /// <param name="baseUrl">Local base URL.</param>
    /// <param name="proxy">Image proxy.</param>
    /// <param name="renderer">Composite renderer.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="paths">Paths (image cache).</param>
    public EventImageService(IRelayBaseUrl baseUrl, ImageProxy proxy, SkiaImageRenderer renderer, ConfigAccessor config, LiveHubPaths paths)
    {
        _baseUrl = baseUrl;
        _proxy = proxy;
        _renderer = renderer;
        _config = config;
        _paths = paths;
    }

    /// <summary>Builds the text of the composite image of an event.</summary>
    /// <param name="ev">The event.</param>
    /// <returns>The text.</returns>
    public static ImageText TextFor(LiveEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        return new ImageText
        {
            Title = ev.Title,
            Participants = ev.Participants.Count >= 2 ? ev.Participants.Take(2).Select(p => p.Name).ToList() : Array.Empty<string>(),
            Top = ev.Competition ?? ev.Category,
            Bottom = ev.Competition is not null ? ev.Category : null,
            ColorKey = ev.Category ?? ev.Competition,
        };
    }

    /// <summary>Gets the image URL of an event.</summary>
    /// <param name="ev">The event.</param>
    /// <param name="shape">The shape.</param>
    /// <returns>The URL or null.</returns>
    public virtual string? ForEvent(LiveEvent ev, ImageShape shape)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var fromApi = shape switch
        {
            ImageShape.Poster => ev.PosterUrl ?? ev.ThumbUrl,
            ImageShape.Logo => ev.LogoUrl ?? ev.Participants.Select(p => p.LogoUrl).FirstOrDefault(l => l is not null),
            _ => ev.ThumbUrl ?? ev.PosterUrl,
        };
        if (fromApi is not null)
        {
            return Proxied(fromApi, ev.ProviderId);
        }

        if (_baseUrl is not null && _config?.Current.GenerateImages == true)
        {
            var version = ChannelIds.Short(ev.Title + "|" + ev.Competition + "|" + ev.Category);
            return _baseUrl.BaseUrl + "/LiveHub/Images/Event/" + Uri.EscapeDataString(ev.Key) + "/" + shape.ToString().ToLowerInvariant() + ".png?v=" + version;
        }

        return ev.LogoUrl is null ? null : Proxied(ev.LogoUrl, ev.ProviderId);
    }

    /// <summary>Gets the image URL of a 24/7 channel.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The URL or null.</returns>
    public virtual string? ForChannel(LiveChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (channel.LogoUrl is not null)
        {
            return Proxied(channel.LogoUrl, channel.ProviderId);
        }

        return _baseUrl is null ? null : _baseUrl.BaseUrl + "/LiveHub/Images/Channel/c-" + Uri.EscapeDataString(channel.Key) + ".png";
    }

    /// <summary>Renders (or reads from the cache) a composite image.</summary>
    /// <param name="cacheKey">Cache key.</param>
    /// <param name="text">The text.</param>
    /// <param name="shape">The shape.</param>
    /// <returns>PNG bytes or null.</returns>
    public byte[]? Render(string cacheKey, ImageText text, ImageShape shape)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_renderer is null)
        {
            return null;
        }

        string? file = null;
        if (_paths is not null)
        {
            var hash = Keys.Hash(cacheKey + "|" + shape + "|" + text.Title + "|" + string.Join(",", text.Participants) + "|" + text.Top + "|" + text.Bottom);
            file = Path.Combine(_paths.ImagesDir, hash + ".png");
            if (File.Exists(file))
            {
                return File.ReadAllBytes(file);
            }
        }

        var png = _renderer.Render(text, shape);
        if (png is not null && file is not null)
        {
            try
            {
                File.WriteAllBytes(file, png);
            }
            catch (IOException)
            {
                // cache is best effort
            }
        }

        return png;
    }

    /// <summary>Routes a remote image URL through the signed image proxy (unchanged without proxy).</summary>
    /// <param name="url">The URL.</param>
    /// <param name="providerId">The provider whose egress is used.</param>
    /// <returns>The URL to give to Jellyfin.</returns>
    public string? Proxy(string? url, string providerId) => url is null ? null : Proxied(url, providerId);

    private string Proxied(string url, string providerId)
    {
        if (_proxy is null || _baseUrl is null)
        {
            return url;
        }

        var path = _proxy.SignPath(url, providerId);
        return path is null ? url : _baseUrl.BaseUrl + "/" + path;
    }
}
