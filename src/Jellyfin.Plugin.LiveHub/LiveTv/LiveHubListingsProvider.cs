using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Channels;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Definitions;
using Jellyfin.Plugin.LiveHub.Locale;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Relay;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Plugin.LiveHub.LiveTv;

/// <summary>
/// EPG of the Live Hub channels: events on the slot channels, XMLTV programs (or filler blocks) on the 24/7 channels.
/// Times are UTC; Jellyfin shows them in each user's time zone.
/// </summary>
public class LiveHubListingsProvider : IListingsProvider
{
    private static readonly string[] NonSport = { "news", "notizie", "cinema", "film", "movie", "music", "musica", "kids", "bambini", "intrattenimento", "entertainment", "documentari", "documentary" };

    private readonly LiveTvLineup _lineup;
    private readonly CatalogService _catalog;
    private readonly DefinitionStore _store;
    private readonly ConfigAccessor _config;
    private readonly IRelayBaseUrl _baseUrl;
    private readonly Images.EventImageService _images;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveHubListingsProvider"/> class.
    /// </summary>
    /// <param name="lineup">Lineup.</param>
    /// <param name="catalog">Catalog.</param>
    /// <param name="store">Definitions (provider names).</param>
    /// <param name="config">Configuration.</param>
    /// <param name="baseUrl">Local base URL.</param>
    /// <param name="images">Images.</param>
    public LiveHubListingsProvider(LiveTvLineup lineup, CatalogService catalog, DefinitionStore store, ConfigAccessor config, IRelayBaseUrl baseUrl, Images.EventImageService images)
    {
        _lineup = lineup;
        _catalog = catalog;
        _store = store;
        _config = config;
        _baseUrl = baseUrl;
        _images = images;
    }

    /// <inheritdoc />
    public string Name => "Live Hub";

    /// <inheritdoc />
    public string Type => LiveTvLineup.TunerType;

