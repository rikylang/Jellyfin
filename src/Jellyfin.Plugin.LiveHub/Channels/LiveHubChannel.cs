using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Images;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Users;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.Channels;

/// <summary>
/// The "Live Hub" channel (My media): Live now, Today, Upcoming, Popular, By category, By source, Favorites, 24/7.
/// Content is filtered and sorted for the browsing user. Playback sources come from <c>LiveHubMediaSourceProvider</c>.
/// </summary>
public sealed class LiveHubChannel : IChannel, ISupportsLatestMedia, IHasCacheKey
{
    private static readonly string[] RootFolders = { "live", "today", "upcoming", "popular", "categories", "sources", "favorites", "channels" };

    private readonly CatalogService _catalog;
    private readonly UserViewFactory _views;
    private readonly DefinitionStore _store;
    private readonly ChannelText _text;
    private readonly EventImageService _images;
    private readonly ConfigAccessor _config;
    private readonly ILogger<LiveHubChannel> _logger;
    private readonly Lock _cacheLock = new();
    private string? _lastServedUser;
    private int _switchEpoch;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveHubChannel"/> class.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="views">User views.</param>
    /// <param name="store">Definitions.</param>
    /// <param name="text">Text formatting.</param>
    /// <param name="images">Images.</param>
    /// <param name="config">Configuration.</param>
    /// <param name="logger">Logger.</param>
    public LiveHubChannel(CatalogService catalog, UserViewFactory views, DefinitionStore store, ChannelText text, EventImageService images, ConfigAccessor config, ILogger<LiveHubChannel> logger)
    {
        _catalog = catalog;
        _views = views;
        _store = store;
        _text = text;
        _images = images;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => Plugin.PluginName;

    /// <inheritdoc />
    public string Description => "Live events and 24/7 channels from your sources, updated in real time.";

    /// <inheritdoc />
    public string DataVersion => "1";

    /// <inheritdoc />
    public string HomePageUrl => "https://github.com/livehub/jellyfin-plugin-livehub";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Clip },
        SupportsSortOrderToggle = false,
    };

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <summary>
    /// The cache key changes with the catalog display revision, the user's preferences and access rules, and whenever
    /// a different user than the last one is served (folder contents are shared database items in Jellyfin).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The key.</returns>
    public string? GetCacheKey(string? userId)
    {
        var id = ParseUser(userId);
        var key = id.ToString("N", CultureInfo.InvariantCulture);
        int epoch;
        lock (_cacheLock)
        {
            if (!string.Equals(_lastServedUser, key, StringComparison.Ordinal))
            {
                _switchEpoch++;
            }

            epoch = _switchEpoch;
        }

        var prefs = _views.Preferences.Get(id);
        return string.Join('-', _catalog.View.DisplayRevision, key[..8], prefs.Version.ToString(CultureInfo.InvariantCulture), _views.Access.Fingerprint(id), epoch.ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        lock (_cacheLock)
        {
            _lastServedUser = query.UserId.ToString("N", CultureInfo.InvariantCulture);
        }

        try
        {
            var view = _views.Create(query.UserId);
            var items = Build(query.FolderId, view);
            var page = items.Skip(query.StartIndex ?? 0).Take(query.Limit ?? int.MaxValue).ToList();
            return Task.FromResult(new ChannelItemResult { Items = page, TotalRecordCount = items.Count });
        }
        catch (Exception ex)
        {
            // Never break browsing: log and show an empty folder.
            _logger.LogError(ex, "Live Hub channel failed for folder {Folder}", query.FolderId);
            return Task.FromResult(new ChannelItemResult());
        }
    }

    /// <inheritdoc />
    public Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var view = _views.Create(ParseUser(request.UserId));
        var items = Events(view)
            .Where(e => EventStatusCalculator.Bucket(e, view.Now, view.Config) is DisplayBucket.Live or DisplayBucket.Imminent)
            .Take(30)
            .Select(e => EventItem(e, "live", view, NameStyle.DayAndTime));
        return Task.FromResult(items);
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        var name = type == ImageType.Primary || type == ImageType.Logo ? "channel-logo.png" : "channel-thumb.png";
        var stream = typeof(LiveHubChannel).Assembly.GetManifestResourceStream("Jellyfin.Plugin.LiveHub.Images." + name);
        return Task.FromResult(stream is null
            ? new DynamicImageResponse { HasImage = false }
            : new DynamicImageResponse { HasImage = true, Format = ImageFormat.Png, Stream = stream });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop, ImageType.Logo };

    /// <summary>Builds the items of a folder (exposed for tests).</summary>
    /// <param name="folderId">The folder id (null = root).</param>
    /// <param name="view">The user view.</param>
    /// <returns>The items.</returns>
    public List<ChannelItemInfo> Build(string? folderId, UserView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (string.IsNullOrEmpty(folderId))
        {
            return RootFolders.Select(f => FolderItem(ChannelIds.Folder(f), _text.Folder(f, view.UiLanguage), null)).ToList();
        }

        var parts = folderId.Split(ChannelIds.Sep);
        if (parts.Length < 2 || parts[0] != "f")
        {
            return new List<ChannelItemInfo>();
        }

        var cfg = view.Config;
        var now = view.Now;
        var today = view.Local(now).Date;
        var events = Events(view);
        switch (parts[1])
        {
            case "live":
                return events.Where(e => EventStatusCalculator.Status(e, now, cfg) == EventStatus.Live)
                    .Select(e => EventItem(e, "live", view, NameStyle.DayAndTime)).ToList();
            case "today":
                return events.Where(e => EventStatusCalculator.Status(e, now, cfg) == EventStatus.Live || view.Local(e.Start).Date == today)
                    .Select(e => EventItem(e, "today", view, NameStyle.TimeOnly)).ToList();
            case "upcoming" when parts.Length == 2:
                var days = Math.Clamp(cfg.UpcomingDays, 1, 9);
                return Enumerable.Range(1, days)
                    .Where(d => events.Any(e => view.Local(e.Start).Date == today.AddDays(d)))
                    .Select(d => FolderItem(ChannelIds.Folder("upcoming", d.ToString(CultureInfo.InvariantCulture)), _text.DayFolder(d, view), null))
                    .ToList();
            case "upcoming":
                var offset = int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : 1;
                return events.Where(e => view.Local(e.Start).Date == today.AddDays(offset) && EventStatusCalculator.Status(e, now, cfg) != EventStatus.Live)
                    .Select(e => EventItem(e, "up" + offset.ToString(CultureInfo.InvariantCulture), view, NameStyle.TimeOnly)).ToList();
            case "popular":
                return events.Where(e => e.Start <= now.AddHours(24))
                    .OrderByDescending(e => EventStatusCalculator.Status(e, now, cfg) == EventStatus.Live)
                    .ThenByDescending(e => e.Popularity ?? -1)
                    .ThenByDescending(e => e.SourceProviders.Count)
                    .ThenBy(e => e.Start)
                    .Take(Math.Clamp(cfg.PopularCount, 1, 200))
                    .Select(e => EventItem(e, "pop", view, NameStyle.DayAndTime)).ToList();
            case "categories" when parts.Length == 2:
                return Categories(view, events)
                    .Select(c => FolderItem(ChannelIds.Folder("categories", ChannelIds.Short(c)), c, null)).ToList();
            case "categories":
                var category = Categories(view, events).FirstOrDefault(c => ChannelIds.Short(c) == parts[2]);
                if (category is null)
                {
                    return new List<ChannelItemInfo>();
                }

                var tag = "cat" + parts[2];
                return events.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))
                    .Select(e => EventItem(e, tag, view, NameStyle.DayAndTime))
                    .Concat(Channels(view).Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase)).Select(c => ChannelItem(c, tag, view)))
                    .ToList();
            case "sources" when parts.Length == 2:
                return _store.List().Where(d => view.ProviderVisible(d.Id))
                    .Where(d => events.Any(e => e.SourceProviders.Contains(d.Id)) || Channels(view).Any(c => c.ProviderId == d.Id))
                    .Select(d => FolderItem(ChannelIds.Folder("sources", d.Id), d.Name, null)).ToList();
            case "sources":
                var provider = parts[2];
                if (!view.ProviderVisible(provider))
                {
                    return new List<ChannelItemInfo>();
                }

                var srcTag = "src" + provider;
                return events.Where(e => e.SourceProviders.Contains(provider))
                    .Select(e => EventItem(e, srcTag, view, NameStyle.DayAndTime))
                    .Concat(Channels(view).Where(c => c.ProviderId == provider).Select(c => ChannelItem(c, srcTag, view)))
                    .ToList();
            case "favorites":
                return events.Where(view.IsFavorite).Select(e => EventItem(e, "fav", view, NameStyle.DayAndTime))
                    .Concat(Channels(view).Where(c => view.Prefs.FavoriteChannels.Contains(c.Key, StringComparer.Ordinal)).Select(c => ChannelItem(c, "fav", view)))
                    .ToList();
            case "channels" when parts.Length == 2:
                var channels = Channels(view);
                var groups = channels.GroupBy(c => c.Category ?? "TV").ToList();
                if (groups.Count > 1 && channels.Count > 12)
                {
                    return groups.OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                        .Select(g => FolderItem(ChannelIds.Folder("channels", ChannelIds.Short(g.Key)), g.Key, null)).ToList();
                }

                return channels.Select(c => ChannelItem(c, "247", view)).ToList();
            case "channels":
                return Channels(view).Where(c => ChannelIds.Short(c.Category ?? "TV") == parts[2])
                    .Select(c => ChannelItem(c, "247" + parts[2], view)).ToList();
            default:
                return new List<ChannelItemInfo>();
        }
    }

    private static Guid ParseUser(string? userId) => Guid.TryParse(userId, out var g) ? g : Guid.Empty;

    private List<LiveEvent> Events(UserView view)
        => view.Sort(_catalog.View.Events.Where(view.IsVisible).Where(e => EventStatusCalculator.Status(e, view.Now, view.Config) != EventStatus.Ended)).ToList();

    private List<LiveChannel> Channels(UserView view)
        => _catalog.View.Channels.Where(view.IsVisible).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private List<string> Categories(UserView view, IEnumerable<LiveEvent> events)
        => events.Select(e => e.Category).Concat(Channels(view).Select(c => c.Category))
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();

    private IReadOnlyDictionary<string, string> ProviderNames()
        => _store.List().ToDictionary(d => d.Id, d => d.Name, StringComparer.Ordinal);

    private static ChannelItemInfo FolderItem(string id, string name, string? image) => new()
    {
        Id = id,
        Name = name,
        Type = ChannelItemType.Folder,
        FolderType = ChannelFolderType.Container,
        ImageUrl = image,
    };

    private ChannelItemInfo EventItem(LiveEvent ev, string tag, UserView view, NameStyle style)
    {
        var streams = view.VisibleStreams(_catalog.StreamsOf(ev));
        var name = _text.Name(ev, view, style);
        var overview = _text.Overview(ev, streams, view, ProviderNames());
        var image = _images.ForEvent(ev, ImageShape.Thumb);
        var version = Engine.Keys.Hash(name + "\n" + overview + "\n" + image)[..8];
        var end = EventStatusCalculator.EstimatedEnd(ev, view.Config);
        var item = new ChannelItemInfo
        {
            Id = ChannelIds.Event(tag, ev.Key, version),
            Name = name,
            OriginalTitle = ev.Title,
            Overview = overview,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Clip,
            IsLiveStream = true,
            ImageUrl = image,
            OfficialRating = ev.ContentRating,
            PremiereDate = ev.Start.UtcDateTime,
            StartDate = ev.Start.UtcDateTime,
            EndDate = end.UtcDateTime,
            DateCreated = view.Now.UtcDateTime,
            DateModified = view.Now.UtcDateTime,
            CommunityRating = null,
        };
        if (ev.Category is not null)
        {
            item.Genres.Add(ev.Category);
        }

        var names = ProviderNames();
        item.Studios.AddRange(ev.SourceProviders.Where(view.ProviderVisible).Select(p => names.TryGetValue(p, out var n) ? n : p));
        item.Tags.Add("livehub");
        item.Tags.AddRange(ev.SourceProviders.Select(p => "livehub-src-" + p));
        item.Tags.AddRange(ev.Tags);
        item.ProviderIds["LiveHub"] = ev.Key;
        return item;
    }

    private ChannelItemInfo ChannelItem(LiveChannel channel, string tag, UserView view)
    {
        var names = ProviderNames();
        var providerName = names.TryGetValue(channel.ProviderId, out var n) ? n : channel.ProviderId;
        var overview = _text.ChannelOverview(channel, view, providerName);
        var image = _images.ForChannel(channel);
        var version = Engine.Keys.Hash(channel.Name + "\n" + overview + "\n" + image)[..8];
        var item = new ChannelItemInfo
        {
            Id = ChannelIds.Channel(tag, channel.Key, version),
            Name = ChannelText.Shorten(channel.Name),
            Overview = overview,
            Type = ChannelItemType.Media,
            MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Clip,
            IsLiveStream = true,
            ImageUrl = image,
            OfficialRating = channel.ContentRating,
            DateCreated = view.Now.UtcDateTime,
            DateModified = view.Now.UtcDateTime,
        };
        if (channel.Category is not null)
        {
            item.Genres.Add(channel.Category);
        }

        item.Studios.Add(providerName);
        item.Tags.Add("livehub");
        item.Tags.Add("livehub-src-" + channel.ProviderId);
        item.Tags.AddRange(channel.Tags);
        item.ProviderIds["LiveHub"] = channel.Key;
        return item;
    }
}
