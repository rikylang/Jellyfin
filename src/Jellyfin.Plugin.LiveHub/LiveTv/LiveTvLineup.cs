using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.LiveHub.Catalog;
using Jellyfin.Plugin.LiveHub.Configuration;
using Jellyfin.Plugin.LiveHub.Model;
using Jellyfin.Plugin.LiveHub.Storage;
using Jellyfin.Plugin.LiveHub.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveHub.LiveTv;

/// <summary>
/// Computes the Live TV lineup: 24/7 channels with stable numbers and per-category slot channels with their events.
/// The result is cached per catalog revision; slot and numbering state is persisted so channels never move.
/// </summary>
public sealed class LiveTvLineup
{
    /// <summary>Tuner host type / id used by the channels.</summary>
    public const string TunerType = "livehub";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly CatalogService _catalog;
    private readonly UserViewFactory _views;
    private readonly ConfigAccessor _config;
    private readonly LiveHubPaths _paths;
    private readonly ILogger<LiveTvLineup> _logger;
    private readonly Images.EventImageService _images;
    private readonly Lock _lock = new();
    private LineupState? _state;
    private Lineup? _current;
    private string _currentKey = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveTvLineup"/> class.
    /// </summary>
    /// <param name="catalog">Catalog.</param>
    /// <param name="views">User views (anonymous view applies the admin source restrictions).</param>
    /// <param name="config">Configuration.</param>
    /// <param name="paths">Paths.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="images">Images (channel logos through the image proxy).</param>
    public LiveTvLineup(CatalogService catalog, UserViewFactory views, ConfigAccessor config, LiveHubPaths paths, ILogger<LiveTvLineup> logger, Images.EventImageService images)
    {
        _catalog = catalog;
        _views = views;
        _config = config;
        _paths = paths;
        _logger = logger;
        _images = images;
    }

    private string StatePath => Path.Combine(_paths.StateDir, "livetv.json");

    /// <summary>Gets the current lineup (rebuilt when the catalog changes, or every few minutes).</summary>
    /// <returns>The lineup.</returns>
    public Lineup Get()
    {
        var view = _catalog.View;
        var now = _catalog.Clock.GetUtcNow();
        var key = view.DataRevision + "|" + view.DisplayRevision + "|" + (now.ToUnixTimeSeconds() / 300).ToString(CultureInfo.InvariantCulture) + "|" + _config.Current.MaxSlotsPerCategory.ToString(CultureInfo.InvariantCulture);
        lock (_lock)
        {
            if (_current is not null && _currentKey == key)
            {
                return _current;
            }

            _current = Build(view, now);
            _currentKey = key;
            return _current;
        }
    }

    /// <summary>Finds the event to play on a slot channel now: the live one, else the next starting within 30 minutes.</summary>
    /// <param name="channelId">The slot channel id.</param>
    /// <returns>The event or null.</returns>
    public LiveEvent? EventOnSlot(string channelId)
    {
        var lineup = Get();
        if (!lineup.Assignments.TryGetValue(channelId, out var list))
        {
            return null;
        }

        var now = _catalog.Clock.GetUtcNow();
        var current = list.Find(a => a.Start <= now.AddMinutes(15) && a.End > now);
        current ??= list.Find(a => a.Start > now && a.Start <= now.AddMinutes(30));
        return current?.Event;
    }

    private Lineup Build(CatalogView view, DateTimeOffset now)
    {
        var cfg = _config.Current;
        var state = LoadState();
        var anonymous = _views.Create(Guid.Empty);
        var lineup = new Lineup { Revision = view.DataRevision };

        // 24/7 channels: stable numbers (the provider number when free, else base + n).
        var used = new HashSet<int>(state.ChannelNumbers.Values);
        var channels = view.Channels.Where(anonymous.IsVisible).OrderBy(c => c.Key, StringComparer.Ordinal).ToList();

        foreach (var ch in channels.Where(c => !state.ChannelNumbers.ContainsKey(c.Key)))
        {
            var number = int.TryParse(ch.Number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 && n < cfg.SlotChannelNumberBase && !used.Contains(n) ? n : NextFree(used, cfg.ChannelNumberBase247);
            state.ChannelNumbers[ch.Key] = number;
            used.Add(number);
        }

        foreach (var ch in channels)
        {
            var tags = new List<string> { "livehub", "livehub-src-" + ch.ProviderId };
            tags.AddRange(ch.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Take(5));
            lineup.Channels.Add(new LineupChannel
            {
                Id = "c-" + ch.Key,
                Name = ch.Name,
                Number = state.ChannelNumbers[ch.Key].ToString(CultureInfo.InvariantCulture),
                Kind = LineupKind.Channel247,
                Category = ch.Category,
                ChannelKey = ch.Key,
                LogoUrl = ch.LogoUrl is null ? null : _images.ForChannel(ch),
                Tags = tags,
                IsHd = ch.Streams.Exists(s => s.IsHd) ? true : null,
            });
        }

        // Slot channels.
        var events = view.Events.Where(anonymous.IsVisible).ToList();
        var (assignments, categories) = SlotAllocator.Allocate(events, state, now, cfg);
        foreach (var (category, slots) in categories)
        {
            for (var i = 0; i < slots.Slots; i++)
            {
                var id = SlotAllocator.SlotId(category, i);
                lineup.Channels.Add(new LineupChannel
                {
                    Id = id,
                    Name = category + " " + (i + 1).ToString(CultureInfo.InvariantCulture),
                    Number = (cfg.SlotChannelNumberBase + (slots.Order * 10) + i + 1).ToString(CultureInfo.InvariantCulture),
                    Kind = LineupKind.Slot,
                    Category = category,
                    Tags = new List<string> { "livehub", "livehub-slot" },
                });
                lineup.Assignments[id] = assignments.TryGetValue(id, out var list) ? list : new List<SlotAssignment>();
            }
        }

        // Numbers of channels that disappear are kept: if the channel comes back it gets the same number.
        lineup.ChannelSetVersion = Hash(lineup.Channels.Select(c => c.Id + "|" + c.Name + "|" + c.Number));
        lineup.ProgramsVersion = Hash(lineup.Assignments.SelectMany(kv => kv.Value.Select(a => kv.Key + "|" + a.Event.Key + "|" + a.Start.ToUnixTimeSeconds() + "|" + a.End.ToUnixTimeSeconds() + "|" + a.Event.Title))
            .Concat(new[] { view.Programs.Sum(p => p.Value.Count).ToString(CultureInfo.InvariantCulture) }));
        SaveState(state);
        return lineup;
    }

    private static int NextFree(HashSet<int> used, int start)
    {
        var n = start + 1;
        while (used.Contains(n))
        {
            n++;
        }

        return n;
    }

    private static string Hash(IEnumerable<string> parts)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))))[..16];

    private LineupState LoadState()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            if (File.Exists(StatePath))
            {
                var loaded = JsonSerializer.Deserialize<LineupState>(File.ReadAllBytes(StatePath), Json);
                if (loaded is not null)
                {
                    loaded.Categories = new Dictionary<string, CategorySlots>(loaded.Categories, StringComparer.OrdinalIgnoreCase);
                    _state = loaded;
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Live TV state could not be read; channels are renumbered");
        }

        _state = new LineupState();
        return _state;
    }

    private void SaveState(LineupState state)
    {
        try
        {
            var tmp = StatePath + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(state, Json));
            File.Move(tmp, StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Live TV state could not be saved");
        }
    }
}