    /// <summary>Returns whether a category is a sport.</summary>
    /// <param name="category">The category.</param>
    /// <returns>True for sports.</returns>
    public static bool IsSport(string? category)
        => category is null || !NonSport.Any(n => category.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public Task<IEnumerable<ProgramInfo>> GetProgramsAsync(ListingsProviderInfo info, string channelId, DateTime startDateUtc, DateTime endDateUtc, CancellationToken cancellationToken)
    {
        var lineup = _lineup.Get();
        var start = new DateTimeOffset(DateTime.SpecifyKind(startDateUtc, DateTimeKind.Utc));
        var end = new DateTimeOffset(DateTime.SpecifyKind(endDateUtc, DateTimeKind.Utc));
        var channel = lineup.Channels.Find(c => c.Id == channelId);
        if (channel is null)
        {
            return Task.FromResult(Enumerable.Empty<ProgramInfo>());
        }

        IEnumerable<ProgramInfo> programs = channel.Kind == LineupKind.Slot
            ? SlotPrograms(lineup, channel, start, end)
            : ChannelPrograms(channel, start, end);
        return Task.FromResult(programs);
    }

    /// <inheritdoc />
    public Task Validate(ListingsProviderInfo info, bool validateLogin, bool validateListings) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<List<NameIdPair>> GetLineups(ListingsProviderInfo info, string country, string location)
        => Task.FromResult(new List<NameIdPair> { new() { Name = "Live Hub", Id = LiveTvLineup.TunerType } });

    /// <inheritdoc />
    public Task<List<ChannelInfo>> GetChannels(ListingsProviderInfo info, CancellationToken cancellationToken)
    {
        var baseUrl = _baseUrl.BaseUrl;
        return Task.FromResult(_lineup.Get().Channels.Select(c => LiveHubTunerHost.ToChannelInfo(c, baseUrl)).ToList());
    }

    private IEnumerable<ProgramInfo> SlotPrograms(Lineup lineup, LineupChannel channel, DateTimeOffset start, DateTimeOffset end)
    {
        if (!lineup.Assignments.TryGetValue(channel.Id, out var list))
        {
            yield break;
        }

        var lang = UiLanguage();
        foreach (var a in list.Where(a => a.End > start && a.Start < end))
        {
            var ev = a.Event;
            var streams = _catalog.StreamsOf(ev);
            yield return new ProgramInfo
            {
                Id = "lh-" + ev.Key,
                ChannelId = channel.Id,
                Name = ChannelText.Shorten(ev.Title, 60),
                EpisodeTitle = ev.Competition,
                Overview = Describe(ev, streams, lang),
                ShortOverview = string.Join(" · ", new[] { ev.Competition, ev.Category }.Where(s => !string.IsNullOrEmpty(s)).Distinct()),
                StartDate = a.Start.UtcDateTime,
                EndDate = a.End.UtcDateTime,
                Genres = new List<string> { ev.Category ?? channel.Category ?? SlotAllocator.DefaultCategory },
                IsLive = true,
                IsSports = IsSport(ev.Category),
                IsNews = !IsSport(ev.Category) && (ev.Category ?? string.Empty).Contains("news", StringComparison.OrdinalIgnoreCase),
                IsHD = streams.Exists(s => s.IsHd),
                OfficialRating = ev.ContentRating,
                ImageUrl = _images.ForEvent(ev, Images.ImageShape.Poster),
                ThumbImageUrl = _images.ForEvent(ev, Images.ImageShape.Thumb),
                LogoImageUrl = ev.LogoUrl is null ? null : _images.ForEvent(ev, Images.ImageShape.Logo),
                BackdropImageUrl = _images.ForEvent(ev, Images.ImageShape.Thumb),
                HasImage = true,
                Etag = Etag(ev.Key, ev.Title, a.Start, a.End, streams.Count),
                ShowId = "lh-" + ChannelIds.Short(ev.Category ?? SlotAllocator.DefaultCategory),
            };
        }
    }

    private IEnumerable<ProgramInfo> ChannelPrograms(LineupChannel channel, DateTimeOffset start, DateTimeOffset end)
    {
        var view = _catalog.View;
        var catalogChannel = channel.ChannelKey is null ? null : _catalog.FindChannel(channel.ChannelKey);
        var programs = channel.ChannelKey is not null && view.Programs.TryGetValue(channel.ChannelKey, out var p) ? p : Array.Empty<LiveProgram>();
        var any = false;
        foreach (var prog in programs.Where(x => x.End > start && x.Start < end))
        {
            any = true;
            var category = prog.Category ?? catalogChannel?.Category;
            yield return new ProgramInfo
            {
                Id = "lh-" + prog.Id,
                ChannelId = channel.Id,
                Name = prog.Title,
                EpisodeTitle = prog.SubTitle,
                Overview = prog.Description,
                StartDate = prog.Start.UtcDateTime,
                EndDate = prog.End.UtcDateTime,
                Genres = category is null ? new List<string>() : new List<string> { category },
                IsSports = category is not null && IsSport(category) && !string.IsNullOrEmpty(category),
                IsNews = (category ?? string.Empty).Contains("news", StringComparison.OrdinalIgnoreCase),
                IsMovie = (category ?? string.Empty).Contains("film", StringComparison.OrdinalIgnoreCase) || (category ?? string.Empty).Contains("movie", StringComparison.OrdinalIgnoreCase),
                ImageUrl = _images.Proxy(prog.ImageUrl, catalogChannel?.ProviderId ?? string.Empty),
                HasImage = prog.ImageUrl is not null,
                OfficialRating = catalogChannel?.ContentRating,
                Etag = Etag(prog.Id, prog.Title, prog.Start, prog.End, 0),
            };
        }

        if (any || !_config.Current.Channel247FillerPrograms)
        {
            yield break;
        }

        // No guide data: two-hour blocks so the channel is visible and playable from the guide.
        var block = TimeSpan.FromHours(2);
        var t = new DateTimeOffset(start.UtcTicks - (start.UtcTicks % block.Ticks), TimeSpan.Zero);
        while (t < end)
        {
            yield return new ProgramInfo
            {
                Id = "lh-fill-" + ChannelIds.Short(channel.Id) + "-" + t.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ChannelId = channel.Id,
                Name = channel.Name,
                Overview = catalogChannel?.Description,
                StartDate = t.UtcDateTime,
                EndDate = (t + block).UtcDateTime,
                Genres = channel.Category is null ? new List<string>() : new List<string> { channel.Category },
                IsLive = true,
                IsNews = (channel.Category ?? string.Empty).Contains("news", StringComparison.OrdinalIgnoreCase),
                ImageUrl = channel.LogoUrl,
                HasImage = channel.LogoUrl is not null,
                Etag = "fill",
            };
            t += block;
        }
    }

    private string Describe(LiveEvent ev, IReadOnlyList<LiveStream> streams, string lang)
    {
        var sb = new StringBuilder();
        var head = string.Join(" · ", new[] { ev.Competition, ev.Category }.Where(s => !string.IsNullOrEmpty(s)).Distinct());
        if (head.Length > 0)
        {
            sb.Append(head).Append('\n');
        }

        var providers = ev.SourceProviders.Select(p => _store.Get(p)?.Name ?? p).Select(n => n.Split('·')[0].Trim()).Distinct().ToList();
        if (providers.Count > 0)
        {
            sb.Append(lang == "it" ? "Fonti: " : "Sources: ").Append(string.Join(", ", providers)).Append('\n');
        }

        var languages = streams.Select(s => s.Language).Append(ev.Language).Where(l => l is not null).Distinct().Select(l => LocaleNormalizer.LanguageName(l, lang)).ToList();
        if (languages.Count > 0)
        {
            sb.Append(lang == "it" ? "Lingue: " : "Languages: ").Append(string.Join(", ", languages)).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(ev.Description))
        {
            sb.Append(ev.Description.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    private string UiLanguage()
    {
        var cfg = _config.Current;
        return Localization.LocalizationService.Normalize(cfg.UiLanguage is "it" or "en" ? cfg.UiLanguage : cfg.DefaultLanguage);
    }

    private static string Etag(string key, string title, DateTimeOffset start, DateTimeOffset end, int streams)
        => ChannelIds.Short(key + "|" + title + "|" + start.ToUnixTimeSeconds() + "|" + end.ToUnixTimeSeconds() + "|" + streams);
}
